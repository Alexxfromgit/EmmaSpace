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
    /// applies the result on the UI thread unless the tree was replaced or the work was cancelled.
    /// A lease does not stop removals, so if the tree changed while the work ran, its result may
    /// describe the old state (or a mix of both): it is then run again, unless
    /// <paramref name="rerunOnChange"/> is false, in which case the caller's
    /// <see cref="OnNodeRemoved"/> is relied on to correct the applied result.
    /// </summary>
    protected async Task RunAsync<T>(Func<FileTree, CancellationToken, Task<T>> work, Action<T> apply, bool rerunOnChange = true)
    {
        var tree = Tree;
        if (tree is null) return;
        Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            while (true)
            {
                var revision = tree.Revision;
                var result = await Task.Run(async () =>
                {
                    using var lease = tree.Lease();
                    return await work(tree, cts.Token);
                }, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(tree, Tree)) return;
                if (tree.Revision != revision && rerunOnChange) continue;
                apply(result);
                if (tree.Revision != revision) OnNodeRemoved();
                return;
            }
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
