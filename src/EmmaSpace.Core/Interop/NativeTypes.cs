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
}

public enum ScanPhase : uint
{
    Starting = 0,
    Enumerating = 1,
    Finalizing = 2,
    Done = 3,
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
