using System.Runtime.InteropServices;

namespace EmmaSpace.Core.Interop;

public enum EsStatus
{
    Ok = 0,
    InvalidArgument = 1,
    NotFound = 2,
    AccessDenied = 3,
    Cancelled = 4,
    NotSupported = 5,
    IoError = 6,
    Running = 7,
    Timeout = 8,
    OutOfMemory = 9,
    Internal = 99,
}

public enum ScanMode : uint
{
    Auto = 0,
    Walker = 1,
    Mft = 2,
    /// <summary>ext2/3/4 inode tables read from the block device (Linux, root).</summary>
    Ext4 = 3,
}

public enum ScanPhase : uint
{
    Starting = 0,
    Enumerating = 1,
    Finalizing = 2,
    Done = 3,
    /// <summary>
    /// App only, never sent by the engine: the Linux fast scan is waiting for pkexec to accept the administrator
    /// password. <see cref="Scanning.HelperScanner"/> reports it, then <see cref="Starting"/> once the helper runs.
    /// </summary>
    WaitingForAuthorization = 0x100,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EsScanOptions
{
    public uint StructSize;
    public uint Mode;
    public uint ThreadCount;
    public uint Flags;
    public char* RootPath;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EsProgress
{
    public ulong Files;
    public ulong Directories;
    public ulong Bytes;
    public uint Errors;
    public uint Phase;
    public uint Mode;
    public uint PathLength;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EsResult
{
    public EsNode* Nodes;
    public char* Names;
    public ulong NamesLength;
    public uint NodeCount;
    public uint Mode;
    public uint Errors;
    public uint Reserved;
    public ulong ElapsedMs;
}

/// <summary>Mirror of <c>es_volume_space</c> in emmascan.h.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct EsVolumeSpace
{
    public ulong Total;
    public ulong Free;
    public ulong Available;
}

/// <summary>Mirror of <c>es_file_stamp</c> in emmascan.h.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct EsFileStamp
{
    public ulong Volume;
    public ulong FileIdLow;
    public ulong FileIdHigh;
    public ulong Size;
    public long MTimeNs;
    public long CTimeNs;
    public ulong Usn;
    public uint Flags;
    public uint Reserved;

    public const uint Trusted = 0x1; // ES_STAMP_TRUSTED
}
