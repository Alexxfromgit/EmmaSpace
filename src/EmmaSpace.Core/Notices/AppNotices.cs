using EmmaSpace.Core.Platform;

namespace EmmaSpace.Core.Notices;

/// <summary>
/// The notices Emma Space shows, worded in one place. Each says what is going on, why Emma Space shows what it shows
/// (or can't show something), and what the user can do. Messages stay short and free of long paths, so they read well
/// aloud too; paths and the full story go in the details.
/// </summary>
public static class AppNotices
{
    /// <summary>macOS: Finder's available space includes purgeable data, so it is more than the empty space.</summary>
    public static Notice Purgeable(string disk, ulong free, ulong purgeable, Func<ulong, string> format) =>
        new("space.purgeable", NoticeLevel.Info, "Available space includes purgeable data", MacStorage.Note(disk, purgeable, format))
        {
            Details = MacStorage.Explanation(disk, free, purgeable, format),
            Actions = [NoticeActions.LearnMoreAction, new(NoticeActions.OpenStorageSettings, "Open Storage Settings")],
        };

    /// <summary>An encrypted volume is attached but not unlocked, so macOS hasn't mounted it.</summary>
    public static Notice LockedDrive(MacVolume volume) =>
        new($"drive.locked:{volume.Key}", NoticeLevel.Limitation, $"{volume.Name} is locked",
            $"{volume.Name} is encrypted and hasn't been unlocked yet, so macOS hasn't opened it: Emma Space can't see its files " +
            "or free space. Unlock it with its password and it appears here, ready to scan.")
        {
            Details =
                "Encrypted drives stay locked until their password is entered. macOS usually asks for it when the drive is " +
                $"connected; if that prompt was closed, open Disk Utility, select {volume.Name} and click Mount to be asked " +
                "again.\n\n" +
                "Emma Space never asks for, sees or keeps drive passwords: macOS does the unlocking. Once the drive is " +
                "unlocked it appears on this page by itself.",
            Actions = [NoticeActions.LearnMoreAction, new(NoticeActions.OpenDiskUtility, "Open Disk Utility")],
        };

    /// <summary>A volume that is attached and unlocked but not mounted, so it has no folder to scan.</summary>
    public static Notice UnmountedDrive(MacVolume volume) =>
        new($"drive.unmounted:{volume.Key}", NoticeLevel.Limitation,
            volume.IsExternal ? $"{volume.Name} is connected but not mounted" : $"{volume.Name} is not mounted",
            $"macOS sees {volume.Name} but hasn't made its files available as a folder, so Emma Space has nothing to scan. " +
            "Mount it and it appears here, ready to scan.")
        {
            Details =
                "A volume is mounted when macOS makes its files available as a folder (under /Volumes) that apps can read. " +
                $"{volume.Name} isn't: perhaps it was ejected while its disk stayed plugged in, or unmounted in Disk " +
                "Utility.\n\n" +
                "Mount does what Disk Utility's Mount button does. Emma Space then only reads the drive: scanning never " +
                "changes anything on it.",
            Actions = [new(NoticeActions.MountVolume, "Mount", volume.DeviceId), NoticeActions.LearnMoreAction],
        };

    /// <summary>
    /// A Time Machine backup disk, before a scan. <paramref name="backupMounts"/> are backups of it macOS mounted for
    /// browsing, which the start page doesn't list as drives of their own.
    /// </summary>
    public static Notice BackupDrive(MacVolume volume, IReadOnlyCollection<string> backupMounts, Func<ulong, string> format) =>
        new($"drive.backup:{volume.Key}", NoticeLevel.Limitation, $"{volume.Name} holds Time Machine backups",
            volume.IsApfs
                ? "Time Machine keeps its backups as snapshots of the disk, which a scan can't look inside: a scan maps only " +
                  $"what is stored as ordinary files and folders, while {volume.Name} uses {format(volume.CapacityInUse)} in all."
                : "macOS lets only apps with Full Disk Access look inside its backups. With it, a scan maps them as folders, " +
                  "counting a file once even when many backups share it.")
        {
            Details = BackupDetails(volume, backupMounts),
            Actions = [NoticeActions.LearnMoreAction, new(NoticeActions.OpenTimeMachineSettings, "Open Time Machine Settings")],
        };

    /// <summary>
    /// After scanning a Time Machine backup disk: why the map shows so little of what the disk uses. When the scan couldn't
    /// open the disk at all (<paramref name="opened"/> false), the Full Disk Access notice beside it says why.
    /// </summary>
    public static Notice BackupScan(MacVolume volume, ulong scannedOnDisk, bool opened, Func<ulong, string> format) =>
        new($"scan.backup-disk:{volume.Key}", NoticeLevel.Limitation, $"{volume.Name} holds Time Machine backups",
            !volume.IsApfs
                ? "Its backups are folders that share unchanged files, which the map counts once."
                : opened
                    ? $"The map shows the {format(scannedOnDisk)} stored as ordinary files and folders. {volume.Name} uses " +
                      $"{format(volume.CapacityInUse)} in all: the rest is held in Time Machine's snapshots, which a scan can't see."
                    : "macOS didn't let Emma Space open it, so the map is empty. Even with access, the map would show only " +
                      $"what is stored as ordinary files and folders, not the backups in Time Machine's snapshots, which " +
                      $"take up much of the {format(volume.CapacityInUse)} {volume.Name} uses.")
        {
            Details = BackupDetails(volume, []),
            Actions = [NoticeActions.LearnMoreAction, new(NoticeActions.OpenTimeMachineSettings, "Open Time Machine Settings")],
            CanDismiss = true,
        };

    private static string BackupDetails(MacVolume volume, IReadOnlyCollection<string> backupMounts)
    {
        var text = volume.IsApfs
            ? "Time Machine keeps each backup as an APFS snapshot: a frozen view of the disk that shares unchanged data " +
              "with the other backups. Snapshots aren't files or folders, so scanning the disk can't show what they " +
              "contain or how much each one takes.\n\n"
            : "This disk keeps its backups the older way, as dated folders in which files that didn't change are shared " +
              "between backups (hard links). Emma Space counts each shared file once.\n\n";
        text +=
            "macOS protects backup disks: Emma Space can read what is on them only with Full Disk Access.\n\n" +
            "To look through your backups, use Browse Time Machine Backups from the Time Machine menu. Time Machine removes " +
            "the oldest backups by itself when the disk gets full.";
        if (backupMounts.Count > 0)
            text += $"\n\nmacOS has also mounted {(backupMounts.Count == 1 ? "one" : backupMounts.Count.ToString("N0"))} of its " +
                    $"backups for browsing ({string.Join(", ", backupMounts)}). The start page doesn't list " +
                    $"{(backupMounts.Count == 1 ? "it" : "them")} as a separate drive: " +
                    $"{(backupMounts.Count == 1 ? "it is" : "they are")} part of {volume.Name}.";
        return text;
    }

    /// <summary>
    /// Mounts Finder hides too, that no drive card on the start page accounts for. <paramref name="backupOf"/> gives, for
    /// a Time Machine backup mounted for browsing, the name of the drive it comes from (null when unknown).
    /// </summary>
    public static Notice HiddenMounts(IReadOnlyCollection<string> paths, Func<string, string?> backupOf)
    {
        var owners = paths.Select(backupOf).OfType<string>().Distinct().ToList();
        var what = paths.Count == 1 && owners.Count == 1 ? $"a Time Machine backup of {owners[0]} for browsing"
            : paths.Count == 1 ? "a volume that Finder hides too"
            : $"{paths.Count:N0} volumes that Finder hides too";
        return new("drive.hidden-mounts", NoticeLevel.Info,
            paths.Count == 1 ? "A hidden volume isn't listed" : $"{paths.Count:N0} hidden volumes aren't listed",
            $"macOS has mounted {what}. Hidden volumes belong to other drives or to the system, so the start page leaves them " +
            "out. To scan one anyway, type its path in the box above.")
        {
            Details = "Hidden volumes mounted now:\n" + string.Join("\n", paths.Select(p =>
                backupOf(p) is { } drive ? $"• {p} (a Time Machine backup of {drive}, mounted for browsing)" : $"• {p}")) +
                "\n\nFinder doesn't show volumes mounted under a name that starts with a dot. Time Machine mounts its backups " +
                "that way, for instance while you browse them.",
            Actions = [NoticeActions.LearnMoreAction],
        };
    }

    /// <summary>macOS's disk tool didn't answer, so locked, unmounted and backup disks can't be pointed out.</summary>
    public static Notice DiskInfoUnavailable() =>
        new("drive.disk-info-unavailable", NoticeLevel.Info, "Some drive details are missing",
            "Emma Space couldn't ask macOS about its disks (diskutil didn't answer), so it can't point out locked or " +
            "unmounted drives or Time Machine backup disks right now. The drives above can still be scanned.")
        {
            CanDismiss = true,
        };

    /// <summary>macOS: the scan of <paramref name="root"/> missed private folders because Emma Space has no Full Disk Access.</summary>
    public static Notice FullDiskAccess(string root) =>
        new($"scan.full-disk-access:{root}", NoticeLevel.Warning, "Some folders could not be read",
            "macOS keeps Mail, Messages, Safari, other apps' data and Time Machine backups private unless Emma Space has Full " +
            "Disk Access. To include them, add Emma Space under Full Disk Access in System Settings (with + if it isn't " +
            "listed), turn it on, then quit and reopen Emma Space and scan again.")
        {
            Actions = [new(NoticeActions.OpenPrivacySettings, "Open Privacy Settings")],
            CanDismiss = true,
        };

    /// <summary>The scanned folder itself couldn't be listed, so the map is empty.</summary>
    public static Notice RootUnreadable(string root) =>
        new($"scan.root-unreadable:{root}", NoticeLevel.Warning, $"{root} could not be opened",
            "The system didn't let Emma Space list it, so there is nothing to map. Check that your account may open it and, " +
            "for a drive, that it is still connected.")
        {
            CanDismiss = true,
        };

    /// <summary>"1 folder", "2 folders".</summary>
    public static string Count(long n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";
}
