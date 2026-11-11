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
    /// <summary>File counted here that has other hard links; deleting this name may not free the space.</summary>
    HasLinks = 0x40,
    /// <summary>
    /// File that shares disk blocks with other files (an APFS clone, macOS); deleting it may free less than its size on
    /// disk. Of several pure clones only the first one found counts the shared blocks as allocated.
    /// </summary>
    Clone = 0x80,
    /// <summary>
    /// A pure clone (<see cref="Clone"/>) whose data is counted on another file of the same clone family, the first one
    /// found. It keeps no copy of the data of its own.
    /// </summary>
    CloneShared = 0x100,
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
