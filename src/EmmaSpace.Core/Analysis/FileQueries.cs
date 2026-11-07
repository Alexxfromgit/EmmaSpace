using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Analysis;

public enum AgeBasis
{
    LastModified,
    LastAccessed,
}

public static class FileQueries
{
    /// <summary>The <paramref name="count"/> largest files, largest first.</summary>
    public static IReadOnlyList<int> LargestFiles(FileTree tree, int count, int scope = FileTree.Root, CancellationToken ct = default)
    {
        var heap = new PriorityQueue<int, ulong>(count + 1);
        foreach (var node in TreeWalk.Files(tree, scope))
        {
            if ((node & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var size = tree.Nodes[node].Size;
            if (heap.Count < count)
                heap.Enqueue(node, size);
            else if (heap.TryPeek(out _, out var smallest) && size > smallest)
                heap.EnqueueDequeue(node, size);
        }
        return SortedDescending(tree, heap);
    }

    /// <summary>Largest files not modified (or accessed) since <paramref name="cutoff"/>.</summary>
    public static IReadOnlyList<int> OldFiles(FileTree tree, DateTimeOffset cutoff, AgeBasis basis, int maxResults,
        ulong minSize = 0, int scope = FileTree.Root, CancellationToken ct = default)
    {
        var cutoffMs = cutoff.ToUnixTimeMilliseconds();
        var heap = new PriorityQueue<int, ulong>(maxResults + 1);
        foreach (var node in TreeWalk.Files(tree, scope))
        {
            if ((node & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            ref readonly var n = ref tree.Nodes[node];
            var time = basis == AgeBasis.LastAccessed ? n.ATime : n.MTime;
            if (time == 0 || time >= cutoffMs || n.Size < minSize) continue;
            if (heap.Count < maxResults)
                heap.Enqueue(node, n.Size);
            else if (heap.TryPeek(out _, out var smallest) && n.Size > smallest)
                heap.EnqueueDequeue(node, n.Size);
        }
        return SortedDescending(tree, heap);
    }

    /// <summary>
    /// Folders that contain no files at any depth. Only the topmost such folder is reported, so a
    /// tree of nested empty folders appears once. A folder only counts as empty when its whole
    /// subtree was read: anything unreadable, a link or an unlisted cloud folder below it means
    /// its contents are unknown, so it is never offered for clean-up.
    /// </summary>
    public static IReadOnlyList<int> EmptyFolders(FileTree tree, int scope = FileTree.Root, CancellationToken ct = default)
    {
        var incomplete = IncompleteSubtrees(tree, ct);
        bool IsEmpty(int node)
        {
            ref readonly var n = ref tree.Nodes[node];
            return node != FileTree.Root && n.IsDirectory && n.FileCount == 0 && !incomplete[node];
        }

        var result = new List<int>();
        foreach (var node in TreeWalk.Subtree(tree, scope))
        {
            if ((node & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (node == scope || !IsEmpty(node)) continue;
            var parent = tree.GetParent(node);
            if (parent != scope && parent != FileTree.None && IsEmpty(parent)) continue;
            result.Add(node);
        }
        return result;
    }

    /// <summary>
    /// For each node, whether it or anything below it could not be fully enumerated. Children
    /// always have higher indexes than their parents, so one reverse pass rolls the flags up.
    /// </summary>
    private static bool[] IncompleteSubtrees(FileTree tree, CancellationToken ct)
    {
        const NodeFlags notListed = NodeFlags.Error | NodeFlags.Reparse | NodeFlags.Cloud;
        var nodes = tree.Nodes;
        var incomplete = new bool[tree.Count];
        for (var i = tree.Count - 1; i >= 0; i--)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            ref readonly var n = ref nodes[i];
            if (n.IsDeleted) continue;
            if (n.IsDirectory && (n.Flags & notListed) != 0) incomplete[i] = true;
            if (incomplete[i] && n.Parent != EsNode.None) incomplete[(int)n.Parent] = true;
        }
        return incomplete;
    }

    private static List<int> SortedDescending(FileTree tree, PriorityQueue<int, ulong> heap)
    {
        var list = new List<int>(heap.Count);
        while (heap.TryDequeue(out var node, out _)) list.Add(node);
        list.Reverse();
        return list;
    }
}
