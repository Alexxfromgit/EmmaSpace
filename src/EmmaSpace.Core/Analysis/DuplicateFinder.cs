using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace EmmaSpace.Core.Analysis;

public sealed record DuplicateOptions
{
    public ulong MinSize { get; init; } = 1_000_000; // "1 MB" in either unit system, like FileHashCache.MinCachedSize

    /// <summary>The storage the files are on; null asks the system, per file system the tree spans.</summary>
    public StorageKind? Storage { get; init; }

    /// <summary>Hashes from earlier searches, used for files that have not changed since; null reads every file.</summary>
    public FileHashCache? Cache { get; init; }
}

/// <param name="HolderCount">
/// Members that keep a copy of the data on disk themselves (<see cref="CountHolders"/>); all of them when null.
/// </param>
public sealed record DuplicateGroup(ulong FileSize, IReadOnlyList<int> Nodes, int? HolderCount = null)
{
    public ulong WastedBytes => Reclaimable(FileSize, HolderCount ?? Nodes.Count);

    /// <summary>What deleting all but one copy would free: every copy kept on disk, except the one that stays.</summary>
    public static ulong Reclaimable(ulong fileSize, int holders) => fileSize * (ulong)Math.Max(holders - 1, 0);

    /// <summary>
    /// How many of these identical files keep their own copy of the data on disk. Pure APFS clones of one file share
    /// it: the engine counts it on the first one found and flags the others <see cref="NodeFlags.CloneShared"/>. Every
    /// other file holds a copy, including clones that were changed since and the first clone of each family.
    /// </summary>
    /// <param name="removedCloneHolders">
    /// First clones of a family that were removed from the tree since the scan. Their family's other clones still hold
    /// the data, so up to that many of the remaining shared clones are counted again. Which family a clone belongs to
    /// is not known, so this is exact for one family per group and an estimate beyond it. Clones changed since they were
    /// cloned are not holders in this sense: they keep their own copy and are counted like plain files.
    /// </param>
    public static int CountHolders(FileTree tree, IEnumerable<int> nodes, int removedCloneHolders = 0)
    {
        int holders = 0, shared = 0;
        foreach (var node in nodes)
        {
            if ((tree.Nodes[node].Flags & NodeFlags.CloneShared) != 0) shared++;
            else holders++;
        }
        return holders + Math.Min(Math.Max(removedCloneHolders, 0), shared);
    }

    /// <summary>The first clone of a family, which holds the data its <see cref="NodeFlags.CloneShared"/> clones share.</summary>
    public static bool IsCloneHolder(NodeFlags flags) => (flags & NodeFlags.CloneHolder) != 0;
}

/// <summary>A completed duplicate search: the groups it found and the smallest file size it looked at.</summary>
public sealed record DuplicateResult(ulong MinSize, IReadOnlyList<DuplicateGroup> Groups);

/// <summary>Files done of the files to read in this stage, and the bytes read (or found in the cache) of the bytes to read.</summary>
public readonly record struct DuplicateProgress(string Stage, long Done, long Total, long BytesDone = 0, long BytesTotal = 0);

/// <summary>
/// What <see cref="DuplicateFinder.FindIdenticalCopyAsync"/> found: the identical copy, or none, with the stamps the
/// copy and the file being checked had at the end of the comparison, and whether a copy could not be compared because
/// another program has it open for writing. Both files must still have those stamps when the file is moved (see
/// <see cref="DuplicateFinder.IsStillTheMatch"/>).
/// </summary>
public sealed record IdenticalCopyCheck(string? Match, FileStamp? MatchStamp, FileStamp? TargetStamp, bool InUse);

/// <summary>
/// Finds identical files in three passes so most files are never read in full:
/// group by size → hash the first and last 64 KiB → hash the whole file.
/// How many files are read at once depends on the storage: many on an SSD, few on a hard disk
/// (where parallel reads make the heads jump back and forth) and in folder order there. A tree that
/// spans several file systems (a disk mounted inside a scanned folder) reads each one with its own plan.
/// </summary>
public static class DuplicateFinder
{
    internal const int SampleSize = 64 * 1024;
    private const int FullBufferSize = 1024 * 1024;
    private const long ProgressIntervalMs = 100;

    /// <summary>
    /// Entries never compared: links are not followed, cloud placeholders would be downloaded by opening
    /// them, and unreadable entries have no reliable size. Saved results are held to the same rule.
    /// </summary>
    internal const NodeFlags SkippedFlags = NodeFlags.Reparse | NodeFlags.Error | NodeFlags.HardLink | NodeFlags.Cloud;

    /// <summary>How many files to read at once in each pass, whether in folder order, and whether to use the cache.</summary>
    internal readonly record struct ReadPlan(int SampleParallelism, int FullParallelism, bool FolderOrder, bool UseCache)
    {
        public static ReadPlan For(StorageKind kind) => kind switch
        {
            StorageKind.Ssd => new(32, 8, false, true),
            // A few small reads queued at once let the disk reorder them (NCQ); whole files one at a time.
            StorageKind.Hdd => new(4, 1, true, true),
            // File times on a share come from the server, at its resolution: not trusted for the cache.
            StorageKind.Network => new(4, 4, false, false),
            _ => new(8, 4, false, true),
        };
    }

    public static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(FileTree tree, DuplicateOptions options,
        IProgress<DuplicateProgress>? progress, CancellationToken ct, int scope = FileTree.Root)
    {
        // Pass 1: same size (cheap, from the scan data).
        var bySize = new Dictionary<ulong, List<int>>();
        foreach (var node in TreeWalk.Files(tree, scope))
        {
            ref readonly var n = ref tree.Nodes[node];
            if (n.Size < Math.Max(1, options.MinSize) || (n.Flags & SkippedFlags) != 0) continue;
            if (!bySize.TryGetValue(n.Size, out var list)) bySize[n.Size] = list = [];
            list.Add(node);
        }
        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        if (candidates.Count == 0) return [];

        var paths = new ConcurrentDictionary<int, string>();
        string PathOf(int node) => paths.GetOrAdd(node, tree.GetPath);
        var locations = Locate(tree, candidates, options.Storage, progress, ct);
        var reader = new GroupHasher(tree, PathOf, locations, options.Cache, progress);

        // Pass 2: sample hash of head and tail.
        var sampleGroups = await reader.HashGroupsAsync(candidates, full: false, "Comparing file samples", ct);

        // Pass 3: full hash, only where the sample did not already cover the whole file.
        var needFull = sampleGroups.Where(g => tree.Nodes[g[0]].Size > 2 * SampleSize).ToList();
        var complete = sampleGroups.Where(g => tree.Nodes[g[0]].Size <= 2 * SampleSize).ToList();
        var fullGroups = await reader.HashGroupsAsync(needFull, full: true, "Verifying duplicates", ct);

        return complete.Concat(fullGroups)
            .Select(g => new DuplicateGroup(tree.Nodes[g[0]].Size, g, DuplicateGroup.CountHolders(tree, g)))
            .OrderByDescending(g => g.WastedBytes)
            .ToList();
    }

    /// <summary>
    /// Where each candidate is stored. On Windows a scan never enters another volume (mount points are not
    /// followed), so the root tells; elsewhere a disk can be mounted anywhere below it, so every folder that
    /// holds candidates is asked (with progress, and stopped by <paramref name="ct"/>).
    /// </summary>
    private static Func<int, StorageLocation> Locate(FileTree tree, List<List<int>> candidates, StorageKind? storage,
        IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        if (storage is { } kind) return _ => new StorageLocation(kind, 0);
        if (OperatingSystem.IsWindows())
        {
            var root = StorageInfo.Get(tree.RootPath);
            return _ => root;
        }
        const string stage = "Looking at the drives";
        var folders = candidates.SelectMany(g => g).Select(tree.GetParent).Distinct().ToList();
        var byFolder = new Dictionary<int, StorageLocation>(folders.Count);
        var lastReport = Environment.TickCount64;
        progress?.Report(new DuplicateProgress(stage, 0, folders.Count));
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            byFolder[folder] = StorageInfo.Get(tree.GetPath(folder));
            if (Environment.TickCount64 - lastReport < ProgressIntervalMs) continue;
            lastReport = Environment.TickCount64;
            progress?.Report(new DuplicateProgress(stage, byFolder.Count, folders.Count));
        }
        return node => byFolder[tree.GetParent(node)];
    }

    /// <summary>On a hard disk files of one folder are usually stored near each other, so they are read folder by folder.</summary>
    internal static IEnumerable<int> InReadOrder(IEnumerable<int> files, ReadPlan plan, Func<int, string> pathOf) =>
        plan.FolderOrder ? files.OrderBy(pathOf, StringComparer.Ordinal) : files;

    /// <summary>Bytes a pass reads of a file: its head and tail (at most twice the sample size), or all of it.</summary>
    private static long BytesToRead(ulong size, bool full) => full ? (long)size : (long)Math.Min(size, 2UL * SampleSize);

    private sealed class GroupHasher(FileTree tree, Func<int, string> pathOf, Func<int, StorageLocation> locate, FileHashCache? cache,
        IProgress<DuplicateProgress>? progress)
    {
        // Taken once: hashes from a search that was running when the cache was cleared are not remembered.
        private readonly int _generation = cache?.Generation ?? 0;

        public async Task<List<List<int>>> HashGroupsAsync(List<List<int>> groups, bool full, string stage, CancellationToken ct)
        {
            var files = groups.SelectMany(g => g).ToList();
            var total = (long)files.Count;
            var bytesTotal = files.Sum(n => BytesToRead(tree.Nodes[n].Size, full));
            long done = 0, bytesDone = 0, lastReport = Environment.TickCount64;
            var hashes = new ConcurrentDictionary<int, UInt128>();
            progress?.Report(new DuplicateProgress(stage, 0, total, 0, bytesTotal));

            void Report()
            {
                if (progress is null) return;
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref lastReport);
                if (now - last < ProgressIntervalMs || Interlocked.CompareExchange(ref lastReport, now, last) != last) return;
                progress.Report(new DuplicateProgress(stage, Interlocked.Read(ref done), total, Interlocked.Read(ref bytesDone), bytesTotal));
            }

            async ValueTask HashOne(int node, FileHashCache? useCache, CancellationToken token)
            {
                var size = tree.Nodes[node].Size;
                var budget = BytesToRead(size, full);
                long counted = 0;
                void OnRead(long bytes)
                {
                    var add = Math.Min(bytes, budget - counted); // a file that grew since the scan counts as scanned
                    if (add <= 0) return;
                    counted += add;
                    Interlocked.Add(ref bytesDone, add);
                    Report();
                }

                var hash = await HashFileAsync(pathOf(node), size, full, useCache, _generation, OnRead, token);
                if (hash is { } h) hashes[node] = h;
                if (budget > counted) Interlocked.Add(ref bytesDone, budget - counted); // unreadable: skipped
                Interlocked.Increment(ref done);
                Report();
            }

            // Each file system is read with its own plan, all at the same time.
            // Grouped by kind as well as device: a device of 0 only means it couldn't be told, so it says nothing
            // about which files share a file system, and files of different kinds never share a plan.
            await Task.WhenAll(files.GroupBy(locate).Select(location =>
            {
                var plan = ReadPlan.For(location.Key.Kind);
                var queue = InReadOrder(location, plan, pathOf);
                var useCache = plan.UseCache ? cache : null;
                return Parallel.ForEachAsync(queue,
                    new ParallelOptions { MaxDegreeOfParallelism = full ? plan.FullParallelism : plan.SampleParallelism, CancellationToken = ct },
                    (node, token) => HashOne(node, useCache, token));
            }));
            progress?.Report(new DuplicateProgress(stage, total, total, bytesTotal, bytesTotal));

            var result = new List<List<int>>();
            foreach (var group in groups)
            {
                foreach (var same in group.Where(hashes.ContainsKey).GroupBy(n => hashes[n]))
                {
                    var members = same.ToList();
                    if (members.Count > 1) result.Add(members);
                }
            }
            return result;
        }
    }

    // No writers while it is open: Windows enforces the share mode, so a file open for writing elsewhere can't be
    // opened (an IOException, so it is not identical) and can't be opened for writing meanwhile. Other systems
    // don't enforce it, which is what the stamps in Unchanged are for.
    private static SafeFileHandle OpenForComparison(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

    // A missing stamp (no native engine, or a file system that can't tell) never counts as unchanged: without it a
    // different file put in the same place afterwards couldn't be told apart either.
    private static bool Unchanged(FileStamp? before, FileStamp? after) =>
        before is { } b && (OperatingSystem.IsWindows() || b.Trusted) && b == after;

    /// <summary>
    /// Whether <see cref="FindIdenticalCopyAsync"/> can confirm a copy at all: that needs the file stamps of the
    /// native engine. Without them no copy is ever found identical.
    /// </summary>
    public static bool CanCheckCopies => NativeEngine.IsAvailable;

    private static SafeFileHandle OpenRead(string path, bool sequential) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            sequential ? FileOptions.SequentialScan | FileOptions.Asynchronous : FileOptions.Asynchronous);

    /// <summary>
    /// Hashes the head and tail (<paramref name="full"/> false) or all of a file. Returns null if the file
    /// can no longer be read or changed size since the scan. With a <paramref name="cache"/>, a file whose
    /// stamp (identity, size, last-write and change time) still matches an earlier hash is not read at all.
    /// </summary>
    internal static async Task<UInt128?> HashFileAsync(string path, ulong expectedSize, bool full, FileHashCache? cache,
        int cacheGeneration, Action<long>? onRead, CancellationToken ct)
    {
        byte[]? buffer = null;
        try
        {
            using var handle = OpenRead(path, full);
            if ((ulong)RandomAccess.GetLength(handle) != expectedSize) return null;

            // Taken from the open file now, not from the scan: the file may have changed since.
            var stampedAt = FileHashCache.NowNs();
            var stamp = cache is null ? null : StorageInfo.GetStamp(handle);
            if (stamp is { } before && before.Size == expectedSize && cache!.TryGet(path, before, full, out var cached))
            {
                onRead?.Invoke(BytesToRead(expectedSize, full));
                return cached;
            }

            buffer = ArrayPool<byte>.Shared.Rent(full ? FullBufferSize : SampleSize);
            var hasher = new XxHash128();
            if (full)
            {
                long offset = 0;
                int read;
                while ((read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, FullBufferSize), offset, ct)) > 0)
                {
                    hasher.Append(buffer.AsSpan(0, read));
                    offset += read;
                    onRead?.Invoke(read);
                }
            }
            else
            {
                var length = (long)expectedSize;
                onRead?.Invoke(await ReadIntoAsync(handle, 0, buffer.AsMemory(0, SampleSize), hasher, ct));
                if (length > SampleSize)
                    onRead?.Invoke(await ReadIntoAsync(handle, Math.Max(SampleSize, length - SampleSize), buffer.AsMemory(0, SampleSize), hasher, ct));
            }
            // Shared for writing, so it may have changed while being read: only trust a hash of a
            // file that still has the size the group (and its reclaimable bytes) is based on.
            if ((ulong)RandomAccess.GetLength(handle) != expectedSize) return null;
            var hash = hasher.GetCurrentHashAsUInt128();
            // Remembered only if nothing changed the file while it was read.
            if (stamp is { } start && start.Size == expectedSize && StorageInfo.GetStamp(handle) == start)
                cache!.Set(path, start, stampedAt, full, hash, cacheGeneration);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The first of <paramref name="copies"/> whose content is identical to the file at <paramref name="path"/>,
    /// compared byte by byte now (see <see cref="AreIdenticalAsync"/>). Copies in a Recycle Bin or Trash folder don't
    /// count: they are on their way out. Used before a copy is moved to the Trash, since the search may be old
    /// (or come from a snapshot) and the files may have changed since. <paramref name="progress"/> gets
    /// the share compared so far, from 0 to 1. Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static async Task<IdenticalCopyCheck> FindIdenticalCopyAsync(string path, ulong size, IEnumerable<string> copies,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var candidates = copies.Where(c => !TrashFolders.Contains(c)).ToList();
        var total = Math.Max(1.0, (double)size * candidates.Count);
        double done = 0;
        var lastReport = Environment.TickCount64;
        var inUse = false;
        foreach (var copy in candidates)
        {
            var start = done;
            var (result, ownStamp, copyStamp) = await CompareAsync(path, copy, size, ct, bytes =>
            {
                done += bytes;
                var now = Environment.TickCount64;
                if (now - lastReport < ProgressIntervalMs) return;
                lastReport = now;
                progress?.Report(Math.Min(1, done / total));
            });
            if (result == Comparison.Identical) return new IdenticalCopyCheck(copy, copyStamp, ownStamp, InUse: false);
            inUse |= result == Comparison.InUse;
            done = start + size;
        }
        return new IdenticalCopyCheck(null, null, null, inUse);
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> is still the one a check compared, the same file and unchanged
    /// (<paramref name="stamp"/> is its stamp at the end of the check), for the copy it matched as well as for the file
    /// about to be moved. Another program could have changed either, or moved a different file there, since. False
    /// without a stamp.
    /// </summary>
    public static bool IsStillTheMatch(string path, FileStamp? stamp)
    {
        if (stamp is null) return false;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return StorageInfo.GetStamp(handle) == stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether two files have exactly the same content, compared byte by byte now (no hashes, no cache).
    /// False if either can't be read or is not <paramref name="expectedSize"/> bytes long, and false when it can't be
    /// told that neither changed during the comparison: a write to a part already compared would go unseen.
    /// Both files need a stamp (see <see cref="CanCheckCopies"/>), the same before and after. Windows also keeps
    /// writers out while the files are open; elsewhere each stamp must be trusted (the change time). File systems keep that time in steps (up to 2 s on FAT), so a file
    /// changed less than <see cref="CompareSettleTime"/> ago is first left to settle, and any later write then moves it.
    /// Writes the kernel only learns of later (through a memory map, or from another computer on a network drive)
    /// can still go unnoticed.
    /// <paramref name="onCompared"/> gets the bytes of each file compared after every step.
    /// </summary>
    public static async Task<bool> AreIdenticalAsync(string first, string second, ulong expectedSize, CancellationToken ct = default,
        Action<long>? onCompared = null) =>
        (await CompareAsync(first, second, expectedSize, ct, onCompared)).Result == Comparison.Identical;

    /// <summary>How long ago a file must have last changed before its change time shows every later write.</summary>
    public static readonly TimeSpan CompareSettleTime = TimeSpan.FromSeconds(2);

    private enum Comparison { Identical, Different, InUse }

    private static async Task<(Comparison Result, FileStamp? FirstStamp, FileStamp? SecondStamp)> CompareAsync(string first, string second,
        ulong expectedSize, CancellationToken ct, Action<long>? onCompared)
    {
        var a = ArrayPool<byte>.Shared.Rent(FullBufferSize);
        var b = ArrayPool<byte>.Shared.Rent(FullBufferSize);
        try
        {
            using var left = OpenForComparison(first);
            using var right = OpenForComparison(second);
            if ((ulong)RandomAccess.GetLength(left) != expectedSize || (ulong)RandomAccess.GetLength(right) != expectedSize)
                return (Comparison.Different, null, null);
            var leftBefore = StorageInfo.GetStamp(left);
            var rightBefore = StorageInfo.GetStamp(right);
            if (!OperatingSystem.IsWindows())
            {
                var wait = Max(UnsettledFor(leftBefore), UnsettledFor(rightBefore));
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                    if (StorageInfo.GetStamp(left) != leftBefore || StorageInfo.GetStamp(right) != rightBefore)
                        return (Comparison.Different, null, null); // being written to right now
                }
            }
            long offset = 0;
            while (true)
            {
                var readLeft = await ReadFullyAsync(left, a.AsMemory(0, FullBufferSize), offset, ct);
                var readRight = await ReadFullyAsync(right, b.AsMemory(0, FullBufferSize), offset, ct);
                if (readLeft != readRight || !a.AsSpan(0, readLeft).SequenceEqual(b.AsSpan(0, readRight)))
                    return (Comparison.Different, null, null);
                if (readLeft == 0) break;
                offset += readLeft;
                onCompared?.Invoke(readLeft);
            }
            var leftAfter = StorageInfo.GetStamp(left);
            var rightAfter = StorageInfo.GetStamp(right);
            var same = (ulong)offset == expectedSize &&
                       (ulong)RandomAccess.GetLength(left) == expectedSize && (ulong)RandomAccess.GetLength(right) == expectedSize &&
                       Unchanged(leftBefore, leftAfter) && Unchanged(rightBefore, rightAfter);
            return same ? (Comparison.Identical, leftAfter, rightAfter) : (Comparison.Different, null, null);
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation)
        {
            return (Comparison.InUse, null, null); // open for writing in another program
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Comparison.Different, null, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    private const int ErrorSharingViolation = 32, ErrorLockViolation = 33;

    private static TimeSpan Max(TimeSpan x, TimeSpan y) => x > y ? x : y;

    // How much longer to wait until the file's last change is CompareSettleTime old. Never longer than that, so a time
    // in the future (a network drive's clock, say) doesn't stall the check.
    private static TimeSpan UnsettledFor(FileStamp? stamp)
    {
        if (stamp is not { } s) return TimeSpan.Zero;
        var ageNs = FileHashCache.NowNs() - Math.Max(s.WriteTimeNs, s.ChangeTimeNs);
        var settleNs = CompareSettleTime.Ticks * 100;
        return ageNs >= settleNs ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(settleNs, settleNs - ageNs) / 100);
    }

    private static async Task<int> ReadFullyAsync(SafeFileHandle handle, Memory<byte> buffer, long offset, CancellationToken ct)
    {
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await RandomAccess.ReadAsync(handle, buffer[total..], offset + total, ct)) > 0)
            total += read;
        return total;
    }

    private static async Task<int> ReadIntoAsync(SafeFileHandle handle, long offset, Memory<byte> buffer, XxHash128 hasher, CancellationToken ct)
    {
        var total = await ReadFullyAsync(handle, buffer, offset, ct);
        hasher.Append(buffer.Span[..total]);
        return total;
    }
}
