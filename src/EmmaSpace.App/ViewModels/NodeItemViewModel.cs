using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmaSpace.App.Services;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.App.ViewModels;

/// <summary>Operations any list item can trigger on its node; implemented by the main view model.</summary>
public interface INodeActions
{
    bool CanShowProperties { get; }
    void Open(int node);
    void Reveal(int node);
    Task CopyPathAsync(int node);
    void ShowInTree(int node);
    void ShowInTreemap(int node);
    Task MoveToTrashAsync(int node);
    void ShowProperties(int node);
}

/// <summary>A file or folder shown in any list. Values are read from the tree on demand.</summary>
public partial class NodeItemViewModel : ObservableObject
{
    private string? _path;

    public NodeItemViewModel(FileTree tree, int node, INodeActions actions)
    {
        Tree = tree;
        Node = node;
        Actions = actions;
        Name = node == FileTree.Root ? tree.RootPath : tree.GetName(node);
        IsDirectory = tree.IsDirectory(node);
        Category = IsDirectory ? FileCategory.Other : FileCategories.Categorize(tree.GetExtension(node));
    }

    public FileTree Tree { get; }
    public int Node { get; }
    public INodeActions Actions { get; }

    public string Name { get; }
    public bool IsDirectory { get; }
    public FileCategory Category { get; }
    public string Path => _path ??= Tree.GetPath(Node);
    public string ParentPath => System.IO.Path.GetDirectoryName(Path) ?? "";

    public ulong Bytes => Tree.Nodes[Node].Size;
    public string SizeText => ByteSize.Format(Tree.Nodes[Node].Size);
    public string AllocatedText => ByteSize.Format(Tree.Nodes[Node].Allocated);

    public string ModifiedText
    {
        get
        {
            var ms = Tree.Nodes[Node].MTime;
            return ms == 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
        }
    }

    public string AccessedText
    {
        get
        {
            var ms = Tree.Nodes[Node].ATime;
            return ms == 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
        }
    }

    public bool HasError => (Tree.Nodes[Node].Flags & NodeFlags.Error) != 0;
    public bool IsReparse => (Tree.Nodes[Node].Flags & NodeFlags.Reparse) != 0;
    public bool IsCloud => (Tree.Nodes[Node].Flags & NodeFlags.Cloud) != 0;
    public bool IsDeleted => Tree.Nodes[Node].IsDeleted;

    public IBrush CategoryBrush => IsDirectory ? CategoryPalette.FolderBrush : CategoryPalette.BrushOf(Category);

    /// <summary>A plain text glyph, so the app needs no icon font.</summary>
    public string Glyph => IsDirectory ? (IsReparse ? "↪" : "▰") : "▪";

    public bool CanShowProperties => Actions.CanShowProperties;

    /// <summary>Re-reads values from the tree, e.g. after something below was deleted.</summary>
    public virtual void Refresh()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(AllocatedText));
        OnPropertyChanged(nameof(Bytes));
    }

    [RelayCommand]
    private void Open() => Actions.Open(Node);

    [RelayCommand]
    private void Reveal() => Actions.Reveal(Node);

    [RelayCommand]
    private Task CopyPath() => Actions.CopyPathAsync(Node);

    [RelayCommand]
    private void ShowInTree() => Actions.ShowInTree(Node);

    [RelayCommand]
    private void ShowInTreemap() => Actions.ShowInTreemap(Node);

    [RelayCommand]
    private Task MoveToTrash() => Actions.MoveToTrashAsync(Node);

    [RelayCommand]
    private void Properties() => Actions.ShowProperties(Node);
}
