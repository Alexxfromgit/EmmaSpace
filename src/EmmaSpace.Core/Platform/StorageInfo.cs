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
/// change of the content moves on (in the file system's time steps: see
/// <see cref="Analysis.DuplicateFinder.CompareSettleTime"/>) and no program can move back. That is the change time on Linux and
/// macOS (only the kernel sets it); on Windows programs can set every time back, ChangeTime included,
/// so only a stamp with the file's change-journal number (<see cref="Usn"/>) is trusted there.
/// </summary>
public readonly record struct FileStamp(ulong Volume, UInt128 FileId, ulong Size, long WriteTimeNs, long ChangeTimeNs,
    ulong Usn, bool Trusted);

/// <summary>
/// Space on a volume, in bytes. <see cref="Free"/> is unused space; <see cref="Available"/> adds what the system
/// removes by itself when an app needs the room (macOS: purgeable data), which is what Finder calls "available".
/// </summary>
public readonly record struct VolumeSpace(ulong Total, ulong Free, ulong Available)
{
    /// <summary>Purgeable space below this is not worth a mention.</summary>
    public const ulong PurgeableWorthMentioning = 100_000_000;

    /// <summary>Purgeable data: space macOS frees on its own when it is needed, without the user doing anything.</summary>
    public ulong Purgeable => Available > Free ? Available - Free : 0;

    public bool HasPurgeable => Purgeable >= PurgeableWorthMentioning;

    /// <summary>What Finder and System Settings call used: the capacity less what is available.</summary>
    public ulong Used => Total > Available ? Total - Available : 0;

    /// <summary>Used including purgeable data, which macOS hasn't cleared yet.</summary>
    public ulong UsedWithPurgeable => Total > Free ? Total - Free : 0;

    /// <summary>
    /// "121.8 GB available of 994.7 GB" when <paramref name="countsPurgeable"/> (the system's own figure, purgeable data
    /// included, as Finder words it), else "57.1 GB free of 994.7 GB"; empty when the capacity is unknown.
    /// </summary>
    public string Describe(bool countsPurgeable, Func<ulong, string> format) =>
        Total == 0 ? ""
        : countsPurgeable ? $"{format(Available)} available of {format(Total)}"
        : $"{format(Free)} free of {format(Total)}";

    /// <summary>"incl. 60.5 GB purgeable", "less than 100.0 MB purgeable" or "no purgeable data".</summary>
    public string DescribePurgeable(Func<ulong, string> format) =>
        HasPurgeable ? $"incl. {format(Purgeable)} purgeable"
        : Purgeable > 0 ? $"less than {format(PurgeableWorthMentioning)} purgeable"
        : "no purgeable data";
}

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

    /// <summary>
    /// The space on the volume holding <paramref name="path"/> as the system's file manager reports it, purgeable
    /// data included; null where the system has no such notion (Windows, Linux: there <see cref="DriveInfo"/>'s free
    /// space is the same figure), or the engine isn't loaded. May take a moment, so call it off the UI thread.
    /// </summary>
    public static VolumeSpace? GetSpace(string path)
    {
        if (string.IsNullOrEmpty(path) || !NativeEngine.IsAvailable) return null;
        return NativeMethods.GetVolumeSpace(path, out var s) == EsStatus.Ok && s.Total > 0
            ? new VolumeSpace(s.Total, s.Free, Math.Max(s.Available, s.Free))
            : null;
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
