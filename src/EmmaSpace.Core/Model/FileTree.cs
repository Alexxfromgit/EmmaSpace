using System.Runtime.CompilerServices;
using EmmaSpace.Core.Interop;

namespace EmmaSpace.Core.Model;

/// <summary>
/// A scanned directory tree stored as flat arrays (see emmascan.h). Node 0 is the root, sizes of
/// directories are subtree totals and children are ordered largest first.
/// </summary>
public sealed class FileTree : IDisposable
{
    public const int Root = 0;
    public const int None = -1;

    private readonly NodeStorage _storage;

    public FileTree(NodeStorage storage, ScanInfo info)
    {
        _storage = storage;
        Info = info;
        Count = storage.Nodes.Length;
        if (Count == 0) throw new ArgumentException("A tree needs at least a root node.", nameof(storage));
    }

    public ScanInfo Info { get; }
    public int Count { get; }
    public NodeStorage Storage => _storage;

    public ReadOnlySpan<EsNode> Nodes => _storage.Nodes;
    public ReadOnlySpan<char> Names => _storage.Names;

    public ref EsNode this[int node] => ref _storage.Nodes[node];

    /// <inheritdoc cref="NodeStorage.Lease"/>
    public IDisposable Lease() => _storage.Lease();

    /// <summary>
    /// Copies the nodes into a tree of their own. Call it on the thread that changes the tree (the
    /// UI thread) before handing work to the background: a lease keeps memory alive but does not
    /// stop <see cref="Remove"/> from changing totals and sibling order under a reader. The name
    /// pool is shared, not copied (removals never change it); dispose the copy to release it.
    /// </summary>
    public FileTree Copy() => new(_storage.CopyNodes(), Info) { SizesApproximate = SizesApproximate };

    /// <summary>
    /// Incremented by every <see cref="Remove"/>. Background readers of the live tree compare it
    /// before and after to tell whether their result still describes the current state.
    /// </summary>
    public int Revision { get; private set; }

    public string RootPath => GetName(Root);

    /// <summary>Raised after a node (and its subtree) was removed.</summary>
    public event Action<int>? NodeRemoved;

    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public ReadOnlySpan<char> GetNameSpan(int node)
    {
        ref readonly var n = ref _storage.Nodes[node];
        return _storage.Names.Slice((int)n.NameOffset, n.NameLength);
    }

    public string GetName(int node) => new(GetNameSpan(node));

    public int GetParent(int node) => ToIndex(this[node].Parent);

    public bool IsDirectory(int node) => this[node].IsDirectory;

    /// <summary>Extension without the dot, or empty. Leading-dot names like ".gitignore" have none.</summary>
    public ReadOnlySpan<char> GetExtension(int node)
    {
        var name = GetNameSpan(node);
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? ReadOnlySpan<char>.Empty : name[(dot + 1)..];
    }

    public int GetDepth(int node)
    {
        var depth = 0;
        for (var n = GetParent(node); n != None; n = GetParent(n)) depth++;
        return depth;
    }

    public string GetPath(int node)
    {
        if (node == Root) return RootPath;
        var chain = new List<int>(16);
        for (var n = node; n != None; n = GetParent(n)) chain.Add(n);

        var length = 0;
        foreach (var n in chain) length += this[n].NameLength + 1;
        Span<char> buffer = length <= 1024 ? stackalloc char[length] : new char[length];
        var pos = 0;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var name = GetNameSpan(chain[i]);
            name.CopyTo(buffer[pos..]);
            pos += name.Length;
            if (i > 0 && !EndsWithSeparator(name)) buffer[pos++] = Path.DirectorySeparatorChar;
        }
        return new string(buffer[..pos]);
    }

    public ChildEnumerable Children(int node) => new(this, node);

    public bool HasChildren(int node)
    {
        foreach (var _ in Children(node)) return true;
        return false;
    }

    /// <summary>True if <paramref name="node"/> is <paramref name="ancestor"/> or lies below it.</summary>
    public bool IsWithin(int node, int ancestor)
    {
        for (var n = node; n != None; n = GetParent(n))
            if (n == ancestor) return true;
        return false;
    }

    /// <summary>
    /// Removes a node (e.g. after it was moved to the Recycle Bin): unlinks it from its parent,
    /// subtracts its totals from every ancestor and marks it and its descendants deleted.
    /// </summary>
    public void Remove(int node)
    {
        if (node == Root) throw new InvalidOperationException("The root cannot be removed.");
        var nodes = _storage.Nodes;
        ref var target = ref nodes[node];
        if (target.IsDeleted) return;

        var parent = ToIndex(target.Parent);
        ref var p = ref nodes[parent];
        if (ToIndex(p.FirstChild) == node)
        {
            p.FirstChild = target.NextSibling;
        }
        else
        {
            for (var c = ToIndex(p.FirstChild); c != None; c = ToIndex(nodes[c].NextSibling))
            {
                if (ToIndex(nodes[c].NextSibling) != node) continue;
                nodes[c].NextSibling = target.NextSibling;
                break;
            }
        }

        var dirs = target.DirCount + (target.IsDirectory ? 1u : 0u);
        for (var a = parent; a != None; a = ToIndex(nodes[a].Parent))
        {
            ref var anc = ref nodes[a];
            anc.Size -= target.Size;
            anc.Allocated -= target.Allocated;
            anc.FileCount -= target.FileCount;
            anc.DirCount -= dirs;
        }

        var stack = new Stack<int>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            ref var removed = ref nodes[n];
            removed.Flags |= NodeFlags.Deleted;
            // With hard links counted once, the bytes may still be held by another name.
            if ((removed.Flags & (NodeFlags.HasLinks | NodeFlags.HardLink)) != 0) SizesApproximate = true;
            for (var c = ToIndex(removed.FirstChild); c != None; c = ToIndex(nodes[c].NextSibling)) stack.Push(c);
        }

        // Every ancestor shrank, so restore largest-first order among each ancestor's siblings
        // (the treemap stops at the first zero-size child and relies on that order).
        for (var a = parent; a != Root && a != None; a = ToIndex(nodes[a].Parent))
            SortChildren(ToIndex(nodes[a].Parent));

        Revision++;
        NodeRemoved?.Invoke(node);
    }

    /// <summary>
    /// True once a removal touched a file with other hard links: those bytes may still be in use
    /// under another name, so totals are approximate until the next scan.
    /// </summary>
    public bool SizesApproximate { get; private set; }

    private void SortChildren(int node)
    {
        var nodes = _storage.Nodes;
        var children = new List<int>();
        for (var c = ToIndex(nodes[node].FirstChild); c != None; c = ToIndex(nodes[c].NextSibling)) children.Add(c);
        if (children.Count < 2) return;
        var sizes = new ulong[children.Count];
        for (var i = 0; i < sizes.Length; i++) sizes[i] = nodes[children[i]].Size;
        // Stable: equal sizes keep their current order.
        var order = Enumerable.Range(0, children.Count).OrderByDescending(i => sizes[i]).ThenBy(i => i).ToArray();
        nodes[node].FirstChild = (uint)children[order[0]];
        for (var k = 0; k < order.Length; k++)
            nodes[children[order[k]]].NextSibling = k + 1 < order.Length ? (uint)children[order[k + 1]] : EsNode.None;
    }

    /// <summary>Finds a node by full path, or <see cref="None"/>.</summary>
    public int FindByPath(string path)
    {
        var root = RootPath.TrimEnd(s_separators);
        var trimmed = path.TrimEnd(s_separators);
        if (!trimmed.StartsWith(root, PathComparison)) return None;
        // "C:\Root" must not match "C:\Rootbackup": require a separator right after the root.
        if (trimmed.Length > root.Length && Array.IndexOf(s_separators, trimmed[root.Length]) < 0 &&
            !EndsWithSeparator(RootPath))
            return None;
        var rest = trimmed.AsSpan(root.Length).TrimStart(s_separators);
        var node = Root;
        foreach (var range in rest.SplitAny(s_separators))
        {
            var part = rest[range];
            if (part.IsEmpty) continue;
            var next = None;
            foreach (var c in Children(node))
            {
                if (!GetNameSpan(c).Equals(part, PathComparison)) continue;
                next = c;
                break;
            }
            if (next == None) return None;
            node = next;
        }
        return node;
    }

    public void Dispose() => _storage.Dispose();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ToIndex(uint value) => value == EsNode.None ? None : (int)value;

    // Backslash separates folders only on Windows; elsewhere it is a valid character in names.
    private static readonly char[] s_separators = OperatingSystem.IsWindows() ? ['\\', '/'] : ['/'];

    private static bool EndsWithSeparator(ReadOnlySpan<char> s) => s.Length > 0 && Array.IndexOf(s_separators, s[^1]) >= 0;

    public readonly struct ChildEnumerable(FileTree tree, int node)
    {
        public ChildEnumerator GetEnumerator() => new(tree, node);
    }

    /// <summary>Enumerates live (non-deleted) children, largest first.</summary>
    public struct ChildEnumerator(FileTree tree, int node)
    {
        private int _current = None;
        private bool _started;

        public readonly int Current => _current;

        public bool MoveNext()
        {
            var nodes = tree.Nodes;
            _current = _started
                ? (_current == None ? None : ToIndex(nodes[_current].NextSibling))
                : ToIndex(nodes[node].FirstChild);
            _started = true;
            while (_current != None && nodes[_current].IsDeleted) _current = ToIndex(nodes[_current].NextSibling);
            return _current != None;
        }
    }
}
