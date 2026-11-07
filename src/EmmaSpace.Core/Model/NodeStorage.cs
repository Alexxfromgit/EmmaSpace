using EmmaSpace.Core.Interop;

namespace EmmaSpace.Core.Model;

/// <summary>Backing memory for a <see cref="FileTree"/>: a node array plus a UTF-16 name pool.</summary>
public abstract class NodeStorage : IDisposable
{
    public abstract Span<EsNode> Nodes { get; }
    public abstract ReadOnlySpan<char> Names { get; }

    /// <summary>
    /// Keeps the memory alive while background work reads it, even if the tree is disposed
    /// meanwhile. Accessing <see cref="Nodes"/> after disposal still throws, but spans already
    /// obtained stay valid until the lease is released.
    /// </summary>
    public virtual IDisposable Lease() => NoLease.Instance;

    /// <summary>
    /// A storage with its own copy of the nodes that shares this one's name pool (removals only
    /// change nodes, never names), keeping the shared memory alive until it is disposed.
    /// </summary>
    internal virtual NodeStorage CopyNodes() => new ArrayNodeStorage(Nodes.ToArray(), Names.ToArray());

    public virtual void Dispose() => GC.SuppressFinalize(this);

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();
        public void Dispose() { }
    }
}

public sealed class ArrayNodeStorage(EsNode[] nodes, char[] names) : NodeStorage
{
    public override Span<EsNode> Nodes => nodes;
    public override ReadOnlySpan<char> Names => names;

    internal override NodeStorage CopyNodes() => new ArrayNodeStorage(nodes.ToArray(), names);
}

/// <summary>Views memory owned by the native engine; nothing is copied.</summary>
internal sealed unsafe class NativeNodeStorage : NodeStorage
{
    private readonly ScanHandle _handle;
    private readonly EsNode* _nodes;
    private readonly int _count;
    private readonly char* _names;
    private readonly int _namesLength;

    public NativeNodeStorage(ScanHandle handle, in EsResult result)
    {
        if (result.NamesLength > int.MaxValue) throw new NotSupportedException("Name pool exceeds 2 GiB.");
        _handle = handle;
        _nodes = result.Nodes;
        _count = checked((int)result.NodeCount);
        _names = result.Names;
        _namesLength = (int)result.NamesLength;
    }

    public override Span<EsNode> Nodes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return new Span<EsNode>(_nodes, _count);
        }
    }

    public override ReadOnlySpan<char> Names
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return new ReadOnlySpan<char>(_names, _namesLength);
        }
    }

    public override IDisposable Lease()
    {
        var added = false;
        _handle.DangerousAddRef(ref added);
        return new HandleLease(_handle);
    }

    internal override NodeStorage CopyNodes()
    {
        var lease = Lease();
        try
        {
            return new LeasedNamesStorage(Nodes.ToArray(), lease, _names, _namesLength);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public override void Dispose()
    {
        _handle.Dispose();
        base.Dispose();
    }

    private sealed class HandleLease(ScanHandle handle) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) handle.DangerousRelease();
        }
    }
}

/// <summary>Copied nodes plus a name pool still owned by the native engine, held by a lease.</summary>
internal sealed unsafe class LeasedNamesStorage(EsNode[] nodes, IDisposable lease, char* names, int namesLength) : NodeStorage
{
    private readonly char* _names = names;
    private bool _disposed;

    public override Span<EsNode> Nodes => nodes;

    public override ReadOnlySpan<char> Names
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new ReadOnlySpan<char>(_names, namesLength);
        }
    }

    public override void Dispose()
    {
        _disposed = true;
        lease.Dispose();
        base.Dispose();
    }
}
