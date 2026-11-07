using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmmaSpace.App.Services;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Formatting;

namespace EmmaSpace.App.ViewModels;

/// <summary>Old files and empty folders: the usual candidates for a clean-up.</summary>
public sealed partial class CleanupViewModel(INodeActions actions, SettingsService settings) : AnalysisViewModelBase(actions)
{
    public static IReadOnlyList<string> AgeOptions { get; } = ["6 months", "1 year", "2 years", "5 years"];
    public static IReadOnlyList<string> BasisOptions { get; } = ["Not modified for", "Not opened for"];
    private static readonly int[] s_months = [6, 12, 24, 60];

    public ObservableCollection<NodeItemViewModel> OldFiles { get; } = [];
    public ObservableCollection<NodeItemViewModel> EmptyFolders { get; } = [];

    [ObservableProperty]
    public partial int AgeIndex { get; set; } = Math.Clamp(settings.Current.OldFilesAgeIndex, 0, 3);

    [ObservableProperty]
    public partial int BasisIndex { get; set; } = settings.Current.OldFilesByAccess ? 1 : 0;

    [ObservableProperty]
    public partial string OldSummary { get; private set; } = "";

    [ObservableProperty]
    public partial string EmptySummary { get; private set; } = "";

    protected override void OnTreeChanged()
    {
        OldFiles.Clear();
        EmptyFolders.Clear();
        OldSummary = EmptySummary = "";
        if (Tree is not null) _ = RefreshAsync();
    }

    public override void OnNodeRemoved()
    {
        RemoveDeleted(OldFiles);
        RemoveDeleted(EmptyFolders);
        UpdateSummaries();
    }

    partial void OnAgeIndexChanged(int value)
    {
        settings.Current.OldFilesAgeIndex = value;
        settings.Save();
        _ = RefreshAsync();
    }

    partial void OnBasisIndexChanged(int value)
    {
        settings.Current.OldFilesByAccess = value == 1;
        settings.Save();
        _ = RefreshAsync();
    }

    private Task RefreshAsync()
    {
        var cutoff = DateTimeOffset.Now.AddMonths(-s_months[Math.Clamp(AgeIndex, 0, 3)]);
        var basis = BasisIndex == 1 ? AgeBasis.LastAccessed : AgeBasis.LastModified;
        return RunAsync((tree, ct) => (
                Old: FileQueries.OldFiles(tree, cutoff, basis, 1000, ct: ct),
                Empty: FileQueries.EmptyFolders(tree, ct: ct)),
            result =>
            {
                OldFiles.Clear();
                foreach (var node in result.Old) OldFiles.Add(new NodeItemViewModel(Tree!, node, Actions));
                EmptyFolders.Clear();
                foreach (var node in result.Empty.Take(5000)) EmptyFolders.Add(new NodeItemViewModel(Tree!, node, Actions));
                UpdateSummaries();
            });
    }

    private void UpdateSummaries()
    {
        var oldBytes = OldFiles.Aggregate(0UL, (s, f) => s + f.Bytes);
        OldSummary = OldFiles.Count == 0 ? "No files match." : $"{OldFiles.Count:N0} largest matches · {ByteSize.Format(oldBytes)}";
        EmptySummary = EmptyFolders.Count == 0 ? "No empty folders." : $"{EmptyFolders.Count:N0} empty folders";
    }
}
