using CommunityToolkit.Mvvm.ComponentModel;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Platform;

namespace EmmaSpace.App.ViewModels;

public sealed class DriveViewModel : ObservableObject
{
    private bool _countsPurgeable; // the space is the system's own figure, purgeable data included (macOS)
    private readonly bool _measured; // macOS startup disk: Finder's figure is asked for, and the card has a purgeable line

    private DriveViewModel(string path, string title, string subtitle, VolumeSpace space, bool isMeasuring)
    {
        Path = path;
        Title = title;
        Subtitle = subtitle;
        Space = space;
        IsMeasuring = _measured = isMeasuring;
    }

    public string Path { get; }
    public string Title { get; }
    public string Subtitle { get; }

    /// <summary>On Windows and Linux, from DriveInfo (available = free); on macOS, as Finder reports it once measured.</summary>
    public VolumeSpace Space { get; private set; }

    /// <summary>
    /// macOS: Finder's figure, purgeable data included, is still being asked for off the UI thread. Until then the card
    /// shows no figures rather than DriveInfo's free space, which would disagree with Finder by many GB.
    /// </summary>
    public bool IsMeasuring { get; private set; }

    /// <summary>The name to use in a sentence: the startup disk rather than "/".</summary>
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
    public string UsageText => IsMeasuring ? "Measuring available space…"
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
        if (!IsMeasuring) return;
        if (space is { } s) Space = s;
        _countsPurgeable = space is not null;
        IsMeasuring = false;
        foreach (var name in (string[])[nameof(Space), nameof(IsMeasuring), nameof(HasPurgeable), nameof(HasUsage), nameof(BarOpacity),
                     nameof(UsedPercent), nameof(UsedWithPurgeablePercent), nameof(UsageText), nameof(PurgeableText),
                     nameof(AccessibleSummary)])
            OnPropertyChanged(name);
    }

    /// <summary>The drives to offer, with DriveInfo's figures; those still <see cref="IsMeasuring"/> need <see cref="SetMeasured"/>.</summary>
    public static IReadOnlyList<DriveViewModel> Discover()
    {
        var result = new List<DriveViewModel>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.Ram or DriveType.NoRootDirectory) continue;
                if (!OperatingSystem.IsWindows() && !IsInterestingUnixMount(d.RootDirectory.FullName)) continue;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? Kind(d.DriveType) : d.VolumeLabel;
                var title = OperatingSystem.IsWindows() ? $"{label} ({d.Name.TrimEnd('\\')})" : label;
                var free = (ulong)d.AvailableFreeSpace;
                // macOS keeps purgeable data on the startup disk (Time Machine's local snapshots, iCloud copies, caches), so
                // only it is measured. Other drives keep DriveInfo's figure: asking about them could make macOS ask the
                // user to let Emma Space access a removable volume, and a stalled one would hold a thread.
                var measure = OperatingSystem.IsMacOS() && d.RootDirectory.FullName == "/" && NativeEngine.IsAvailable;
                result.Add(new DriveViewModel(d.RootDirectory.FullName, title, $"{d.DriveFormat} · {Kind(d.DriveType)}",
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
            result.Add(new DriveViewModel(home, "Your user folder", home, default, isMeasuring: false));
        return result;
    }

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
