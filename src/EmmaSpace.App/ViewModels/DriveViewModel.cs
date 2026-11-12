using CommunityToolkit.Mvvm.ComponentModel;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Platform;

namespace EmmaSpace.App.ViewModels;

public sealed class DriveViewModel : ObservableObject
{
    private bool _countsPurgeable; // the space is the system's own figure, purgeable data included (macOS)

    private DriveViewModel(string path, string title, string subtitle, VolumeSpace space, bool isMeasuring)
    {
        Path = path;
        Title = title;
        Subtitle = subtitle;
        Space = space;
        IsMeasuring = isMeasuring;
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
    public bool HasUsage => !IsMeasuring && Space.Total > 0;

    /// <summary>Used as Finder and System Settings count it: purgeable data counts as available.</summary>
    public double UsedPercent => Space.Total == 0 ? 0 : Space.Used * 100.0 / Space.Total;

    /// <summary>Used including purgeable data, drawn lighter behind <see cref="UsedPercent"/>.</summary>
    public double UsedWithPurgeablePercent => Space.Total == 0 ? 0 : Space.UsedWithPurgeable * 100.0 / Space.Total;

    public string UsageText => IsMeasuring ? "" : Space.Describe(_countsPurgeable, ByteSize.Format);
    public string PurgeableText => IsMeasuring ? "" : Space.DescribePurgeable(ByteSize.Format);

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
        foreach (var name in (string[])[nameof(Space), nameof(IsMeasuring), nameof(HasPurgeable), nameof(HasUsage),
                     nameof(UsedPercent), nameof(UsedWithPurgeablePercent), nameof(UsageText), nameof(PurgeableText)])
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
                // Only macOS counts purgeable data, and not on network shares, which would only make the wait longer.
                var measure = OperatingSystem.IsMacOS() && d.DriveType != DriveType.Network && NativeEngine.IsAvailable;
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
