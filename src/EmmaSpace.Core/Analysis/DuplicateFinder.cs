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
    public ulong MinSize { get; init; } = 1024 * 1024;

    /// <summary>The storage the files are on; null asks the system, per file system the tree spans.</summary>
    public StorageKind? Storage { get; init; }

    /// <summary>Hashes from earlier searches, used for files that have not changed since; null reads every file.</summary>
    public FileHashCache? Cache { get; init; }
}

public sealed record DuplicateGroup(ulong FileSize, IReadOnlyList<int> Nodes)
{
    public ulong WastedBytes => FileSize * (ulong)(Nodes.Count - 1);
}

/// <summary>A completed duplicate search: the groups it found and the smallest file size it looked at.</summary>
public sealed record DuplicateResult(ulong MinSize, IReadOnlyList<DuplicateGroup> Groups);

/// <summary>Files done of the files to read in this stage, and the bytes read (or found in the cache) of the bytes to read.</summary>
public readonly record struct DuplicateProgress(string Stage, long Done, long Total, long BytesDone = 0, long BytesTotal = 0);

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
            .Select(g => new DuplicateGroup(tree.Nodes[g[0]].Size, g))
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
            await Task.WhenAll(files.GroupBy(n => locate(n).Device).Select(device =>
            {
                var plan = ReadPlan.For(locate(device.First()).Kind);
                var queue = InReadOrder(device, plan, pathOf);
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

    private static bool Unchanged(FileStamp? before, FileStamp? after) =>
        OperatingSystem.IsWindows() ? before == after : before is { Trusted: true } && before == after;

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
    /// compared byte by byte now; null if none is. Copies in a Recycle Bin or Trash folder don't count:
    /// they are on their way out. Used before a copy is moved to the Trash, since the search may be old
    /// (or come from a snapshot) and the files may have changed since. <paramref name="progress"/> gets
    /// the share compared so far, from 0 to 1. Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static async Task<string?> FindIdenticalCopyAsync(string path, ulong size, IEnumerable<string> copies,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var candidates = copies.Where(c => !TrashFolders.Contains(c)).ToList();
        var total = Math.Max(1.0, (double)size * candidates.Count);
        double done = 0;
        var lastReport = Environment.TickCount64;
        foreach (var copy in candidates)
        {
            var start = done;
            var identical = await AreIdenticalAsync(path, copy, size, ct, bytes =>
            {
                done += bytes;
                var now = Environment.TickCount64;
                if (now - lastReport < ProgressIntervalMs) return;
                lastReport = now;
                progress?.Report(Math.Min(1, done / total));
            });
            if (identical) return copy;
            done = start + size;
        }
        return null;
    }

    /// <summary>
    /// Whether two files have exactly the same content, compared byte by byte now (no hashes, no cache).
    /// False if either can't be read or is not <paramref name="expectedSize"/> bytes long, and false when it can't be
    /// told that neither changed during the comparison: a write to a part already compared would go unseen.
    /// Windows keeps writers out while the files are open; elsewhere each file's stamp must be trusted (the
    /// change time, which every write moves on) and the same before and after.
    /// <paramref name="onCompared"/> gets the bytes of each file compared after every step.
    /// </summary>
    public static async Task<bool> AreIdenticalAsync(string first, string second, ulong expectedSize, CancellationToken ct = default,
        Action<long>? onCompared = null)
    {
        var a = ArrayPool<byte>.Shared.Rent(FullBufferSize);
        var b = ArrayPool<byte>.Shared.Rent(FullBufferSize);
        try
        {
            using var left = OpenForComparison(first);
            using var right = OpenForComparison(second);
            if ((ulong)RandomAccess.GetLength(left) != expectedSize || (ulong)RandomAccess.GetLength(right) != expectedSize)
                return false;
            var leftBefore = StorageInfo.GetStamp(left);
            var rightBefore = StorageInfo.GetStamp(right);
            long offset = 0;
            while (true)
            {
                var readLeft = await ReadFullyAsync(left, a.AsMemory(0, FullBufferSize), offset, ct);
                var readRight = await ReadFullyAsync(right, b.AsMemory(0, FullBufferSize), offset, ct);
                if (readLeft != readRight || !a.AsSpan(0, readLeft).SequenceEqual(b.AsSpan(0, readRight))) return false;
                if (readLeft == 0) break;
                offset += readLeft;
                onCompared?.Invoke(readLeft);
            }
            return (ulong)offset == expectedSize &&
                   (ulong)RandomAccess.GetLength(left) == expectedSize && (ulong)RandomAccess.GetLength(right) == expectedSize &&
                   Unchanged(leftBefore, StorageInfo.GetStamp(left)) && Unchanged(rightBefore, StorageInfo.GetStamp(right));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
        }
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
