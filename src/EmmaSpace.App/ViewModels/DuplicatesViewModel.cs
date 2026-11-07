using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmaSpace.App.Services;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Model;

namespace EmmaSpace.App.ViewModels;

public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(FileTree tree, DuplicateGroup group, INodeActions actions)
    {
        FileSize = group.FileSize;
        foreach (var node in group.Nodes.OrderBy(tree.GetPath, StringComparer.CurrentCultureIgnoreCase))
            Files.Add(new NodeItemViewModel(tree, node, actions));
        Name = Files[0].Name;
    }

    public ulong FileSize { get; }
    public string Name { get; }
    public ObservableCollection<NodeItemViewModel> Files { get; } = [];
    public ulong Wasted => Files.Count > 1 ? FileSize * (ulong)(Files.Count - 1) : 0;
    public string SizeText => ByteSize.Format(FileSize);
    public string CountText => Files.Count.ToString(CultureInfo.CurrentCulture) + " copies";
    public string WastedText => ByteSize.Format(Wasted);

    public void RemoveDeleted()
    {
        for (var i = Files.Count - 1; i >= 0; i--)
            if (Files[i].IsDeleted) Files.RemoveAt(i);
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(WastedText));
        OnPropertyChanged(nameof(Wasted));
    }
}

public sealed partial class DuplicatesViewModel(INodeActions actions, SettingsService settings) : AnalysisViewModelBase(actions)
{
    public static IReadOnlyList<string> MinSizeOptions { get; } = ["Any size", "1 MB", "10 MB", "100 MB"];
    private static readonly ulong[] s_minSizes = [1, 1UL << 20, 10UL << 20, 100UL << 20];

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial DuplicateGroupViewModel? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial int MinSizeIndex { get; set; } = Math.Clamp(settings.Current.DuplicateMinSizeIndex, 0, 3);

    [ObservableProperty]
    public partial string Status { get; private set; } = "Find files with identical content. Only files that share a size are read.";

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial bool HasRun { get; private set; }

    protected override void OnTreeChanged()
    {
        Groups.Clear();
        SelectedGroup = null;
        HasRun = false;
        ProgressValue = 0;
        Status = "Find files with identical content. Only files that share a size are read.";
    }

    partial void OnMinSizeIndexChanged(int value)
    {
        settings.Current.DuplicateMinSizeIndex = value;
        settings.Save();
    }

    public override void OnNodeRemoved()
    {
        foreach (var g in Groups) g.RemoveDeleted();
        for (var i = Groups.Count - 1; i >= 0; i--)
            if (Groups[i].Files.Count < 2) Groups.RemoveAt(i);
        UpdateStatus();
    }

    [RelayCommand]
    private async Task FindAsync()
    {
        if (Tree is null) return;
        Groups.Clear();
        HasRun = false;
        var options = new DuplicateOptions { MinSize = s_minSizes[Math.Clamp(MinSizeIndex, 0, 3)] };
        var progress = new Progress<DuplicateProgress>(p =>
        {
            Status = $"{p.Stage}… {p.Done:N0} / {p.Total:N0}";
            ProgressValue = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
        });
        await RunAsync((tree, ct) => DuplicateFinder.FindAsync(tree, options, progress, ct), groups =>
        {
            foreach (var g in groups) Groups.Add(new DuplicateGroupViewModel(Tree!, g, Actions));
            SelectedGroup = Groups.FirstOrDefault();
            HasRun = true;
            UpdateStatus();
        });
    }

    [RelayCommand]
    private void Stop()
    {
        Cancel();
        Status = "Stopped.";
    }

    private void UpdateStatus()
    {
        if (!HasRun) return;
        var wasted = Groups.Aggregate(0UL, (sum, g) => sum + g.Wasted);
        Status = Groups.Count == 0
            ? "No duplicates found."
            : $"{Groups.Count:N0} groups of duplicates · {ByteSize.Format(wasted)} could be freed";
    }
}
