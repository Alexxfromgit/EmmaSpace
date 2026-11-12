using CommunityToolkit.Mvvm.ComponentModel;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Platform;

namespace EmmaSpace.App.ViewModels;

public sealed class DriveViewModel : ObservableObject
{
    private bool _countsPurgeable; // the space is the system's own figure, purgeable data included (macOS)
    private readonly bool _measured; // macOS startup disk: Finder's figure is asked for, and the card has a purgeable line

    private readonly string _format; // file system, e.g. "apfs"
    private string _kind; // "Local disk", "External disk"…

    private DriveViewModel(string path, string title, string format, string kind, VolumeSpace space, bool isMeasuring)
    {
        Path = path;
        Title = title;
        _format = format;
        _kind = kind;
        Space = space;
        IsMeasuring = _measured = isMeasuring;
    }

    public string Path { get; }
    public string Title { get; private set; }

    public string Subtitle =>
        IsLocked ? "Locked · encrypted"
        : IsUnmounted ? (Volume?.IsExternal == true ? "Connected · not mounted" : "Not mounted")
        : string.Join(" · ", new[] { _format, _kind, IsBackup ? "Time Machine backups" : "" }.Where(p => p.Length > 0));

    /// <summary>macOS: an encrypted drive that is attached but not unlocked, so there is nothing to scan yet.</summary>
    public bool IsLocked { get; private init; }

    /// <summary>macOS: a drive that is attached and unlocked but not mounted, so it has no folder to scan.</summary>
    public bool IsUnmounted { get; private init; }

    /// <summary>Locked or not mounted: the card explains instead of scanning.</summary>
    public bool IsUnavailable => IsLocked || IsUnmounted;

    /// <summary>macOS: holds Time Machine backups, which are snapshots a scan can't see.</summary>
    public bool IsBackup { get; private set; }

    /// <summary>macOS: what diskutil says about this volume, once asked (off the UI thread).</summary>
    public MacVolume? Volume { get; private set; }

    /// <summary>On Windows and Linux, from DriveInfo (available = free); on macOS, as Finder reports it once measured.</summary>
    public VolumeSpace Space { get; private set; }

    /// <summary>
    /// macOS: Finder's figure, purgeable data included, is still being asked for off the UI thread. Until then the card
    /// shows no figures rather than DriveInfo's free space, which would disagree with Finder by many GB.
    /// </summary>
    public bool IsMeasuring { get; private set; }

    /// <summary>The name to use in a sentence: "the startup disk" for "/".</summary>
    public string Name => Path == "/" ? "the startup disk" : Title;

    public bool HasPurgeable => !IsMeasuring && Space.HasPurgeable;
    public bool HasUsage => Space.Total > 0;

    /// <summary>The bar keeps its place while measuring, unseen, so the card doesn't grow when the figures arrive.</summary>
    public double BarOpacity => IsMeasuring ? 0 : 1;

    /// <summary>The startup disk's second line: how much is purgeable. Shown, even while measuring, so its height is fixed.</summary>
    public bool ShowsPurgeableLine => _measured;

    /// <summary>Used as Finder and System Settings count it: purgeable data counts as available.</summary>
    public double UsedPercent => Space.Total == 0 ? 0 : Space.Used * 100.0 / Space.Total;

    /// <summary>Used including purgeable data, drawn lighter behind <see cref="UsedPercent"/>.</summary>
    public double UsedWithPurgeablePercent => Space.Total == 0 ? 0 : Space.UsedWithPurgeable * 100.0 / Space.Total;

    // macOS words it as Finder does, "available"; elsewhere free and available are the same figure, called free.
    public string UsageText => IsLocked ? "Unlock it to see its space and scan it"
        : IsUnmounted ? "Mount it to see its space and scan it"
        : IsMeasuring ? "Measuring available space…"
        : Space.Describe(_countsPurgeable || OperatingSystem.IsMacOS(), ByteSize.Format);

    public string PurgeableText => IsMeasuring ? "\u00A0" // keeps the line's height
        : _countsPurgeable ? Space.DescribePurgeable(ByteSize.Format)
        : "purgeable data unknown";

    /// <summary>What a screen reader says after the card's title: the figures the card shows.</summary>
    public string AccessibleSummary =>
        string.Join(", ", new[] { Subtitle, UsageText, ShowsPurgeableLine && !IsMeasuring ? PurgeableText : "" }.Where(t => t.Length > 0));

    /// <summary>
    /// Fills in the system's own figures (null: none to be had, keep DriveInfo's). Slow on some volumes, so
    /// <see cref="StorageInfo.GetSpace"/> is called off the UI thread and the result handed here, on the UI thread. The
    /// card updates in place, so a card that has keyboard focus keeps it.
    /// </summary>
    public void SetMeasured(VolumeSpace? space)
    {
        if (space is { } s) Space = s;
        else if (!IsMeasuring) return; // keep the figures shown rather than fall back
        _countsPurgeable = space is not null;
        IsMeasuring = false;
        foreach (var name in (string[])[nameof(Space), nameof(IsMeasuring), nameof(HasPurgeable), nameof(HasUsage), nameof(BarOpacity),
                     nameof(UsedPercent), nameof(UsedWithPurgeablePercent), nameof(UsageText), nameof(PurgeableText),
                     nameof(AccessibleSummary)])
            OnPropertyChanged(name);
    }

    /// <summary>
    /// Fills in what macOS's diskutil says about this volume: the name Finder shows, whether it is external, whether it
    /// holds Time Machine backups. Called on the UI thread with what was asked off it.
    /// </summary>
    public void SetVolume(MacVolume volume)
    {
        Volume = volume;
        Title = volume.Name;
        IsBackup = volume.IsBackup;
        if (volume.IsExternal) _kind = "External disk";
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsBackup));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(AccessibleSummary));
    }

    /// <summary>A card for an external drive that is attached but locked or not mounted: it can be explained, not scanned.</summary>
    public static DriveViewModel Unavailable(MacVolume volume) =>
        new("", volume.Name, volume.IsApfs ? "apfs" : "", volume.IsExternal ? "External disk" : "Disk", default, isMeasuring: false)
        {
            IsLocked = volume.IsLocked,
            IsUnmounted = !volume.IsLocked,
            Volume = volume,
        };

    /// <summary>The drives found, and the mounts left out because Finder hides them too (said so in a notice).</summary>
    public sealed record Discovery(IReadOnlyList<DriveViewModel> Drives, IReadOnlyList<string> HiddenMounts);

    /// <summary>The drives to offer, with DriveInfo's figures; those still <see cref="IsMeasuring"/> need <see cref="SetMeasured"/>.</summary>
    public static Discovery Discover()
    {
        var result = new List<DriveViewModel>();
        var hidden = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.Ram or DriveType.NoRootDirectory) continue;
                var path = d.RootDirectory.FullName;
                if (!OperatingSystem.IsWindows() && !IsInterestingUnixMount(path)) continue;
                // macOS mounts Time Machine backups for browsing at /Volumes/.timemachine/…; Finder hides such mounts, and
                // a card of their own would repeat the drive they come from. A notice names them instead.
                if (OperatingSystem.IsMacOS() && MacDisks.IsHiddenMount(path))
                {
                    hidden.Add(path);
                    continue;
                }
                var free = (ulong)d.AvailableFreeSpace;
                // macOS keeps purgeable data on the startup disk (Time Machine's local snapshots, iCloud copies, caches), so
                // only it is measured. Other drives keep DriveInfo's figure: asking about them could make macOS ask the
                // user to let Emma Space access a removable volume, and a stalled one would hold a thread.
                var measure = OperatingSystem.IsMacOS() && path == "/" && NativeEngine.IsAvailable;
                result.Add(new DriveViewModel(path, TitleOf(d), d.DriveFormat, Kind(d.DriveType),
                    new VolumeSpace((ulong)d.TotalSize, free, free), isMeasuring: measure));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home))
            result.Add(new DriveViewModel(home, "Your user folder", "", home, default, isMeasuring: false) { IsUserFolder = true });
        return new Discovery(result, hidden);
    }

    private static string TitleOf(DriveInfo d)
    {
        if (OperatingSystem.IsWindows())
        {
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? Kind(d.DriveType) : d.VolumeLabel;
            return $"{label} ({d.Name.TrimEnd('\\')})";
        }
        var path = d.RootDirectory.FullName;
        // On macOS and Linux .NET's volume label is the mount path; the folder's name is what Finder and Files show,
        // until diskutil's name replaces it (macOS). "/" is the startup disk, linked from /Volumes under its name.
        if (path == "/") return StartupDiskName() ?? "/";
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }

    // macOS links /Volumes/<startup disk name> to "/".
    private static string? StartupDiskName()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            return new DirectoryInfo("/Volumes").EnumerateFileSystemInfos()
                .FirstOrDefault(e => e.LinkTarget == "/")?.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A mount the start page lists or mentions: any drive on Windows; "/", /Volumes, /media and /mnt elsewhere.</summary>
    public static bool IsListedMount(string path) => OperatingSystem.IsWindows() || IsInterestingUnixMount(path);

    /// <summary>The "Your user folder" card.</summary>
    public bool IsUserFolder { get; private init; }

    private static bool IsInterestingUnixMount(string path) =>
        path == "/" || path.StartsWith("/Volumes/", StringComparison.Ordinal) || path.StartsWith("/media/", StringComparison.Ordinal) ||
        path.StartsWith("/mnt/", StringComparison.Ordinal);

    private static string Kind(DriveType type) => type switch
    {
        DriveType.Fixed => "Local disk",
        DriveType.Removable => "Removable",
        DriveType.Network => "Network",
        _ => "Drive",
    };
}
