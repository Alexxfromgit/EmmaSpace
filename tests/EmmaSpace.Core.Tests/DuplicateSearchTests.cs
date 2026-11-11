using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Platform;
using EmmaSpace.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace EmmaSpace.Core.Tests;

/// <summary>Read plans, progress in bytes, the hash cache and the check before a copy is moved to the Trash.</summary>
public partial class DuplicateSearchTests
{
    private const int Big = 300_000; // larger than the head and tail samples together

    /// <summary>Reports synchronously (Progress&lt;T&gt; would post them to the thread pool).</summary>
    private sealed class Collect<T> : IProgress<T>
    {
        public readonly ConcurrentQueue<T> Reports = new();
        public void Report(T value) => Reports.Enqueue(value);
    }

    /// <summary>one, two, three: identical; variant: same size, head and tail, different middle; two small twins.</summary>
    private static TempTree Files(out byte[] content)
    {
        var temp = new TempTree();
        content = new byte[Big];
        new Random(7).NextBytes(content);
        var variant = (byte[])content.Clone();
        variant[Big / 2] ^= 0xFF;
        temp.File("a/one.bin", content);
        temp.File("b/two.bin", content);
        temp.File("c/three.bin", content);
        temp.File("d/variant.bin", variant);
        temp.File("small1.txt", 5000, (byte)'q');
        temp.File("small2.txt", 5000, (byte)'q');
        return temp;
    }

    /// <summary>A cache that remembers the small, just-written test files (the real one waits until a file has settled).</summary>
    private static FileHashCache TestCache(string? path = null) => new(path) { MinCachedSize = 0, SettleTime = TimeSpan.Zero };

    /// <summary>A stamp the cache accepts, for tests of the cache itself.</summary>
    private static FileStamp Stamp(ulong volume = 1, ulong fileId = 2, ulong size = 3, long write = 4, long change = 5, ulong usn = 6) =>
        new(volume, fileId, size, write, change, usn, Trusted: true);

    private static Task<FileTree> ScanAsync(TempTree temp) =>
        new ManagedScanner().ScanAsync(temp.Root, new ScanOptions(), null, TestContext.Current.CancellationToken);

    private static async Task<List<string[]>> FindAsync(FileTree tree, DuplicateOptions options, IProgress<DuplicateProgress>? progress = null)
    {
        var groups = await DuplicateFinder.FindAsync(tree, options, progress, TestContext.Current.CancellationToken);
        return groups.Select(g => g.Nodes.Select(tree.GetName).Order().ToArray()).ToList();
    }

    private static FileStamp StampOf(string path)
    {
        using var handle = File.OpenHandle(path);
        return StorageInfo.GetStamp(handle) ?? throw new InvalidOperationException("The native engine is needed for file stamps.");
    }

    /// <summary>
    /// File stamps come from the native engine, and the cache only remembers trusted ones (on Windows: on a drive
    /// with a change journal). Without them nothing is cached and these tests would pass without testing anything.
    /// </summary>
    private static void RequireTrustedStamps(string path)
    {
        Assert.SkipUnless(NativeEngine.IsAvailable, "File stamps need the native engine.");
        Assert.SkipUnless(StampOf(path).Trusted, "This drive keeps no change journal, so its files are never cached.");
    }

    /// <summary>
    /// Sets a file's last-write time to what .NET reads back (100 ns steps; ext4 keeps nanoseconds), so a
    /// test can later put that exact time back.
    /// </summary>
    private static DateTime RoundWriteTime(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, written);
        return written;
    }

    // Read plans

    [Theory]
    [InlineData(StorageKind.Ssd)]
    [InlineData(StorageKind.Hdd)]
    [InlineData(StorageKind.Network)]
    [InlineData(StorageKind.Unknown)]
    public async Task Every_read_plan_finds_the_same_duplicates(StorageKind storage)
    {
        using var temp = Files(out _);
        using var tree = await ScanAsync(temp);
        var groups = await FindAsync(tree, new DuplicateOptions { MinSize = 1, Storage = storage });
        Assert.Equal([["one.bin", "three.bin", "two.bin"], ["small1.txt", "small2.txt"]], groups);
    }

    [Fact]
    public async Task Without_a_storage_override_the_system_is_asked_and_finds_the_same()
    {
        using var temp = Files(out _);
        using var tree = await ScanAsync(temp);
        var groups = await FindAsync(tree, new DuplicateOptions { MinSize = 1 });
        Assert.Equal([["one.bin", "three.bin", "two.bin"], ["small1.txt", "small2.txt"]], groups);
    }

    [Fact]
    public void A_hard_disk_is_read_folder_by_folder_and_an_ssd_in_any_order()
    {
        string[] paths = ["/b/2", "/a/9", "/b/1", "/a/1"];
        var hdd = DuplicateFinder.ReadPlan.For(StorageKind.Hdd);
        var ssd = DuplicateFinder.ReadPlan.For(StorageKind.Ssd);
        Assert.Equal([3, 1, 2, 0], DuplicateFinder.InReadOrder([0, 1, 2, 3], hdd, i => paths[i]));
        Assert.Equal([0, 1, 2, 3], DuplicateFinder.InReadOrder([0, 1, 2, 3], ssd, i => paths[i]));
        Assert.Equal(1, hdd.FullParallelism);
        Assert.False(DuplicateFinder.ReadPlan.For(StorageKind.Network).UseCache); // server-set times are not trusted
    }

    [Fact]
    public async Task Progress_counts_bytes_and_ends_complete()
    {
        using var temp = Files(out _);
        using var tree = await ScanAsync(temp);
        var progress = new Collect<DuplicateProgress>();
        await FindAsync(tree, new DuplicateOptions { MinSize = 1, Storage = StorageKind.Ssd }, progress);

        var last = progress.Reports.GroupBy(p => p.Stage).ToDictionary(g => g.Key, g => g.Last());
        // Samples: 64 KiB head + 64 KiB tail of the four big files, all of the two small ones.
        var samples = last["Comparing file samples"];
        Assert.Equal(4L * 2 * 64 * 1024 + 2 * 5000, samples.BytesTotal);
        Assert.Equal(samples.BytesTotal, samples.BytesDone);
        Assert.Equal(6, samples.Done);
        // Verify: whole files, only the four big ones whose samples matched.
        var verify = last["Verifying duplicates"];
        Assert.Equal(4L * Big, verify.BytesTotal);
        Assert.Equal(verify.BytesTotal, verify.BytesDone);
        Assert.Equal(4, verify.Total);
        Assert.All(progress.Reports, p => Assert.InRange(p.BytesDone, 0, p.BytesTotal));
    }

    [Fact]
    public async Task A_search_can_be_stopped_while_it_looks_at_the_drives()
    {
        using var temp = Files(out _);
        using var tree = await ScanAsync(temp);
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DuplicateFinder.FindAsync(tree, new DuplicateOptions { MinSize = 1 }, null, stopped.Token));
    }

    // Hash cache with real files

    [Fact]
    public async Task Unchanged_files_are_taken_from_the_cache()
    {
        using var temp = Files(out _);
        RequireTrustedStamps(Path.Combine(temp.Root, "a", "one.bin"));
        using var tree = await ScanAsync(temp);
        var cache = TestCache();
        var options = new DuplicateOptions { MinSize = 1, Storage = StorageKind.Ssd, Cache = cache };
        var first = await FindAsync(tree, options);
        Assert.Equal(0, cache.Hits);
        Assert.True(cache.Count >= 6);

        Assert.Equal(first, await FindAsync(tree, options));
        Assert.Equal(6 + 4, cache.Hits); // every sample and every full hash
    }

    [Fact]
    public async Task A_file_rewritten_with_its_old_size_and_modified_time_is_read_again()
    {
        using var temp = Files(out var content);
        var two = Path.Combine(temp.Root, "b", "two.bin");
        RequireTrustedStamps(two);
        var written = RoundWriteTime(two); // so putting it back below restores it exactly, as such tools do
        var original = StampOf(two);
        using var tree = await ScanAsync(temp);
        var cache = TestCache();
        var options = new DuplicateOptions { MinSize = 1, Storage = StorageKind.Ssd, Cache = cache };
        await FindAsync(tree, options);

        // What VeraCrypt or a tag editor does: same size, last-write time put back.
        Thread.Sleep(50); // past the clock step of the change time
        var changed = (byte[])content.Clone();
        changed[Big / 2] ^= 0x55;
        File.WriteAllBytes(two, changed);
        File.SetLastWriteTimeUtc(two, written);
        var rewritten = StampOf(two);
        Assert.Equal((original.Size, original.WriteTimeNs, original.FileId), (rewritten.Size, rewritten.WriteTimeNs, rewritten.FileId));

        var groups = await FindAsync(tree, options);
        Assert.Contains(["one.bin", "three.bin"], groups);
        Assert.DoesNotContain(groups, g => g.Contains("two.bin"));
    }

    [Fact]
    public async Task A_file_replaced_by_another_one_is_read_again()
    {
        using var temp = Files(out var content);
        var two = Path.Combine(temp.Root, "b", "two.bin");
        RequireTrustedStamps(two);
        using var tree = await ScanAsync(temp);
        var cache = TestCache();
        var options = new DuplicateOptions { MinSize = 1, Storage = StorageKind.Ssd, Cache = cache };
        await FindAsync(tree, options);

        // A different file (new file ID) moved into its place, with the old file's times.
        var written = File.GetLastWriteTimeUtc(two);
        var changed = (byte[])content.Clone();
        changed[Big / 2] ^= 0x55;
        var replacement = temp.File("replacement.bin", changed);
        File.SetLastWriteTimeUtc(replacement, written);
        File.Move(replacement, two, overwrite: true);

        var groups = await FindAsync(tree, options);
        Assert.DoesNotContain(groups, g => g.Contains("two.bin"));
    }

    [Fact]
    public async Task Files_that_just_changed_are_not_remembered()
    {
        using var temp = Files(out _);
        RequireTrustedStamps(Path.Combine(temp.Root, "a", "one.bin"));
        using var tree = await ScanAsync(temp);
        var cache = new FileHashCache { MinCachedSize = 0 }; // the real settle time: these files were written just now
        await FindAsync(tree, new DuplicateOptions { MinSize = 1, Storage = StorageKind.Ssd, Cache = cache });
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Small_files_are_not_remembered()
    {
        using var temp = Files(out _);
        RequireTrustedStamps(Path.Combine(temp.Root, "a", "one.bin"));
        using var tree = await ScanAsync(temp);
        var cache = new FileHashCache { SettleTime = TimeSpan.Zero }; // the real 1 MB minimum
        await FindAsync(tree, new DuplicateOptions { MinSize = 1, Storage = StorageKind.Ssd, Cache = cache });
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task The_cache_survives_saving_and_loading()
    {
        using var temp = Files(out _);
        RequireTrustedStamps(Path.Combine(temp.Root, "a", "one.bin"));
        using var tree = await ScanAsync(temp);
        var path = Path.Combine(temp.Root, "cache", "hash-cache.bin");
        var cache = TestCache(path);
        await FindAsync(tree, new DuplicateOptions { MinSize = 1, Storage = StorageKind.Ssd, Cache = cache });
        cache.Save();
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // no temporary file left behind

        var loaded = FileHashCache.Load(path);
        Assert.Equal(cache.Count, loaded.Count);
        var one = Path.Combine(temp.Root, "a", "one.bin");
        Assert.True(loaded.TryGet(one, StampOf(one), full: true, out var full));
        Assert.True(cache.TryGet(one, StampOf(one), full: true, out var expected));
        Assert.Equal(expected, full);

        loaded.Clear();
        Assert.Equal(0, loaded.Count);
        Assert.False(File.Exists(path));
    }

    // Hash cache rules

    [Fact]
    public void A_cached_hash_needs_every_part_of_the_stamp_to_match()
    {
        var cache = TestCache();
        var stamp = Stamp();
        cache.Set("/a", stamp, FileHashCache.NowNs(), full: true, 42u, cache.Generation);
        Assert.True(cache.TryGet("/a", stamp, full: true, out var hash));
        Assert.Equal((UInt128)42, hash);
        Assert.False(cache.TryGet("/a", stamp, full: false, out _)); // only the full hash is known
        Assert.False(cache.TryGet("/b", stamp, full: true, out _));
        Assert.False(cache.TryGet("/a", stamp with { Volume = 9 }, full: true, out _)); // another disk at that path
        Assert.False(cache.TryGet("/a", stamp with { FileId = 9u }, full: true, out _)); // another file at that path
        Assert.False(cache.TryGet("/a", stamp with { Size = 9 }, full: true, out _));
        Assert.False(cache.TryGet("/a", stamp with { WriteTimeNs = 9 }, full: true, out _));
        Assert.False(cache.TryGet("/a", stamp with { ChangeTimeNs = 9 }, full: true, out _));
        Assert.False(cache.TryGet("/a", stamp with { Usn = 9 }, full: true, out _)); // written, times put back (Windows)
        Assert.False(cache.TryGet("/a", stamp with { Trusted = false }, full: true, out _));
    }

    [Fact]
    public void Untrusted_stamps_and_file_systems_without_change_times_are_never_remembered()
    {
        var cache = TestCache();
        cache.Set("/untrusted", Stamp() with { Trusted = false }, FileHashCache.NowNs(), full: true, 1u, cache.Generation);
        cache.Set("/no-change-time", Stamp(change: 0), FileHashCache.NowNs(), full: true, 1u, cache.Generation);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void A_file_must_have_settled_for_a_minute_when_its_stamp_was_taken()
    {
        var cache = new FileHashCache { MinCachedSize = 0 }; // the real settle time
        var now = FileHashCache.NowNs();
        long Seconds(int s) => s * 1_000_000_000L;
        cache.Set("/recent", Stamp(write: now - Seconds(30), change: now - Seconds(30)), now, full: true, 1u, cache.Generation);
        // Old now, but only 20 s old when its stamp was taken, at the start of a long read.
        cache.Set("/late", Stamp(write: now - Seconds(120), change: now - Seconds(120)), now - Seconds(100), full: true, 1u, cache.Generation);
        Assert.Equal(0, cache.Count);

        cache.Set("/settled", Stamp(write: now - Seconds(120), change: now - Seconds(90)), now, full: true, 1u, cache.Generation);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Hashes_from_a_search_that_was_running_during_Clear_are_not_kept()
    {
        var cache = TestCache();
        var stamp = Stamp();
        var before = cache.Generation;
        cache.Set("/a", stamp, FileHashCache.NowNs(), full: false, 42u, before);
        Assert.True(cache.TryGet("/a", stamp, full: false, out _));

        cache.Clear();
        cache.Set("/b", stamp, FileHashCache.NowNs(), full: false, 43u, before); // the search started before the Clear
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("/a", stamp, full: false, out _));

        cache.Set("/c", stamp, FileHashCache.NowNs(), full: false, 44u, cache.Generation);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void A_damaged_cache_file_is_ignored()
    {
        using var temp = new TempTree();
        var path = Path.Combine(temp.Root, "hash-cache.bin");
        var cache = TestCache(path);
        for (var i = 1; i <= 1000; i++)
            cache.Set($"/file{i}", Stamp(fileId: (ulong)i, size: (ulong)i, write: i, change: i), FileHashCache.NowNs(), full: false, (UInt128)i, 0);
        cache.Save();
        Assert.Equal(1000, FileHashCache.Load(path).Count);

        // Each damage below is sure to change the entries or their checksum. Flipping a bit of the
        // compressed stream is not: Brotli has redundant encodings, so some flips decode to the very
        // same entries, which the checksum then rightly accepts. Which bits those are depends on the
        // entry order, which UInt128's per-process hash seed changes, so such a test fails now and then.
        var saved = File.ReadAllBytes(path);
        var header = saved[..CacheHeaderSize];
        var entries = Decompress(saved[CacheHeaderSize..]);
        Assert.Equal(BitConverter.ToInt32(saved, 16) * BitConverter.ToInt32(saved, 20), entries.Length); // entry size × count: the layout is right

        var changed = (byte[])entries.Clone();
        changed[entries.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, [.. header, .. Compress(changed)]); // well-formed stream, wrong entries
        Assert.Equal(0, FileHashCache.Load(path).Count);

        var wrongChecksum = (byte[])saved.Clone();
        wrongChecksum[CacheHeaderSize - 1] ^= 0x01;
        File.WriteAllBytes(path, wrongChecksum);
        Assert.Equal(0, FileHashCache.Load(path).Count);

        File.WriteAllBytes(path, saved[..(CacheHeaderSize + (saved.Length - CacheHeaderSize) / 2)]); // cut off
        Assert.Equal(0, FileHashCache.Load(path).Count);

        File.WriteAllBytes(path, [.. header, .. "not Brotli"u8]);
        Assert.Equal(0, FileHashCache.Load(path).Count);

        File.WriteAllBytes(path, saved);
        Assert.Equal(1000, FileHashCache.Load(path).Count); // only the damage was refused

        File.WriteAllBytes(path, "not a cache"u8.ToArray());
        Assert.Equal(0, FileHashCache.Load(path).Count);
        Assert.Equal(0, FileHashCache.Load(Path.Combine(temp.Root, "missing.bin")).Count);
    }

    /// <summary>Magic (8 bytes), format version, sample size, entry size and count (4 each), checksum (16); then the Brotli stream.</summary>
    private const int CacheHeaderSize = 8 + 4 * 4 + 16;

    private static byte[] Decompress(byte[] compressed)
    {
        using var brotli = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(data);
        return output.ToArray();
    }

    [Fact]
    public void A_full_cache_keeps_taking_new_files_and_drops_the_least_recently_used_when_saved()
    {
        var cache = TestCache();
        var now = FileHashCache.NowNs();
        for (var i = 0; i < FileHashCache.MaxEntries + 10; i++)
            cache.Set($"/f{i}", Stamp(fileId: (ulong)i), now, full: false, (UInt128)i, 0);
        Assert.Equal(FileHashCache.MaxEntries + 10, cache.Count); // still accepted between saves

        using var stream = new MemoryStream();
        cache.Write(stream);
        Assert.Equal(FileHashCache.MaxEntries, cache.Count); // trimmed in memory too
        stream.Position = 0;
        var loaded = new FileHashCache();
        loaded.Read(stream);
        Assert.Equal(FileHashCache.MaxEntries, loaded.Count);
    }

    // File stamps and storage

    [Fact]
    public void The_change_time_moves_even_when_the_modified_time_is_put_back()
    {
        Assert.SkipUnless(NativeEngine.IsAvailable, "File stamps need the native engine.");
        using var temp = new TempTree();
        var path = temp.File("x.bin", 1000);
        var written = RoundWriteTime(path);
        var before = StampOf(path);
        Assert.Equal(1000UL, before.Size);
        Assert.NotEqual(0, before.ChangeTimeNs);
        Thread.Sleep(50);

        File.WriteAllBytes(path, new byte[1000]);
        File.SetLastWriteTimeUtc(path, written);
        var after = StampOf(path);
        Assert.Equal(before.WriteTimeNs, after.WriteTimeNs);
        Assert.Equal(before.FileId, after.FileId);
        Assert.NotEqual(before.ChangeTimeNs, after.ChangeTimeNs);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileBasicInfo info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, in FileBasicInfo info, uint size);

    [Fact]
    public void On_Windows_a_write_is_caught_even_when_every_time_is_put_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && NativeEngine.IsAvailable, "Windows only: programs can set ChangeTime back there.");
        using var temp = new TempTree();
        var path = temp.File("container.bin", 2 * 1024 * 1024);
        var before = StampOf(path);
        Assert.SkipUnless(before.Usn != 0, "This drive keeps no change journal.");
        var size = (uint)Marshal.SizeOf<FileBasicInfo>();
        FileBasicInfo times;
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite))
            Assert.True(GetFileInformationByHandleEx(handle, 0, out times, size)); // FileBasicInfo

        Thread.Sleep(50);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 1024 * 1024;
            stream.WriteByte(0x55);
        }
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite))
            Assert.True(SetFileInformationByHandle(handle, 0, times, size)); // every time back, ChangeTime included

        var after = StampOf(path);
        Assert.Equal((before.Size, before.WriteTimeNs, before.ChangeTimeNs), (after.Size, after.WriteTimeNs, after.ChangeTimeNs));
        Assert.NotEqual(before.Usn, after.Usn);

        var cache = TestCache();
        cache.Set(path, before, FileHashCache.NowNs(), full: true, 1u, cache.Generation);
        Assert.False(cache.TryGet(path, after, full: true, out _));
    }

    [Fact]
    public void Storage_of_a_folder_is_a_known_kind_on_one_device()
    {
        Assert.SkipUnless(NativeEngine.IsAvailable, "Storage kinds need the native engine.");
        using var temp = new TempTree();
        var root = StorageInfo.Get(temp.Root);
        Assert.True(Enum.IsDefined(root.Kind));
        Assert.Equal(root, StorageInfo.Get(temp.Folder("sub")));
        Assert.Equal(StorageKind.Unknown, StorageInfo.GetKind(""));
    }

    // The check before a copy is moved to the Trash

    /// <summary>A comparison is trusted only with the file stamps of the native engine.</summary>
    private static void RequireComparableFiles() =>
        Assert.SkipUnless(DuplicateFinder.CanCheckCopies, "File stamps need the native engine.");

    [Fact]
    public async Task Copies_are_compared_byte_by_byte()
    {
        RequireComparableFiles();
        using var temp = Files(out var content);
        var ct = TestContext.Current.CancellationToken;
        var one = Path.Combine(temp.Root, "a", "one.bin");
        Assert.True(await DuplicateFinder.AreIdenticalAsync(one, Path.Combine(temp.Root, "b", "two.bin"), Big, ct));
        Assert.False(await DuplicateFinder.AreIdenticalAsync(one, Path.Combine(temp.Root, "d", "variant.bin"), Big, ct));
        Assert.False(await DuplicateFinder.AreIdenticalAsync(one, Path.Combine(temp.Root, "missing.bin"), Big, ct));
        Assert.False(await DuplicateFinder.AreIdenticalAsync(one, one, Big + 1, ct)); // not the size they were found with

        var longer = temp.File("longer.bin", [.. content, 0]);
        Assert.False(await DuplicateFinder.AreIdenticalAsync(one, longer, Big, ct));
    }

    [Fact]
    public async Task The_identical_copy_is_found_and_copies_in_the_bin_do_not_count()
    {
        RequireComparableFiles();
        using var temp = Files(out var content);
        var ct = TestContext.Current.CancellationToken;
        var one = Path.Combine(temp.Root, "a", "one.bin");
        var inBin = temp.File(Path.Join("$Recycle.Bin", "S-1-5-21", "$R1.bin"), content);
        var variant = Path.Combine(temp.Root, "d", "variant.bin");
        var two = Path.Combine(temp.Root, "b", "two.bin");

        Assert.Null((await DuplicateFinder.FindIdenticalCopyAsync(one, Big, [inBin, variant], null, ct)).Match);
        var progress = new Collect<double>();
        var found = await DuplicateFinder.FindIdenticalCopyAsync(one, Big, [inBin, variant, two], progress, ct);
        Assert.Equal(two, found.Match);
        Assert.False(found.InUse);
        Assert.True(DuplicateFinder.IsStillTheMatch(two, found.MatchStamp));
        Assert.True(DuplicateFinder.IsStillTheMatch(one, found.TargetStamp));
        Assert.All(progress.Reports, p => Assert.InRange(p, 0, 1));
    }

    [Fact]
    public async Task A_copy_written_to_while_it_is_compared_is_not_identical()
    {
        RequireComparableFiles();
        using var temp = Files(out _);
        var ct = TestContext.Current.CancellationToken;
        var one = Path.Combine(temp.Root, "a", "one.bin");
        var two = Path.Combine(temp.Root, "b", "two.bin");
        // No pause after writing the files: the check itself waits until their last change is old enough for any
        // write to move the change time on (Linux keeps it in steps of a clock tick).
        var written = false;
        var identical = await DuplicateFinder.AreIdenticalAsync(one, two, Big, ct, _ =>
        {
            if (written) return;
            try
            {
                // Same size, and in the part already compared, so only the stamp can tell.
                using var writer = new FileStream(two, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer.WriteByte(0x5A);
                written = true;
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                // Windows kept the writer out while the files were compared: they really are identical.
            }
        });

        Assert.Equal(!written, identical);
        if (!OperatingSystem.IsWindows()) Assert.True(written);
    }

    [Fact]
    public async Task A_different_file_moved_in_place_of_the_match_is_not_taken_for_it()
    {
        RequireComparableFiles();
        using var temp = Files(out var content);
        var ct = TestContext.Current.CancellationToken;
        var one = Path.Combine(temp.Root, "a", "one.bin");
        var two = Path.Combine(temp.Root, "b", "two.bin");
        var found = await DuplicateFinder.FindIdenticalCopyAsync(one, Big, [two], null, ct);
        Assert.Equal(two, found.Match);

        // Same name and content, but another file: the one that was compared is gone.
        var other = temp.File("other.bin", content);
        File.Move(other, two, overwrite: true);
        Assert.False(DuplicateFinder.IsStillTheMatch(two, found.MatchStamp));
        Assert.False(DuplicateFinder.IsStillTheMatch(Path.Combine(temp.Root, "missing.bin"), found.MatchStamp));
        Assert.False(DuplicateFinder.IsStillTheMatch(two, stamp: null));
    }

    [Fact]
    public async Task The_file_to_move_must_still_be_the_one_compared()
    {
        RequireComparableFiles();
        using var temp = Files(out var content);
        var ct = TestContext.Current.CancellationToken;
        var one = Path.Combine(temp.Root, "a", "one.bin");
        var two = Path.Combine(temp.Root, "b", "two.bin");
        var found = await DuplicateFinder.FindIdenticalCopyAsync(one, Big, [two], null, ct);
        Assert.Equal(two, found.Match);
        Assert.True(DuplicateFinder.IsStillTheMatch(one, found.TargetStamp));

        // Rewritten after the check with the same size: no longer the file that was found identical.
        using (var writer = new FileStream(one, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            writer.WriteByte((byte)(content[0] ^ 0xFF));
        Assert.Equal(Big, new FileInfo(one).Length);
        Assert.False(DuplicateFinder.IsStillTheMatch(one, found.TargetStamp));
        Assert.True(DuplicateFinder.IsStillTheMatch(two, found.MatchStamp));
    }

    [Fact]
    public async Task The_check_can_be_stopped()
    {
        using var temp = Files(out _);
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DuplicateFinder.FindIdenticalCopyAsync(
            Path.Combine(temp.Root, "a", "one.bin"), Big, [Path.Combine(temp.Root, "b", "two.bin")], null, stopped.Token));
    }

    [Theory]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21-1\$RABC.bin", true)]
    [InlineData(@"D:\$RECYCLE.BIN\S-1-5-21-1\file", true)]
    [InlineData(@"E:\RECYCLER\file", true)]
    [InlineData("/Users/emma/.Trash/file", true)]
    [InlineData("/Volumes/USB/.Trashes/501/file", true)]
    [InlineData("/home/emma/.local/share/Trash/files/file", true)]
    [InlineData("/media/emma/disk/.Trash-1000/files/file", true)]
    [InlineData("/media/emma/disk/.Trash/1000/files/file", true)]
    [InlineData(@"C:\Users\emma\Documents\Recycle Bin notes.txt", false)]
    [InlineData("/home/emma/Trash/file", false)]
    [InlineData("/home/emma/.Trashy/file", false)]
    [InlineData("/media/emma/disk/.Trash-backup/file", false)]
    [InlineData("/media/emma/disk/.Trash-/file", false)]
    [InlineData("/media/emma/disk/.Trash-1000x/file", false)]
    public void Recycle_bins_and_trash_folders_are_recognized(string path, bool inTrash) =>
        Assert.Equal(inTrash, TrashFolders.Contains(path, dataHome: null));

    [Theory]
    [InlineData("/home/emma/data/Trash/files/a.bin", "/home/emma/data", true)]
    [InlineData("/home/emma/data/Trash/files/a.bin", "/home/emma/data/", true)]
    [InlineData("/home/emma/data/Trashy/a.bin", "/home/emma/data", false)]
    [InlineData("/home/emma/data/Trash/files/a.bin", "data", false)] // relative: ignored, as the specification says
    [InlineData("/home/emma/.local/share/Trash/files/a.bin", "/home/emma/data", true)]
    public void The_home_trash_follows_XDG_DATA_HOME(string path, string dataHome, bool inTrash) =>
        Assert.Equal(inTrash, TrashFolders.Contains(path, dataHome));
}
