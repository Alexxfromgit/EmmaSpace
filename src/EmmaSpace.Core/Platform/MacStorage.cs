namespace EmmaSpace.Core.Platform;

/// <summary>
/// macOS counts purgeable data (Time Machine snapshots kept on the Mac, iCloud copies, caches) as available space,
/// because it removes that data by itself when an app needs the room. This explains the difference to the user and
/// points them to Storage settings, which lists what they can clear themselves.
/// </summary>
public static class MacStorage
{
    /// <summary>Opens System Settings → General → Storage (macOS 13 and later).</summary>
    public const string StorageSettingsUrl = "x-apple.systempreferences:com.apple.settings.Storage";

    // Before macOS 13 storage lived in About This Mac → Storage, which is this app; System Preferences had no pane for it.
    private const string StorageManagementApp = "/System/Library/CoreServices/Applications/Storage Management.app";

    /// <summary>The short note under the drives on the start page; <paramref name="disk"/> names the drive in a sentence.</summary>
    public static string Note(string disk, ulong purgeable, Func<ulong, string> format) =>
        $"Available space on {disk} includes {format(purgeable)} of purgeable data that macOS removes by itself when " +
        "space is needed, the same way Finder counts it.";

    /// <summary>The full explanation, for the disk with these figures.</summary>
    public static string Explanation(string disk, ulong free, ulong purgeable, Func<ulong, string> format) =>
        $"{format(free)} of {disk} is empty right now. Another {format(purgeable)} is purgeable: data macOS can remove " +
        "on its own, such as Time Machine snapshots kept on this Mac, iCloud files and photos that are safely stored in " +
        "iCloud, and caches. Finder and System Settings count both as available, and so does Emma Space.\n\n" +
        "You don't need to do anything. As soon as an app needs the room, macOS clears purgeable data, so all of the " +
        "available space can be used. Storage settings lists recommendations if you'd like to free space yourself.\n\n" +
        "Purgeable files, such as caches and iCloud copies, are still on the disk, so a scan finds them and shows them " +
        "in the map, while System Settings leaves them out of \"used\". Time Machine snapshots are not files, so a scan " +
        "doesn't see them. That is why a scan of the whole disk rarely matches what System Settings shows as used.";

    /// <summary>Opens Storage settings. Throws IOException if they could not be opened.</summary>
    public static void OpenStorageSettings() =>
        FileActions.RunLauncher("/usr/bin/open", FileActions.LauncherTimeout,
            OperatingSystem.IsMacOSVersionAtLeast(13) || !Directory.Exists(StorageManagementApp)
                ? StorageSettingsUrl
                : StorageManagementApp);
}
