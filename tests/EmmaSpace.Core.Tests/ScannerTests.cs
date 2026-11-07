using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Scanning;

namespace EmmaSpace.Core.Tests;

public class ScannerTests
{
    private static TempTree CreateSample()
    {
        var temp = new TempTree();
        temp.File("top.bin", 5000);
        temp.File(Path.Join("docs", "a.txt"), 100);
        temp.File(Path.Join("docs", "deep", "b.txt"), 200);
        temp.File(Path.Join("docs", "deep", "deeper", "ünïcödé.dat"), 300);
        temp.Folder("empty");
        return temp;
    }

    private static void AssertSampleTree(FileTree tree)
    {
        ref readonly var root = ref tree.Nodes[FileTree.Root];
        Assert.Equal(5600UL, root.Size);
        Assert.Equal(4u, root.FileCount);
        Assert.Equal(4u, root.DirCount);
        Assert.Equal(0, tree.Info.Errors);
        var children = new List<string>();
        foreach (var c in tree.Children(FileTree.Root)) children.Add(tree.GetName(c));
        Assert.Equal(["top.bin", "docs", "empty"], children);
        Assert.NotEqual(FileTree.None, TreeFixtures.Find(tree, Path.Join("docs", "deep", "deeper", "ünïcödé.dat")));
    }

    [Fact]
    public async Task Managed_scanner_builds_the_tree()
    {
        using var temp = CreateSample();
        using var tree = await new ManagedScanner().ScanAsync(temp.Root, new ScanOptions(), null, TestContext.Current.CancellationToken);
        AssertSampleTree(tree);
        Assert.Equal(ScanSource.Managed, tree.Info.Source);
    }

    [Fact]
    public async Task Native_scanner_builds_the_tree()
    {
        Assert.SkipUnless(NativeEngine.IsAvailable, "Native engine not built for this platform.");
        using var temp = CreateSample();
        var reports = new List<ScanProgress>();
        using var tree = await new NativeScanner().ScanAsync(temp.Root, new ScanOptions(), new SyncProgress(reports), TestContext.Current.CancellationToken);
        AssertSampleTree(tree);
        Assert.Equal(ScanSource.Native, tree.Info.Source);
        Assert.Equal(ScanMode.Walker, tree.Info.Mode);
        Assert.Contains(reports, r => r.Phase == ScanPhase.Done);
    }

    [Fact]
    public async Task Native_and_managed_scanners_agree_on_a_real_folder()
    {
        Assert.SkipUnless(NativeEngine.IsAvailable, "Native engine not built for this platform.");
        // The SDK folder is large, stable and readable without elevation.
        var folder = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        using var native = await new NativeScanner().ScanAsync(folder, new ScanOptions(), null, TestContext.Current.CancellationToken);
        using var managed = await new ManagedScanner().ScanAsync(folder, new ScanOptions(), null, TestContext.Current.CancellationToken);
        Assert.Equal(managed.Nodes[0].FileCount, native.Nodes[0].FileCount);
        Assert.Equal(managed.Nodes[0].DirCount, native.Nodes[0].DirCount);
        Assert.Equal(managed.Nodes[0].Size, native.Nodes[0].Size);
    }

    [Fact]
    public async Task Missing_folders_raise_a_scan_exception()
    {
        var missing = Path.Combine(Path.GetTempPath(), "emmaspace-missing-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<ScanException>(() =>
            new DefaultScanner().ScanAsync(missing, new ScanOptions(), null, TestContext.Current.CancellationToken));
        Assert.Equal(EsStatus.NotFound, ex.Status);
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var folder = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DefaultScanner().ScanAsync(folder, new ScanOptions(), null, cts.Token));
    }

    private sealed class SyncProgress(List<ScanProgress> reports) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
