using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Model;

namespace EmmaSpace.App.ViewModels;

public partial class TreeRowViewModel : NodeItemViewModel
{
    public TreeRowViewModel(FileTree tree, int node, int depth, INodeActions actions) : base(tree, node, actions)
    {
        Depth = depth;
        HasChildren = IsDirectory && tree.HasChildren(node);
    }

    public int Depth { get; }
    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public bool HasChildren { get; private set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>How many children are currently listed below this row (paged for huge folders).</summary>
    public int ShownChildren { get; set; }

    public string Expander => !HasChildren ? "" : IsExpanded ? "▾" : "▸";

    /// <summary>True for the "… N more items" placeholder; the table shows only its label.</summary>
    public virtual bool IsMore => false;
    public virtual string MoreLabel => "";

    public double Share
    {
        get
        {
            var parent = Tree.GetParent(Node);
            var parentSize = parent == FileTree.None ? Tree.Nodes[Node].Size : Tree.Nodes[parent].Size;
            return parentSize == 0 ? 0 : Tree.Nodes[Node].Size * 100.0 / parentSize;
        }
    }

    public string ShareText => Share.ToString("0.0", CultureInfo.CurrentCulture) + " %";

    public string FilesText => IsDirectory ? Tree.Nodes[Node].FileCount.ToString("N0", CultureInfo.CurrentCulture) : "";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Expander));

    public override void Refresh()
    {
        base.Refresh();
        HasChildren = IsDirectory && Tree.HasChildren(Node);
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(Expander));
        OnPropertyChanged(nameof(Share));
        OnPropertyChanged(nameof(ShareText));
        OnPropertyChanged(nameof(FilesText));
    }
}

/// <summary>Placeholder row that loads the next page of a very large folder.</summary>
public sealed class MoreRowViewModel(FileTree tree, TreeRowViewModel owner, int remaining, ulong remainingBytes, INodeActions actions)
    : TreeRowViewModel(tree, owner.Node, owner.Depth + 1, actions)
{
    public TreeRowViewModel Owner { get; } = owner;
    public override bool IsMore => true;
    public override string MoreLabel { get; } = $"… {remaining:N0} more items ({ByteSize.Format(remainingBytes)})";
}

/// <summary>
/// The folder tree as a flat list of visible rows: expanding inserts the children right after
/// a row, collapsing removes its descendants. This keeps the list virtualized and fast.
/// </summary>
public sealed partial class FolderTreeViewModel(INodeActions actions) : ObservableObject
{
    private const int PageSize = 500;
    private FileTree? _tree;

    public ObservableCollection<TreeRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial TreeRowViewModel? SelectedRow { get; set; }

    public void Load(FileTree? tree)
    {
        _tree = tree;
        Rows.Clear();
        SelectedRow = null;
        if (tree is null) return;
        var root = new TreeRowViewModel(tree, FileTree.Root, 0, actions);
        Rows.Add(root);
        Expand(root);
        SelectedRow = root;
    }

    public void Toggle(TreeRowViewModel row)
    {
        if (row is MoreRowViewModel more)
        {
            LoadMore(more);
            return;
        }
        if (row.IsExpanded) Collapse(row);
        else Expand(row);
    }

    public void Expand(TreeRowViewModel row, int minimumShown = 0)
    {
        if (_tree is null || !row.HasChildren || row.IsExpanded) return;
        row.IsExpanded = true;
        row.ShownChildren = 0;
        InsertChildren(row, Rows.IndexOf(row) + 1, Math.Max(PageSize, minimumShown));
    }

    public void Collapse(TreeRowViewModel row)
    {
        if (!row.IsExpanded) return;
        row.IsExpanded = false;
        var index = Rows.IndexOf(row);
        while (index + 1 < Rows.Count && Rows[index + 1].Depth > row.Depth) Rows.RemoveAt(index + 1);
    }

    /// <summary>Expands every ancestor of <paramref name="node"/> and selects its row.</summary>
    public TreeRowViewModel? Reveal(int node)
    {
        if (_tree is null || node < 0 || _tree.Nodes[node].IsDeleted) return null;
        var chain = new List<int>();
        for (var n = node; n != FileTree.None; n = _tree.GetParent(n)) chain.Add(n);
        chain.Reverse();

        var row = Rows.FirstOrDefault(r => r.Node == FileTree.Root && r is not MoreRowViewModel);
        for (var i = 1; row is not null && i < chain.Count; i++)
        {
            var target = chain[i];
            if (!row.IsExpanded) Expand(row, minimumShown: PositionOf(row.Node, target) + 1);
            var found = FindChildRow(row, target);
            while (found is null && Rows.ElementAtOrDefault(LastDescendantIndex(row)) is MoreRowViewModel more)
            {
                LoadMore(more);
                found = FindChildRow(row, target);
            }
            row = found;
        }
        if (row is not null) SelectedRow = row;
        return row;
    }

    /// <summary>Drops rows of removed nodes and refreshes sizes of the rest.</summary>
    public void OnNodeRemoved()
    {
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i] is not MoreRowViewModel && Rows[i].IsDeleted) Rows.RemoveAt(i);
        }
        foreach (var row in Rows) row.Refresh();
    }

    private void InsertChildren(TreeRowViewModel owner, int index, int count)
    {
        var tree = _tree!;
        var skipped = 0;
        var added = 0;
        var remaining = 0;
        ulong remainingBytes = 0;
        foreach (var child in tree.Children(owner.Node))
        {
            if (skipped < owner.ShownChildren)
            {
                skipped++;
                continue;
            }
            if (added < count)
            {
                Rows.Insert(index + added, new TreeRowViewModel(tree, child, owner.Depth + 1, actions));
                added++;
            }
            else
            {
                remaining++;
                remainingBytes += tree.Nodes[child].Size;
            }
        }
        owner.ShownChildren += added;
        if (remaining > 0) Rows.Insert(index + added, new MoreRowViewModel(tree, owner, remaining, remainingBytes, actions));
    }

    private void LoadMore(MoreRowViewModel more)
    {
        var index = Rows.IndexOf(more);
        Rows.RemoveAt(index);
        InsertChildren(more.Owner, index, PageSize);
    }

    private TreeRowViewModel? FindChildRow(TreeRowViewModel parent, int node)
    {
        var start = Rows.IndexOf(parent);
        for (var i = start + 1; i < Rows.Count && Rows[i].Depth > parent.Depth; i++)
            if (Rows[i].Depth == parent.Depth + 1 && Rows[i].Node == node && Rows[i] is not MoreRowViewModel) return Rows[i];
        return null;
    }

    private int LastDescendantIndex(TreeRowViewModel row)
    {
        var i = Rows.IndexOf(row);
        while (i + 1 < Rows.Count && Rows[i + 1].Depth > row.Depth) i++;
        return i;
    }

    private int PositionOf(int parent, int child)
    {
        var position = 0;
        foreach (var c in _tree!.Children(parent))
        {
            if (c == child) return position;
            position++;
        }
        return 0;
    }
}
