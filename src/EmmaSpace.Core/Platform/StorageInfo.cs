using EmmaSpace.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace EmmaSpace.Core.Platform;

/// <summary>What kind of storage a folder is on, which decides how many reads are best issued at once.</summary>
public enum StorageKind
{
    Unknown = 0,
    /// <summary>No seek penalty (solid state): many reads at once are fastest.</summary>
    Ssd = 1,
    /// <summary>Rotational disk: reads in parallel make the heads jump back and forth.</summary>
    Hdd = 2,
    /// <summary>Network share, or a virtual machine's shared folder.</summary>
    Network = 3,
}

/// <summary>The kind of storage a path is on, and which file system: equal <see cref="Device"/>s share one.</summary>
public readonly record struct StorageLocation(StorageKind Kind, ulong Device);

/// <summary>
/// Identity and times of an open file (see <c>es_file_stamp</c>): two stamps that are equal in every
/// field describe the same file, unchanged, provided the stamp is <see cref="Trusted"/>: one that every
/// change of the content moves on and no program can move back. That is the change time on Linux and
/// macOS (only the kernel sets it); on Windows programs can set every time back, ChangeTime included,
/// so only a stamp with the file's change-journal number (<see cref="Usn"/>) is trusted there.
/// </summary>
public readonly record struct FileStamp(ulong Volume, UInt128 FileId, ulong Size, long WriteTimeNs, long ChangeTimeNs,
    ulong Usn, bool Trusted);

public static class StorageInfo
{
    /// <summary>
    /// The kind of storage holding <paramref name="path"/>, asked of the operating system through the
    /// native engine; <see cref="StorageKind.Unknown"/> when it can't be told or the engine isn't loaded.
    /// May touch the disk (a sleeping drive or slow share), so call it off the UI thread.
    /// </summary>
    public static StorageKind GetKind(string path) => Get(path).Kind;

    /// <inheritdoc cref="GetKind"/>
    public static StorageLocation Get(string path)
    {
        if (string.IsNullOrEmpty(path) || !NativeEngine.IsAvailable) return default;
        return NativeMethods.GetStorageKind(path, out var kind, out var device) == EsStatus.Ok && kind <= (uint)StorageKind.Network
            ? new StorageLocation((StorageKind)kind, device)
            : default;
    }

    /// <summary>The stamp of an open file; null without the native engine or if the system can't tell.</summary>
    public static FileStamp? GetStamp(SafeFileHandle handle)
    {
        if (!NativeEngine.IsAvailable || handle.IsInvalid || handle.IsClosed) return null;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (NativeMethods.GetFileStamp(handle.DangerousGetHandle(), out var s) != EsStatus.Ok) return null;
            return new FileStamp(s.Volume, new UInt128(s.FileIdHigh, s.FileIdLow), s.Size, s.MTimeNs, s.CTimeNs, s.Usn,
                (s.Flags & EsFileStamp.Trusted) != 0);
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }
}
