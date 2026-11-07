using EmmaSpace.Core.Formatting;

namespace EmmaSpace.App.ViewModels;

public sealed class DriveViewModel
{
    private DriveViewModel(string path, string title, string subtitle, ulong total, ulong free)
    {
        Path = path;
        Title = title;
        Subtitle = subtitle;
        Total = total;
        Free = free;
    }

    public string Path { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public ulong Total { get; }
    public ulong Free { get; }
    public bool HasUsage => Total > 0;
    public double UsedPercent => Total == 0 ? 0 : (Total - Free) * 100.0 / Total;
    public string UsageText => Total == 0 ? "" : $"{ByteSize.Format(Free)} free of {ByteSize.Format(Total)}";

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
                result.Add(new DriveViewModel(d.RootDirectory.FullName, title, $"{d.DriveFormat} · {Kind(d.DriveType)}",
                    (ulong)d.TotalSize, (ulong)d.AvailableFreeSpace));
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
            result.Add(new DriveViewModel(home, "Your user folder", home, 0, 0));
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
