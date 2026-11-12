using System.Diagnostics;

namespace EmmaSpace.Core.Platform;

/// <summary>A volume as macOS's <c>diskutil</c> describes it.</summary>
/// <param name="DeviceId">BSD name, e.g. "disk15s2".</param>
/// <param name="Uuid">The volume's UUID; empty when diskutil gives none.</param>
/// <param name="MountPoint">Where it is mounted; null when it isn't (for instance while it is locked).</param>
/// <param name="IsLocked">Encrypted and not unlocked yet: macOS can't mount it until its password is entered.</param>
/// <param name="IsBackup">
/// Holds Time Machine backups: APFS role "Backup", or mounted where Time Machine backs up to. On APFS the backups are
/// snapshots, not files.
/// </param>
/// <param name="IsExternal">On a physical disk outside the Mac (USB, Thunderbolt…), not a disk image.</param>
/// <param name="CapacityInUse">Bytes the volume uses, snapshots included; 0 when unknown (not APFS).</param>
/// <param name="HoldsUserFiles">
/// A volume people keep files on: APFS roles none, Data or Backup, or not APFS at all. macOS's own volumes (System,
/// Preboot, Recovery, VM…) stay unmounted on purpose and are never offered.
/// </param>
/// <param name="IsApfs">An APFS volume; otherwise a partition with another file system (ExFAT, HFS+, NTFS…).</param>
public sealed record MacVolume(string DeviceId, string Uuid, string Name, string? MountPoint, bool IsLocked, bool IsEncrypted,
    bool IsBackup, bool IsExternal, ulong CapacityInUse, bool HoldsUserFiles = true, bool IsApfs = true)
{
    /// <summary>Holds files but isn't mounted: locked, or ejected while its disk stays attached. Its card explains why.</summary>
    public bool IsUnavailable => HoldsUserFiles && MountPoint is null;

    /// <summary>Identifies it in notice keys: its UUID, else its device.</summary>
    public string Key => Uuid.Length > 0 ? Uuid : DeviceId;
}

/// <summary>
/// What macOS knows about its disks beyond the mounted folders .NET sees: locked (encrypted, not yet unlocked) or
/// unmounted volumes, Time Machine backup disks, their names and which disks are external. From <c>diskutil … -plist</c>
/// and <c>tmutil</c>, which take a fraction of a second, so call <see cref="List"/> off the UI thread.
/// </summary>
public static class MacDisks
{
    private const string DiskUtil = "/usr/sbin/diskutil";
    private const string TmUtil = "/usr/bin/tmutil";

    // Time Machine mounts the backups it is looking at here, one folder per backup volume UUID; Finder hides them.
    private const string TimeMachineMounts = "/Volumes/.timemachine/";

    // Partition types that are never a volume of files: the disk's own housekeeping.
    private static readonly HashSet<string> s_notVolumes =
        ["EFI", "Apple_APFS", "Apple_Boot", "Apple_partition_map", "Apple_Free", "Apple_CoreStorage", "Apple_APFS_Recovery",
         "Microsoft Reserved", "Linux Swap"];

    /// <summary>The volumes, or null when they can't be listed (not macOS, or diskutil failed).</summary>
    public static IReadOnlyList<MacVolume>? List()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var apfs = Run(DiskUtil, "apfs", "list", "-plist");
        var disks = Run(DiskUtil, "list", "-plist");
        if (apfs is null || disks is null) return null;
        // These two fail when there is nothing to report (diskutil exits 1 when no external disk is attached; tmutil when
        // no backup disk is set up), which is an answer too: no external disks, no backup destinations.
        var external = Run(DiskUtil, "list", "-plist", "external", "physical");
        var destinations = Run(TmUtil, "destinationinfo", "-X");
        try
        {
            return Parse(apfs, disks, external, destinations);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Volumes from the output of <c>diskutil apfs list -plist</c> (names, roles, lock state), <c>diskutil list -plist</c>
    /// (mount points), <c>diskutil list -plist external physical</c> (which disks are external, and their other
    /// partitions; null: none) and <c>tmutil destinationinfo -X</c> (where Time Machine backs up to; null: nowhere).
    /// </summary>
    internal static IReadOnlyList<MacVolume> Parse(string apfsList, string diskList, string? externalPhysical,
        string? timeMachineDestinations = null)
    {
        var mounts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var disk in PropertyList.Dicts(PropertyList.Parse(diskList), "AllDisksAndPartitions"))
        foreach (var volume in PropertyList.Dicts(disk, "APFSVolumes").Concat(PropertyList.Dicts(disk, "Partitions")))
            if (PropertyList.String(volume, "DeviceIdentifier") is { } id && PropertyList.String(volume, "MountPoint") is { Length: > 0 } at)
                mounts[id] = at;

        var externalPlist = externalPhysical is null ? null : PropertyList.Parse(externalPhysical);
        var externalDisks = PropertyList.Strings(externalPlist, "WholeDisks").ToHashSet(StringComparer.Ordinal);
        var backupMounts = timeMachineDestinations is null
            ? []
            : PropertyList.Dicts(PropertyList.Parse(timeMachineDestinations), "Destinations")
                .Select(d => PropertyList.String(d, "MountPoint")).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var result = new List<MacVolume>();
        foreach (var container in PropertyList.Dicts(PropertyList.Parse(apfsList), "Containers"))
        {
            // A container lives on one or more partitions ("disk14s2"); it is external when those are on an external disk.
            var external = PropertyList.Dicts(container, "PhysicalStores")
                .Select(s => PropertyList.String(s, "DeviceIdentifier"))
                .Any(store => store is not null && externalDisks.Contains(WholeDisk(store)));
            foreach (var volume in PropertyList.Dicts(container, "Volumes"))
            {
                if (PropertyList.String(volume, "DeviceIdentifier") is not { } id) continue;
                var roles = PropertyList.Strings(volume, "Roles").ToList();
                var mount = mounts.GetValueOrDefault(id);
                result.Add(new MacVolume(id, PropertyList.String(volume, "APFSVolumeUUID") ?? "",
                    PropertyList.String(volume, "Name") ?? id, mount,
                    PropertyList.Bool(volume, "Locked"), PropertyList.Bool(volume, "Encryption") || PropertyList.Bool(volume, "FileVault"),
                    roles.Contains("Backup") || (mount is not null && backupMounts.Contains(mount)), external,
                    PropertyList.UInt(volume, "CapacityInUse"), roles.All(r => r is "Data" or "Backup")));
            }
        }

        // Other file systems on external disks (ExFAT, HFS+, NTFS…): diskutil apfs doesn't list them.
        foreach (var disk in PropertyList.Dicts(externalPlist, "AllDisksAndPartitions"))
        foreach (var part in PropertyList.Dicts(disk, "Partitions"))
        {
            if (PropertyList.String(part, "DeviceIdentifier") is not { } id || PropertyList.String(part, "Content") is not { } content ||
                s_notVolumes.Contains(content))
                continue;
            var mount = mounts.GetValueOrDefault(id);
            if (mount is null && PropertyList.String(part, "MountPoint") is { Length: > 0 } m) mount = m;
            result.Add(new MacVolume(id, PropertyList.String(part, "VolumeUUID") ?? PropertyList.String(part, "DiskUUID") ?? "",
                PropertyList.String(part, "VolumeName") is { Length: > 0 } name ? name : id, mount, IsLocked: false,
                IsEncrypted: false, mount is not null && backupMounts.Contains(mount), IsExternal: true, CapacityInUse: 0,
                HoldsUserFiles: true, IsApfs: false));
        }
        return result;
    }

    /// <summary>"disk14s2" → "disk14".</summary>
    internal static string WholeDisk(string device)
    {
        var s = device.IndexOf('s', "disk".Length);
        return s > 0 ? device[..s] : device;
    }

    /// <summary>
    /// A mount Finder doesn't show, e.g. a Time Machine backup macOS mounted for browsing: anything below /Volumes whose
    /// name starts with a dot.
    /// </summary>
    public static bool IsHiddenMount(string path) =>
        path.StartsWith("/Volumes/.", StringComparison.Ordinal);

    /// <summary>
    /// For a backup Time Machine mounted at /Volumes/.timemachine/&lt;volume UUID&gt;/…, the UUID of the backup volume
    /// it comes from; null for any other path.
    /// </summary>
    public static string? TimeMachineMountSource(string path)
    {
        if (!path.StartsWith(TimeMachineMounts, StringComparison.Ordinal)) return null;
        var rest = path[TimeMachineMounts.Length..];
        var slash = rest.IndexOf('/');
        var uuid = slash < 0 ? rest : rest[..slash];
        return Guid.TryParse(uuid, out _) ? uuid : null;
    }

    /// <summary>
    /// Changes when a disk is attached or detached (its whole-disk /dev/diskN entry comes and goes), even one that is
    /// locked and so mounts nothing. Cheap: one listing of /dev. Partitions are left out: they come with their disk.
    /// </summary>
    public static string DeviceSignature()
    {
        if (!OperatingSystem.IsMacOS()) return "";
        try
        {
            var names = Directory.GetFiles("/dev", "disk*").Where(n => !Path.GetFileName(n).AsSpan(4).Contains('s')).ToArray();
            Array.Sort(names, StringComparer.Ordinal);
            return string.Join('\n', names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    /// Mounts an unlocked volume that is attached but not mounted (what Disk Utility's Mount does). Throws
    /// <see cref="IOException"/> with diskutil's message when it can't. A locked volume needs its password first, which
    /// only macOS asks for: Emma Space never handles drive passwords.
    /// </summary>
    public static void Mount(string deviceId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(deviceId, @"\Adisk[0-9]+s[0-9]+\z"))
            throw new ArgumentException($"Not a volume: {deviceId}", nameof(deviceId));
        var (ok, output) = RunWithOutput(DiskUtil, TimeSpan.FromSeconds(30), "mount", deviceId);
        if (!ok) throw new IOException(string.IsNullOrWhiteSpace(output) ? $"diskutil could not mount {deviceId}." : output.Trim());
    }

    private static string? Run(string tool, params string[] args)
    {
        var (ok, output) = RunWithOutput(tool, TimeSpan.FromSeconds(10), args);
        return ok ? output : null;
    }

    // The tool's output when it succeeds, else its error message.
    private static (bool Ok, string Output) RunWithOutput(string tool, TimeSpan timeout, params string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return (false, "");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync(); // drained so a chatty diskutil can't block on a full pipe
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                return (false, $"{Path.GetFileName(tool)} did not answer in time.");
            }
            return process.ExitCode == 0 ? (true, output.Result) : (false, error.Result.Length > 0 ? error.Result : output.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
