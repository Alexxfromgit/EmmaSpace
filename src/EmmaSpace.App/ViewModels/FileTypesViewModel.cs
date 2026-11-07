using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EmmaSpace.App.Controls;
using EmmaSpace.App.Services;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Model;

namespace EmmaSpace.App.ViewModels;

public sealed class CategoryRowViewModel(CategoryStats stats, ulong total)
{
    public string Name { get; } = stats.Category.ToString();
    public IBrush Brush { get; } = CategoryPalette.BrushOf(stats.Category);
    public string SizeText { get; } = ByteSize.Format(stats.Bytes);
    public string CountText { get; } = stats.Count.ToString("N0", CultureInfo.CurrentCulture) + " files";
    public double Share { get; } = total == 0 ? 0 : stats.Bytes * 100.0 / total;
    public string ShareText => Share.ToString("0.0", CultureInfo.CurrentCulture) + " %";
}

public sealed class ExtensionRowViewModel(ExtensionStats stats, ulong total)
{
    public ExtensionStats Stats { get; } = stats;
    public string Extension { get; } = stats.Extension == FileTypeBreakdown.NoExtension ? stats.Extension : "." + stats.Extension;
    public string Category { get; } = stats.Category.ToString();
    public IBrush Brush { get; } = CategoryPalette.BrushOf(stats.Category);
    public string SizeText { get; } = ByteSize.Format(stats.Bytes);
    public string CountText { get; } = stats.Count.ToString("N0", CultureInfo.CurrentCulture);
    public double Share { get; } = total == 0 ? 0 : stats.Bytes * 100.0 / total;
}

public sealed partial class FileTypesViewModel(INodeActions actions) : AnalysisViewModelBase(actions)
{
    public ObservableCollection<CategoryRowViewModel> Categories { get; } = [];
    public ObservableCollection<ExtensionRowViewModel> Extensions { get; } = [];
    public ObservableCollection<NodeItemViewModel> ExtensionFiles { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<DonutSlice> Slices { get; private set; } = [];

    [ObservableProperty]
    public partial ExtensionRowViewModel? SelectedExtension { get; set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    protected override void OnTreeChanged()
    {
        Categories.Clear();
        Extensions.Clear();
        ExtensionFiles.Clear();
        Slices = [];
        Summary = "";
        if (Tree is not null) _ = RefreshAsync();
    }

    public override void OnNodeRemoved()
    {
        if (Tree is not null) _ = RefreshAsync();
    }

    private Task RefreshAsync() => RunAsync((tree, ct) => FileTypeBreakdown.Compute(tree, ct: ct), report =>
    {
        var total = Tree!.Nodes[FileTree.Root].Size;
        Categories.Clear();
        foreach (var c in report.Categories) Categories.Add(new CategoryRowViewModel(c, total));
        Extensions.Clear();
        foreach (var e in report.Extensions.Take(500)) Extensions.Add(new ExtensionRowViewModel(e, total));
        Slices = report.Categories.Select(c => new DonutSlice(c.Bytes, CategoryPalette.ColorOf(c.Category), c.Category.ToString())).ToList();
        Summary = $"{report.Extensions.Count:N0} file types";
        SelectedExtension = Extensions.FirstOrDefault();
    });

    partial void OnSelectedExtensionChanged(ExtensionRowViewModel? value)
    {
        ExtensionFiles.Clear();
        var tree = Tree;
        if (value is null || tree is null) return;
        var ext = value.Stats.Extension;
        var revision = tree.Revision;
        _ = Task.Run(() =>
        {
            using var lease = tree.Lease();
            var heap = new PriorityQueue<int, ulong>();
            for (var i = 0; i < tree.Count; i++)
            {
                ref readonly var n = ref tree.Nodes[i];
                if (n.IsDirectory || n.IsDeleted) continue;
                var e = tree.GetExtension(i);
                var match = ext == FileTypeBreakdown.NoExtension ? e.IsEmpty : e.Equals(ext, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                if (heap.Count < 200) heap.Enqueue(i, n.Size);
                else if (heap.TryPeek(out _, out var min) && n.Size > min) heap.EnqueueDequeue(i, n.Size);
            }
            var list = new List<int>();
            while (heap.TryDequeue(out var node, out _)) list.Add(node);
            list.Reverse();
            return list;
        }).ContinueWith(t =>
        {
            // After a removal the whole breakdown is refreshed, which selects the extension anew.
            if (t.IsCompletedSuccessfully && ReferenceEquals(SelectedExtension, value) && ReferenceEquals(tree, Tree) &&
                tree.Revision == revision)
                foreach (var node in t.Result) ExtensionFiles.Add(new NodeItemViewModel(tree, node, Actions));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
