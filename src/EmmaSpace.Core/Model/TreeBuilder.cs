using EmmaSpace.Core.Interop;

namespace EmmaSpace.Core.Model;

/// <summary>
/// Managed counterpart of the native NodeStore: builds the flat tree format, then rolls sizes
/// up and orders siblings largest first. Used by the managed scanner and by tests.
/// </summary>
public sealed class TreeBuilder
{
    private EsNode[] _nodes = new EsNode[1024];
    private char[] _names = new char[16 * 1024];
    private int _count;
    private int _namesLength;

    public int Count => _count;

    public int AddRoot(string path, long mtime = 0, uint attributes = 0)
    {
        _count = 0;
        _namesLength = 0;
        return Append(EsNode.None, path, NodeFlags.Directory, 0, 0, mtime, 0, attributes);
    }

    /// <summary>Adds a child; children of one parent are kept in insertion order until <see cref="Build"/>.</summary>
    public int AddChild(int parent, ReadOnlySpan<char> name, NodeFlags flags, ulong size = 0, ulong allocated = 0,
        long mtime = 0, long atime = 0, uint attributes = 0)
    {
        var index = Append((uint)parent, name, flags, size, allocated, mtime, atime, attributes);
        ref var p = ref _nodes[parent];
        // Prepend, then Build() sorts, so insertion order does not matter.
        _nodes[index].NextSibling = p.FirstChild;
        p.FirstChild = (uint)index;
        return index;
    }

    public void MarkError(int node) => _nodes[node].Flags |= NodeFlags.Error;

    public FileTree Build(ScanInfo info)
    {
        var nodes = _nodes.AsSpan(0, _count);
        for (var i = nodes.Length - 1; i >= 1; i--)
        {
            ref readonly var child = ref nodes[i];
            ref var parent = ref nodes[(int)child.Parent];
            parent.Size += child.Size;
            parent.Allocated += child.Allocated;
            parent.FileCount += child.FileCount;
            parent.DirCount += child.DirCount + (child.IsDirectory ? 1u : 0u);
        }

        var children = new List<int>();
        var nodeArray = _nodes;
        for (var i = 0; i < nodes.Length; i++)
        {
            ref var n = ref nodes[i];
            if (n.FirstChild == EsNode.None) continue;
            children.Clear();
            for (var c = n.FirstChild; c != EsNode.None; c = nodes[(int)c].NextSibling) children.Add((int)c);
            if (children.Count > 1)
                children.Sort((a, b) => nodeArray[b].Size.CompareTo(nodeArray[a].Size));
            n.FirstChild = (uint)children[0];
            for (var k = 0; k < children.Count; k++)
                nodes[children[k]].NextSibling = k + 1 < children.Count ? (uint)children[k + 1] : EsNode.None;
        }

        var storage = new ArrayNodeStorage(nodes.ToArray(), _names.AsSpan(0, _namesLength).ToArray());
        return new FileTree(storage, info);
    }

    private int Append(uint parent, ReadOnlySpan<char> name, NodeFlags flags, ulong size, ulong allocated,
        long mtime, long atime, uint attributes)
    {
        if (_count == _nodes.Length) Array.Resize(ref _nodes, _nodes.Length * 2);
        var nameLength = Math.Min(name.Length, ushort.MaxValue);
        if (_namesLength + nameLength > _names.Length)
            Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesLength + nameLength));
        name[..nameLength].CopyTo(_names.AsSpan(_namesLength));

        var isDir = (flags & NodeFlags.Directory) != 0;
        _nodes[_count] = new EsNode
        {
            Size = isDir ? 0 : size,
            Allocated = isDir ? 0 : allocated,
            MTime = mtime,
            ATime = atime,
            Parent = parent,
            FirstChild = EsNode.None,
            NextSibling = EsNode.None,
            NameOffset = (uint)_namesLength,
            NameLength = (ushort)nameLength,
            FileCount = isDir ? 0u : 1u,
            Attributes = attributes,
            Flags = flags,
        };
        _namesLength += nameLength;
        return _count++;
    }
}
