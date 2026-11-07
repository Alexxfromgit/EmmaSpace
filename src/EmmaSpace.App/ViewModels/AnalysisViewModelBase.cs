using CommunityToolkit.Mvvm.ComponentModel;
using EmmaSpace.Core.Model;

namespace EmmaSpace.App.ViewModels;

/// <summary>Shared plumbing for tabs that analyse the current tree in the background.</summary>
public abstract partial class AnalysisViewModelBase(INodeActions actions) : ObservableObject
{
    private CancellationTokenSource? _cts;

    protected INodeActions Actions { get; } = actions;
    protected FileTree? Tree { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; protected set; }

    public void Load(FileTree? tree)
    {
        Cancel();
        Tree = tree;
        OnTreeChanged();
    }

    public virtual void OnNodeRemoved() { }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        IsBusy = false;
    }

    protected abstract void OnTreeChanged();

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool while holding a lease on the tree, then
    /// applies the result on the UI thread unless the tree changed or the work was cancelled.
    /// </summary>
    protected async Task RunAsync<T>(Func<FileTree, CancellationToken, Task<T>> work, Action<T> apply)
    {
        var tree = Tree;
        if (tree is null) return;
        Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var result = await Task.Run(async () =>
            {
                using var lease = tree.Lease();
                return await work(tree, cts.Token);
            }, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(tree, Tree)) apply(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The tree was replaced while we were working.
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsBusy = false;
        }
    }

    protected Task RunAsync<T>(Func<FileTree, CancellationToken, T> work, Action<T> apply) =>
        RunAsync((t, ct) => Task.FromResult(work(t, ct)), apply);

    protected void RemoveDeleted<T>(IList<T> items) where T : NodeItemViewModel
    {
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i].IsDeleted) items.RemoveAt(i);
    }
}
