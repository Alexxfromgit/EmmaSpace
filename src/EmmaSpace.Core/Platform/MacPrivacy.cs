using System.Runtime.InteropServices;

namespace EmmaSpace.Core.Platform;

/// <summary>
/// macOS keeps some data private (Mail, Messages, Safari, other apps' containers, Time Machine) unless the user gives an
/// app Full Disk Access. Without it those folders count as "could not be read", so the app points the user to the
/// setting.
/// </summary>
public static partial class MacPrivacy
{
    /// <summary>Opens System Settings → Privacy &amp; Security → Full Disk Access.</summary>
    public const string FullDiskAccessSettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles";

    // Readable only with Full Disk Access, whoever the user is; present on every Mac.
    private const string ProbePath = "/Library/Application Support/com.apple.TCC/TCC.db";

    /// <summary>
    /// True if this process has Full Disk Access, false if it hasn't, null when that can't be told (not macOS, or the
    /// probe file is missing). Opens the probe read-only with open(2) and closes it at once: no lock, nothing read.
    /// </summary>
    public static bool? HasFullDiskAccess()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var fd = open(ProbePath, ORdOnly | OCloExec);
        if (fd >= 0)
        {
            close(fd);
            return true;
        }
        return Marshal.GetLastPInvokeError() is EPerm or EAcces ? false : null;
    }

    /// <summary>
    /// True if a scan of <paramref name="root"/> reaches data macOS keeps private: the whole disk, the users' folders,
    /// <paramref name="home"/> or a folder holding it, or anything in its Library.
    /// </summary>
    public static bool MayCoverProtectedData(string root, string home)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(home)) return false;
        root = Normalize(root);
        home = Normalize(home);
        var library = home == "/" ? "/Library" : home + "/Library";
        return IsSameOrInside(home, root) || IsSameOrInside(root, library) ||
               IsSameOrInside("/Library/Application Support/com.apple.TCC", root) ||
               root == "/System/Volumes/Data";
    }

    /// <summary>Opens the Full Disk Access pane. Throws IOException if System Settings could not be started.</summary>
    public static void OpenFullDiskAccessSettings() =>
        FileActions.RunLauncher("/usr/bin/open", FileActions.LauncherTimeout, FullDiskAccessSettingsUrl);

    private static string Normalize(string path)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        return path.Length == 0 ? "/" : path;
    }

    // Folder names are case-insensitive on a default APFS volume.
    private static bool IsSameOrInside(string path, string folder) =>
        folder == "/" ||
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private const int ORdOnly = 0x0000, OCloExec = 0x01000000; // <fcntl.h> on macOS
    private const int EPerm = 1, EAcces = 13;

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc")]
    private static partial int close(int fd);
}
