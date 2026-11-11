using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Analysis;

public sealed record DuplicateOptions
{
    public ulong MinSize { get; init; } = 1024 * 1024;
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);
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

public readonly record struct DuplicateProgress(string Stage, long Done, long Total);

/// <summary>
/// Finds identical files in three passes so most files are never read in full:
/// group by size → hash the first and last 64 KiB → hash the whole file.
/// </summary>
public static class DuplicateFinder
{
    private const int SampleSize = 64 * 1024;

    public static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(FileTree tree, DuplicateOptions options,
        IProgress<DuplicateProgress>? progress, CancellationToken ct, int scope = FileTree.Root)
    {
        // Pass 1: same size (cheap, from the scan data).
        var bySize = new Dictionary<ulong, List<int>>();
        foreach (var node in TreeWalk.Files(tree, scope))
        {
            ref readonly var n = ref tree.Nodes[node];
            if (n.Size < Math.Max(1, options.MinSize)) continue;
            // Links are never followed; cloud placeholders would be downloaded by opening them.
            if ((n.Flags & (NodeFlags.Reparse | NodeFlags.Error | NodeFlags.HardLink | NodeFlags.Cloud)) != 0) continue;
            if (!bySize.TryGetValue(n.Size, out var list)) bySize[n.Size] = list = [];
            list.Add(node);
        }
        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        var paths = new ConcurrentDictionary<int, string>();
        string PathOf(int node) => paths.GetOrAdd(node, tree.GetPath);

        // Pass 2: sample hash of head and tail.
        var sampleGroups = await HashGroupsAsync(candidates, tree, PathOf, full: false, "Comparing file samples",
            options, progress, ct);

        // Pass 3: full hash, only where the sample did not already cover the whole file.
        var needFull = sampleGroups.Where(g => tree.Nodes[g[0]].Size > 2 * SampleSize).ToList();
        var complete = sampleGroups.Where(g => tree.Nodes[g[0]].Size <= 2 * SampleSize).ToList();
        var fullGroups = await HashGroupsAsync(needFull, tree, PathOf, full: true, "Verifying duplicates",
            options, progress, ct);

        return complete.Concat(fullGroups)
            .Select(g => new DuplicateGroup(tree.Nodes[g[0]].Size, g, DuplicateGroup.CountHolders(tree, g)))
            .OrderByDescending(g => g.WastedBytes)
            .ToList();
    }

    private static async Task<List<List<int>>> HashGroupsAsync(List<List<int>> groups, FileTree tree,
        Func<int, string> pathOf, bool full, string stage, DuplicateOptions options,
        IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var total = groups.Sum(g => (long)g.Count);
        long done = 0;
        var hashes = new ConcurrentDictionary<int, UInt128>();
        progress?.Report(new DuplicateProgress(stage, 0, total));

        await Parallel.ForEachAsync(groups.SelectMany(g => g),
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = ct },
            async (node, token) =>
            {
                var hash = await HashFileAsync(pathOf(node), tree.Nodes[node].Size, full, token);
                if (hash is { } h) hashes[node] = h;
                var d = Interlocked.Increment(ref done);
                if (d % 64 == 0 || d == total) progress?.Report(new DuplicateProgress(stage, d, total));
            });

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

    /// <summary>Returns null if the file can no longer be read or changed size since the scan.</summary>
    internal static async Task<UInt128?> HashFileAsync(string path, ulong expectedSize, bool full, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(full ? 1024 * 1024 : SampleSize);
        try
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = full ? FileOptions.SequentialScan | FileOptions.Asynchronous : FileOptions.Asynchronous,
                BufferSize = 0,
            });
            if ((ulong)stream.Length != expectedSize) return null;

            var hasher = new XxHash128();
            if (full)
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0) hasher.Append(buffer.AsSpan(0, read));
            }
            else
            {
                await ReadIntoAsync(stream, 0, buffer.AsMemory(0, SampleSize), hasher, ct);
                if (stream.Length > SampleSize)
                    await ReadIntoAsync(stream, Math.Max(SampleSize, stream.Length - SampleSize), buffer.AsMemory(0, SampleSize), hasher, ct);
            }
            // Shared for writing, so it may have changed while being read: only trust a hash of a
            // file that still has the size the group (and its reclaimable bytes) is based on.
            if ((ulong)stream.Length != expectedSize) return null;
            return hasher.GetCurrentHashAsUInt128();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadIntoAsync(FileStream stream, long offset, Memory<byte> buffer, XxHash128 hasher, CancellationToken ct)
    {
        stream.Position = offset;
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer[total..], ct)) > 0) total += read;
        hasher.Append(buffer.Span[..total]);
    }
}
