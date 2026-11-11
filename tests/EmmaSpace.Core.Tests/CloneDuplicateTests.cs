using System.Diagnostics;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Scanning;

namespace EmmaSpace.Core.Tests;

/// <summary>APFS clones found by the native engine, as the duplicate finder sees them (macOS).</summary>
public sealed class CloneDuplicateTests
{
    [Fact]
    public async Task Duplicates_count_each_clone_family_once()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && NativeEngine.IsAvailable, "APFS clones need macOS and the native engine.");
        using var temp = new TempTree();
        var content = new byte[300_000];
        new Random(5).NextBytes(content); // not compressible, so each real copy takes its full size
        var a1 = temp.File("a1.bin", content);
        var b1 = temp.File("b/b1.bin", content); // written again: its own blocks
        temp.File("plain.bin", content);
        if (!Clone(a1, Path.Combine(temp.Root, "a2.bin"))) Assert.Skip("cp -c could not clone here (not APFS?)");
        Assert.True(Clone(b1, Path.Combine(temp.Root, "b", "b2.bin")));

        var ct = TestContext.Current.CancellationToken;
        using var tree = await new NativeScanner().ScanAsync(temp.Root, new ScanOptions(), null, ct);
        var groups = await DuplicateFinder.FindAsync(tree, new DuplicateOptions { MinSize = 1 }, null, ct);

        var group = Assert.Single(groups);
        Assert.Equal(5, group.Nodes.Count);
        Assert.Equal(4, group.Nodes.Count(n => (tree.Nodes[n].Flags & NodeFlags.Clone) != 0));
        // On disk: family a once, family b once and the plain copy. Keeping one, two copies' worth can go.
        Assert.Equal(3, group.HolderCount);
        Assert.Equal(2UL * 300_000, group.WastedBytes);
    }

    private static bool Clone(string from, string to)
    {
        using var cp = Process.Start(new ProcessStartInfo("/bin/cp", ["-c", from, to]) { RedirectStandardError = true })!;
        cp.WaitForExit();
        return cp.ExitCode == 0;
    }
}
