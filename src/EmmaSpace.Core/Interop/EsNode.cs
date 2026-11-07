using System.Runtime.InteropServices;

namespace EmmaSpace.Core.Interop;

[Flags]
public enum NodeFlags : ushort
{
    None = 0,
    Directory = 0x01,
    Reparse = 0x02,
    Error = 0x04,
    HardLink = 0x08,
    Deleted = 0x10,
    /// <summary>Cloud placeholder: size counts only the locally stored part.</summary>
    Cloud = 0x20,
}

/// <summary>Mirror of <c>es_node</c> in emmascan.h. Must stay exactly 64 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct EsNode
{
    public const uint None = uint.MaxValue;

    public ulong Size;
    public ulong Allocated;
    public long MTime;
    public long ATime;
    public uint Parent;
    public uint FirstChild;
    public uint NextSibling;
    public uint NameOffset;
    public uint FileCount;
    public uint DirCount;
    public uint Attributes;
    public ushort NameLength;
    public NodeFlags Flags;

    public readonly bool IsDirectory => (Flags & NodeFlags.Directory) != 0;
    public readonly bool IsDeleted => (Flags & NodeFlags.Deleted) != 0;
}
