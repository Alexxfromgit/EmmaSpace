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
        var copyActions = new CopyActions(actions, this);
        foreach (var node in group.Nodes.OrderBy(tree.GetPath, StringComparer.CurrentCultureIgnoreCase))
            Files.Add(new NodeItemViewModel(tree, node, copyActions));
        Name = Files[0].Name;
    }

    /// <summary>
    /// Actions on the copies in this group: moving one to the Trash first checks that it is still identical
    /// to another copy. The search may be old (or come from a snapshot), and the files may have changed since.
    /// </summary>
    private sealed class CopyActions(INodeActions inner, DuplicateGroupViewModel group) : INodeActions
    {
        public bool CanShowProperties => inner.CanShowProperties;
        public void Open(int node) => inner.Open(node);
        public Task RevealAsync(int node) => inner.RevealAsync(node);
        public Task CopyPathAsync(int node) => inner.CopyPathAsync(node);
        public void ShowInTree(int node) => inner.ShowInTree(node);
        public void ShowInTreemap(int node) => inner.ShowInTreemap(node);
        public Task MoveToTrashAsync(FileTree tree, int node) =>
            inner.MoveDuplicateToTrashAsync(tree, node, group.Files.Select(f => f.Node).ToList());
        public Task MoveDuplicateToTrashAsync(FileTree tree, int node, IReadOnlyList<int> copies) =>
            inner.MoveDuplicateToTrashAsync(tree, node, copies);
        public void ShowProperties(int node) => inner.ShowProperties(node);
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

public sealed partial class DuplicatesViewModel(INodeActions actions, SettingsService settings, HashCacheService hashCache)
    : AnalysisViewModelBase(actions)
{
    public static IReadOnlyList<string> MinSizeOptions { get; } = ["Any size", "1 MB", "10 MB", "100 MB"];
    private static readonly ulong[] s_minSizes = [1, 1UL << 20, 10UL << 20, 100UL << 20];

    private ulong _resultMinSize; // the smallest file size the shown results looked at
    private bool _resultFromSnapshot; // the shown results were saved with the snapshot on screen

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    /// <summary>
    /// The results on screen, to save with a snapshot: null unless a search has completed (a running or
    /// stopped one would be partial). Copies removed since then are already gone from <see cref="Groups"/>.
    /// </summary>
    public DuplicateResult? CurrentResult => HasRun && !IsBusy
        ? new DuplicateResult(_resultMinSize,
            Groups.Select(g => new DuplicateGroup(g.FileSize, g.Files.Select(f => f.Node).ToList())).ToList())
        : null;

    /// <summary>Shows results that were saved with the snapshot just opened, instead of searching again.</summary>
    public void ShowSaved(DuplicateResult result)
    {
        if (Tree is null) return;
        Cancel();
        Groups.Clear();
        foreach (var g in result.Groups) Groups.Add(new DuplicateGroupViewModel(Tree, g, Actions));
        SelectedGroup = Groups.FirstOrDefault();
        _resultMinSize = result.MinSize;
        _resultFromSnapshot = true;
        ProgressValue = 0;
        HasRun = true;
        UpdateStatus();
    }

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
        _resultFromSnapshot = false;
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
        _resultFromSnapshot = false;
        var options = new DuplicateOptions { MinSize = s_minSizes[Math.Clamp(MinSizeIndex, 0, 3)] };
        var progress = new Progress<DuplicateProgress>(p =>
        {
            // Bytes, so a stage spent on a few large files doesn't look stuck.
            Status = p.BytesTotal > 0
                ? $"{p.Stage}… {ByteSize.Format(p.BytesDone)} of {ByteSize.Format(p.BytesTotal)} · {p.Done:N0} of {p.Total:N0} files"
                : $"{p.Stage}… {p.Done:N0} / {p.Total:N0}";
            ProgressValue = p.BytesTotal > 0 ? p.BytesDone * 100.0 / p.BytesTotal
                : p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
        });
        // Hashing again after a removal would be slow, and a removal cannot create new duplicates:
        // the result is applied as is and OnNodeRemoved drops the removed copies from it.
        await RunAsync(async (tree, ct) =>
        {
            var cache = await hashCache.GetAsync();
            try
            {
                return await DuplicateFinder.FindAsync(tree, options with { Cache = cache }, progress, ct);
            }
            finally
            {
                // Also after Stop: what was hashed so far is just as valid. Saving doesn't hold up the results.
                if (cache is not null) _ = Task.Run(cache.Save, CancellationToken.None);
            }
        }, groups =>
        {
            foreach (var g in groups) Groups.Add(new DuplicateGroupViewModel(Tree!, g, Actions));
            SelectedGroup = Groups.FirstOrDefault();
            _resultMinSize = options.MinSize;
            HasRun = true;
            UpdateStatus();
        }, rerunOnChange: false);
    }

    private CancellationTokenSource? _checkCts;
    private string _checkText = "";

    /// <summary>A copy is being compared with the others before it is moved to the Trash.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    public partial bool IsChecking { get; private set; }

    /// <summary>A search or a check is running: Stop and the progress bar show, "Find duplicates" doesn't.</summary>
    public bool IsWorking => IsBusy || IsChecking;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy)) OnPropertyChanged(nameof(IsWorking));
    }

    /// <summary>Shows a check before moving a copy to the Trash; Stop cancels it through the returned token.</summary>
    public CancellationToken BeginCheck(string text)
    {
        _checkCts?.Cancel();
        _checkCts?.Dispose();
        _checkCts = new CancellationTokenSource();
        _checkText = text;
        Status = text;
        ProgressValue = 0;
        IsChecking = true;
        return _checkCts.Token;
    }

    /// <summary>How far the check is, from 0 to 1.</summary>
    public void ReportCheck(double done)
    {
        if (!IsChecking) return;
        ProgressValue = done * 100;
        Status = $"{_checkText} {done.ToString("P0", CultureInfo.CurrentCulture)}";
    }

    /// <summary>Ends the check and puts the results summary back (or "Stopped." when it was cancelled).</summary>
    public void EndCheck(bool cancelled)
    {
        IsChecking = false;
        ProgressValue = 0;
        _checkCts?.Dispose();
        _checkCts = null;
        if (IsBusy) return;
        if (cancelled) Status = "Stopped.";
        else UpdateStatus();
    }

    [RelayCommand]
    private void Stop()
    {
        if (IsChecking)
        {
            _checkCts?.Cancel();
            return;
        }
        Cancel();
        Status = "Stopped.";
    }

    private void UpdateStatus()
    {
        if (!HasRun) return;
        var wasted = Groups.Aggregate(0UL, (sum, g) => sum + g.Wasted);
        var found = Groups.Count == 0
            ? "No duplicates found"
            : $"{Groups.Count:N0} groups of duplicates · {ByteSize.Format(wasted)} could be freed";
        if (!_resultFromSnapshot)
        {
            Status = Groups.Count == 0 ? found + "." : found;
            return;
        }
        var sizes = _resultMinSize > 1 ? $", files of {ByteSize.Format(_resultMinSize)} and up" : "";
        Status = $"Saved with this snapshot{sizes}: {found}. Search again to check the files as they are now.";
    }
}
