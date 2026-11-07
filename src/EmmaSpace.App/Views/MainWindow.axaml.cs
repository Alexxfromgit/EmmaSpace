using Avalonia.Controls;
using Avalonia.Input;
using EmmaSpace.App.ViewModels;

namespace EmmaSpace.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Treemap.NodeActivated += (_, node) => ViewModel?.ZoomTowards(node);
        Treemap.NodeContextRequested += OnTreemapContext;
        Treemap.ContextRequested += (_, e) =>
        {
            // No node under the pointer: don't show a menu bound to the window's view model.
            if (Treemap.ContextMenu?.DataContext is not NodeItemViewModel) e.Handled = true;
        };
        FolderList.DoubleTapped += (_, _) => ToggleSelectedRow(null);
        FolderList.KeyDown += OnFolderListKeyDown;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    // Runs on right-button press, before the styled context menu opens on release.
    private void OnTreemapContext(object? sender, int node)
    {
        if (Treemap.ContextMenu is not { } menu) return;
        menu.DataContext = ViewModel?.Tree is { } tree && node >= 0 ? new NodeItemViewModel(tree, node, ViewModel) : null;
    }

    private void OnFolderListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right:
                ToggleSelectedRow(expand: true);
                e.Handled = true;
                break;
            case Key.Left:
                ToggleSelectedRow(expand: false);
                e.Handled = true;
                break;
            case Key.Enter:
                ToggleSelectedRow(null);
                e.Handled = true;
                break;
            case Key.Delete when ViewModel?.FolderTree.SelectedRow is { } row and not MoreRowViewModel:
                _ = ViewModel.MoveToTrashAsync(row.Node);
                e.Handled = true;
                break;
        }
    }

    private void ToggleSelectedRow(bool? expand)
    {
        var tree = ViewModel?.FolderTree;
        if (tree?.SelectedRow is not { } row) return;
        if (row is MoreRowViewModel || expand is null)
            tree.Toggle(row);
        else if (expand.Value)
            tree.Expand(row);
        else
            tree.Collapse(row);
    }
}
