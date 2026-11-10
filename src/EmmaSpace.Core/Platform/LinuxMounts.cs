using System.Runtime.InteropServices;
using System.Text;

namespace EmmaSpace.Core.Platform;

/// <summary>A mounted file system: where it is mounted and its type (ext4, btrfs, tmpfs, ...).</summary>
/// <param name="Path">The folder that was looked up, with symbolic links resolved.</param>
public sealed record MountedFileSystem(string MountPoint, string Type, string Path)
{
    /// <summary>ext2, ext3 and ext4, which the Linux fast scan reads directly.</summary>
    public bool IsExt => Type is "ext4" or "ext3" or "ext2";

    /// <summary>True when the folder is where the file system is mounted, not a folder inside it.</summary>
    public bool IsMountPoint => Path == MountPoint;
}

/// <summary>The kernel's identity of the mount that holds a path, as far as it reported it.</summary>
internal readonly record struct MountIdentity(ulong? MountId, uint DeviceMajor, uint DeviceMinor);

/// <summary>Which file system holds a folder, from /proc/self/mountinfo (Linux).</summary>
public static partial class LinuxMounts
{
    public static MountedFileSystem? Find(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            // The native scan resolves symbolic links too (realpath), so both look at the same folder.
            var real = RealPath(path) ?? System.IO.Path.GetFullPath(path);
            return Find(real, File.ReadLines("/proc/self/mountinfo"), Identify(real));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The mount that holds the absolute, resolved <paramref name="path"/>. With the mount ID from statx() that is
    /// exact, even for mounts hidden under later ones; otherwise the longest mount point that contains the path (on
    /// the path's device when known), the last one listed winning among mounts on the same point.
    /// </summary>
    internal static MountedFileSystem? Find(string path, IEnumerable<string> mountinfo, MountIdentity? identity = null)
    {
        var full = path.Length > 1 ? path.TrimEnd('/') : path;
        MountedFileSystem? best = null;
        foreach (var line in mountinfo)
        {
            // id parent major:minor root mount-point options [optional...] - type source super-options
            var fields = line.Split(' ');
            var dash = Array.IndexOf(fields, "-");
            if (fields.Length < 5 || dash < 5 || dash + 1 >= fields.Length) continue;
            var point = Unescape(fields[4]);
            var mount = new MountedFileSystem(point, fields[dash + 1], full);
            if (identity is { MountId: { } id })
            {
                if (ulong.TryParse(fields[0], out var lineId) && lineId == id) return mount;
                continue;
            }
            if (identity is { } dev && fields[2] != $"{dev.DeviceMajor}:{dev.DeviceMinor}") continue;
            var contains = point == "/" || full == point || full.StartsWith(point + "/", StringComparison.Ordinal);
            if (contains && (best is null || point.Length >= best.MountPoint.Length)) best = mount;
        }
        return best;
    }

    // Space, tab, newline and backslash are written as \ooo.
    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var input = Encoding.UTF8.GetBytes(s);
        var bytes = new List<byte>(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '\\' && i + 3 < input.Length && IsOctal(input[i + 1]) && IsOctal(input[i + 2]) && IsOctal(input[i + 3]))
            {
                bytes.Add((byte)((input[i + 1] - '0') * 64 + (input[i + 2] - '0') * 8 + (input[i + 3] - '0')));
                i += 3;
            }
            else
            {
                bytes.Add(input[i]);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsOctal(byte c) => c is >= (byte)'0' and <= (byte)'7';

    // ---- libc

    private static string? RealPath(string path)
    {
        var resolved = realpath(path, 0);
        if (resolved == 0) return null;
        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            free(resolved);
        }
    }

    // struct statx (include/uapi/linux/stat.h) is the same on every architecture: 256 bytes.
    private const int AtFdCwd = -100;
    private const uint StatxMntId = 0x1000;
    private const int StatxSize = 256, MaskOffset = 0, DevMajorOffset = 136, DevMinorOffset = 140, MntIdOffset = 144;

    private static unsafe MountIdentity? Identify(string path)
    {
        var buffer = stackalloc byte[StatxSize];
        if (statx(AtFdCwd, path, 0, StatxMntId, buffer) != 0) return null;
        var mask = *(uint*)(buffer + MaskOffset);
        ulong? mountId = (mask & StatxMntId) != 0 ? *(ulong*)(buffer + MntIdOffset) : null; // Linux 5.8+
        return new MountIdentity(mountId, *(uint*)(buffer + DevMajorOffset), *(uint*)(buffer + DevMinorOffset));
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint realpath(string path, nint resolved);

    [LibraryImport("libc")]
    private static partial void free(nint pointer);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int statx(int dirfd, string path, int flags, uint mask, byte* buffer);
}
