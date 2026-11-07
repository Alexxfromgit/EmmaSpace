using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Model;

namespace EmmaSpace.App.ViewModels;

public sealed partial class LargestFilesViewModel(INodeActions actions) : AnalysisViewModelBase(actions)
{
    private const int Count = 1000;

    public ObservableCollection<NodeItemViewModel> Files { get; } = [];

    [ObservableProperty]
    public partial NodeItemViewModel? SelectedFile { get; set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    protected override void OnTreeChanged()
    {
        Files.Clear();
        Summary = "";
        if (Tree is not null) _ = RefreshAsync();
    }

    public override void OnNodeRemoved()
    {
        RemoveDeleted(Files);
        UpdateSummary();
    }

    private Task RefreshAsync() => RunAsync((tree, ct) => FileQueries.LargestFiles(tree, Count, ct: ct), nodes =>
    {
        Files.Clear();
        foreach (var node in nodes) Files.Add(new NodeItemViewModel(Tree!, node, Actions));
        UpdateSummary();
    });

    private void UpdateSummary()
    {
        var total = Files.Aggregate(0UL, (sum, f) => sum + f.Bytes);
        Summary = Files.Count == 0 ? "" : $"Top {Files.Count:N0} files · {ByteSize.Format(total)}";
    }
}
