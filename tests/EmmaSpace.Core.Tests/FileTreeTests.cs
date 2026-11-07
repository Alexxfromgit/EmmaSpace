using System.Runtime.InteropServices;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Tests;

public class FileTreeTests
{
    [Fact]
    public void Node_layout_matches_the_native_struct() => Assert.Equal(64, Marshal.SizeOf<EsNode>());

    [Fact]
    public void Sizes_roll_up_and_children_are_largest_first()
    {
        using var tree = TreeFixtures.Sample();
        ref readonly var root = ref tree.Nodes[FileTree.Root];
        Assert.Equal(3365UL, root.Size);
        Assert.Equal(6u, root.FileCount);
        Assert.Equal(5u, root.DirCount);

        var names = new List<string>();
        foreach (var c in tree.Children(FileTree.Root)) names.Add(tree.GetName(c));
        Assert.Equal(["big.zip", "videos", "docs", "README", "empty"], names);
    }

    [Fact]
    public void Paths_and_lookup_round_trip()
    {
        using var tree = TreeFixtures.Sample();
        var movie = TreeFixtures.Find(tree, Path.Join("videos", "movie.mp4"));
        Assert.NotEqual(FileTree.None, movie);
        Assert.Equal(Path.Join(@"C:\Root", "videos", "movie.mp4"), tree.GetPath(movie));
        Assert.Equal("mp4", tree.GetExtension(movie).ToString());
        Assert.Equal(2, tree.GetDepth(movie));
        Assert.Equal(FileTree.None, TreeFixtures.Find(tree, "nope"));
    }

    [Fact]
    public void Drive_root_paths_do_not_double_the_separator()
    {
        using var tree = TreeFixtures.Sample(@"C:\");
        var zip = TreeFixtures.Find(tree, "big.zip");
        Assert.Equal(@"C:\big.zip", tree.GetPath(zip));
    }

    [Fact]
    public void Remove_updates_ancestors_and_hides_the_subtree()
    {
        using var tree = TreeFixtures.Sample();
        var videos = TreeFixtures.Find(tree, "videos");
        var movie = TreeFixtures.Find(tree, Path.Join("videos", "movie.mp4"));
        int? removed = null;
        tree.NodeRemoved += n => removed = n;

        tree.Remove(videos);

        Assert.Equal(videos, removed);
        Assert.Equal(2065UL, tree[FileTree.Root].Size);
        Assert.Equal(4u, tree[FileTree.Root].FileCount);
        Assert.Equal(4u, tree[FileTree.Root].DirCount);
        Assert.True(tree[movie].IsDeleted);
        foreach (var c in tree.Children(FileTree.Root)) Assert.NotEqual(videos, c);
    }

    [Fact]
    public void Remove_of_a_middle_sibling_keeps_the_chain_intact()
    {
        using var tree = TreeFixtures.Sample();
        var docs = TreeFixtures.Find(tree, "docs");
        tree.Remove(docs);
        var names = new List<string>();
        foreach (var c in tree.Children(FileTree.Root)) names.Add(tree.GetName(c));
        Assert.Equal(["big.zip", "videos", "README", "empty"], names);
    }
}
