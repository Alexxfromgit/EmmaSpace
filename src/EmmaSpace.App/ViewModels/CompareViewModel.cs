using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmaSpace.App.Services;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Snapshots;

namespace EmmaSpace.App.ViewModels;

public sealed class DiffRowViewModel(DiffEntry entry, IBrush grownBrush, IBrush shrunkBrush)
{
    public string Path { get; } = entry.Path;
    public string Kind { get; } = entry.Kind switch
    {
        ChangeKind.Added => "New",
        ChangeKind.Removed => "Gone",
        ChangeKind.Grown => "Grew",
        _ => "Shrank",
    };
    public string OldText { get; } = entry.Kind == ChangeKind.Added ? "—" : ByteSize.Format(entry.OldSize);
    public string NewText { get; } = entry.Kind == ChangeKind.Removed ? "—" : ByteSize.Format(entry.NewSize);
    public string DeltaText { get; } = ByteSize.FormatDelta(entry.Delta);
    public IBrush DeltaBrush { get; } = entry.Delta > 0 ? grownBrush : shrunkBrush;
    public string Glyph { get; } = entry.IsDirectory ? "▰" : "▪";
}

public sealed partial class CompareViewModel(INodeActions actions, IDialogService dialogs) : AnalysisViewModelBase(actions)
{
    private static readonly IBrush s_grown = new ImmutableSolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush s_shrunk = new ImmutableSolidColorBrush(Color.Parse("#30A46C"));

    public ObservableCollection<DiffRowViewModel> Changes { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = "Compare this scan with a saved snapshot to see what grew, shrank, appeared or disappeared.";

    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    protected override void OnTreeChanged()
    {
        Changes.Clear();
        HasResult = false;
        Summary = "Compare this scan with a saved snapshot to see what grew, shrank, appeared or disappeared.";
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (Tree is null) return;
        var path = await dialogs.OpenFileAsync("Compare with snapshot", "Emma Space snapshot", SnapshotFile.Extension);
        if (path is null) return;

        FileTree older;
        try
        {
            older = await SnapshotFile.LoadAsync(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await dialogs.ShowErrorAsync("Could not open snapshot", ex.Message);
            return;
        }

        using (older)
        {
            if (Tree is not { } current) return; // the scan was closed while the file was loading
            if (!SnapshotDiff.SameLocation(older, current))
            {
                await dialogs.ShowErrorAsync("Different location",
                    $"This snapshot is of \"{older.RootPath}\", but the current scan is of \"{current.RootPath}\". " +
                    "Open a snapshot of the same folder or drive to compare.");
                return;
            }
            await RunAsync((tree, ct) => SnapshotDiff.Compare(older, tree, ct: ct), report =>
            {
                Changes.Clear();
                foreach (var c in report.Changes) Changes.Add(new DiffRowViewModel(c, s_grown, s_shrunk));
                var when = older.Info.ScannedAt.ToString("g", CultureInfo.CurrentCulture);
                Summary = $"Since {when}: {ByteSize.Format(report.OldTotal)} → {ByteSize.Format(report.NewTotal)} " +
                          $"({ByteSize.FormatDelta(report.Delta)}) · {report.Changes.Count:N0} changes shown";
                HasResult = true;
            });
        }
    }
}
