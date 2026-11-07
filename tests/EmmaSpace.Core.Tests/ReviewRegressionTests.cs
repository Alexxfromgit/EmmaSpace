using System.Runtime.InteropServices;
using System.Text;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Snapshots;

namespace EmmaSpace.Core.Tests;

/// <summary>Regression tests for issues found in review of the first version.</summary>
public class ReviewRegressionTests
{
    private static ScanInfo Info(string root = @"C:\Root") =>
        new(root, DateTimeOffset.Now, TimeSpan.Zero, ScanMode.Walker, ScanSource.Managed, 0);

    private static List<string> ChildNames(FileTree tree, int node)
    {
        var names = new List<string>();
        foreach (var c in tree.Children(node)) names.Add(tree.GetName(c));
        return names;
    }

    [Fact]
    public void Remove_keeps_siblings_largest_first()
    {
        var b = new TreeBuilder();
        b.AddRoot(@"C:\Root");
        var big = b.AddChild(0, "big", NodeFlags.Directory);
        b.AddChild(big, "only.bin", NodeFlags.None, 100);
        var small = b.AddChild(0, "small", NodeFlags.Directory);
        b.AddChild(small, "s.bin", NodeFlags.None, 50);
        using var tree = b.Build(Info());
        Assert.Equal(["big", "small"], ChildNames(tree, 0));

        tree.Remove(TreeFixtures.Find(tree, Path.Join("big", "only.bin")));

        // "big" is now empty and must not stay in front of the non-empty sibling.
        Assert.Equal(["small", "big"], ChildNames(tree, 0));
        Assert.False(tree.SizesApproximate);
    }

    [Fact]
    public void Removing_a_hard_linked_file_marks_sizes_approximate()
    {
        var b = new TreeBuilder();
        b.AddRoot(@"C:\Root");
        b.AddChild(0, "counted.bin", NodeFlags.HasLinks, 5000);
        b.AddChild(0, "other-name.bin", NodeFlags.HardLink, 0);
        using var tree = b.Build(Info());
        tree.Remove(TreeFixtures.Find(tree, "counted.bin"));
        Assert.True(tree.SizesApproximate);
    }

    [Fact]
    public void FindByPath_requires_a_folder_boundary_after_the_root()
    {
        var b = new TreeBuilder();
        b.AddRoot(@"C:\Root");
        b.AddChild(0, "backup", NodeFlags.Directory);
        using var tree = b.Build(Info());
        Assert.Equal(FileTree.None, tree.FindByPath(@"C:\Rootbackup"));
        Assert.NotEqual(FileTree.None, tree.FindByPath(Path.Join(@"C:\Root", "backup")));
        Assert.Equal(FileTree.Root, tree.FindByPath(@"C:\Root"));
    }

    [Fact]
    public void Empty_folders_exclude_anything_that_was_not_fully_read()
    {
        var b = new TreeBuilder();
        b.AddRoot(@"C:\Root");
        b.AddChild(0, "cloud", NodeFlags.Directory | NodeFlags.Cloud);       // not listed
        var parent = b.AddChild(0, "parent", NodeFlags.Directory);
        var locked = b.AddChild(parent, "locked", NodeFlags.Directory);
        b.MarkError(locked);                                                   // unreadable
        var withLink = b.AddChild(0, "with-link", NodeFlags.Directory);
        b.AddChild(withLink, "junction", NodeFlags.Directory | NodeFlags.Reparse);
        b.AddChild(0, "really-empty", NodeFlags.Directory);
        using var tree = b.Build(Info());

        var empty = FileQueries.EmptyFolders(tree, ct: TestContext.Current.CancellationToken).Select(tree.GetName).ToList();
        Assert.Equal(["really-empty"], empty);
    }

    [Fact]
    public async Task Duplicates_never_open_cloud_placeholders()
    {
        using var temp = new TempTree();
        temp.File("a.bin", 4096); // identical content: without the Cloud rule they would match
        temp.File("c.bin", 4096);
        var b = new TreeBuilder();
        b.AddRoot(temp.Root);
        b.AddChild(0, "a.bin", NodeFlags.None, 4096);
        b.AddChild(0, "c.bin", NodeFlags.Cloud, 4096);
        using var tree = b.Build(Info(temp.Root));
        var groups = await DuplicateFinder.FindAsync(tree, new DuplicateOptions { MinSize = 1 }, null, TestContext.Current.CancellationToken);
        Assert.Empty(groups);
    }

    [Fact]
    public void Snapshots_with_cyclic_sibling_chains_are_rejected()
    {
        var nodes = new EsNode[2];
        nodes[0] = new EsNode { Parent = EsNode.None, FirstChild = 1, NextSibling = EsNode.None, Flags = NodeFlags.Directory };
        nodes[1] = new EsNode { Parent = 0, FirstChild = EsNode.None, NextSibling = 1 }; // points at itself
        using var stream = WriteRawSnapshot(nodes, [], nodes.Length, 0);
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(stream));
    }

    [Fact]
    public void Snapshots_claiming_huge_sizes_fail_without_allocating_them()
    {
        using var tooMany = WriteRawSnapshot([], [], SnapshotFile.MaxNodes + 1, 0);
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(tooMany));

        // Within the limit but truncated: must fail on the missing data, not allocate ~4 GB first.
        using var truncated = WriteRawSnapshot([], [], SnapshotFile.MaxNodes, 0);
        var before = GC.GetTotalAllocatedBytes();
        Assert.ThrowsAny<Exception>(() => SnapshotFile.Load(truncated));
        Assert.True(GC.GetTotalAllocatedBytes() - before < 512L * 1024 * 1024);
    }

    // Writes a snapshot with an arbitrary header (counts) and payload, bypassing SnapshotFile.Save.
    private static MemoryStream WriteRawSnapshot(EsNode[] nodes, char[] names, int nodeCount, int namesLength)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("EMSNAP01"u8);
            w.Write(1);
            w.Write(@"C:\Root");
            w.Write(0L);
            w.Write(0L);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(nodeCount);
            w.Write(namesLength);
            w.Write(Marshal.SizeOf<EsNode>());
        }
        using (var brotli = new System.IO.Compression.BrotliStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(MemoryMarshal.AsBytes(nodes.AsSpan()));
            brotli.Write(MemoryMarshal.AsBytes(names.AsSpan()));
        }
        ms.Position = 0;
        return ms;
    }
}
