using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Scanning;

namespace EmmaSpace.Core.Tests;

public class AnalysisTests
{
    [Fact]
    public void File_types_group_by_extension_case_insensitively()
    {
        using var tree = TreeFixtures.Sample();
        var report = FileTypeBreakdown.Compute(tree, ct: TestContext.Current.CancellationToken);

        Assert.Equal("zip", report.Extensions[0].Extension);
        Assert.Equal(FileCategory.Archives, report.Extensions[0].Category);
        Assert.Contains(report.Extensions, e => e.Extension == "mkv" && e.Category == FileCategory.Video);
        Assert.Contains(report.Extensions, e => e.Extension == FileTypeBreakdown.NoExtension && e.Bytes == 5);

        var video = Assert.Single(report.Categories, c => c.Category == FileCategory.Video);
        Assert.Equal(1300UL, video.Bytes);
        Assert.Equal(2, video.Count);
        Assert.Equal(3365UL, (ulong)report.Categories.Sum(c => (long)c.Bytes));
    }

    [Fact]
    public void Largest_files_are_sorted_and_limited()
    {
        using var tree = TreeFixtures.Sample();
        var largest = FileQueries.LargestFiles(tree, 3, ct: TestContext.Current.CancellationToken);
        Assert.Equal(["big.zip", "movie.mp4", "clip.MKV"], largest.Select(tree.GetName));
    }

    [Fact]
    public void Largest_files_can_be_scoped_to_a_folder()
    {
        using var tree = TreeFixtures.Sample();
        var docs = TreeFixtures.Find(tree, "docs");
        Assert.Equal(["a.pdf", "notes.txt"], FileQueries.LargestFiles(tree, 10, docs, TestContext.Current.CancellationToken).Select(tree.GetName));
    }

    [Fact]
    public void Old_files_use_the_cutoff()
    {
        using var tree = TreeFixtures.Sample();
        var old = FileQueries.OldFiles(tree, DateTimeOffset.UtcNow.AddYears(-1), AgeBasis.LastModified, 100, ct: TestContext.Current.CancellationToken);
        Assert.Equal(["movie.mp4", "notes.txt"], old.Select(tree.GetName));
    }

    [Fact]
    public void Empty_folders_report_only_the_topmost()
    {
        using var tree = TreeFixtures.Sample();
        var empty = FileQueries.EmptyFolders(tree, ct: TestContext.Current.CancellationToken).Select(tree.GetName).Order().ToList();
        Assert.Equal(["empty", "sub"], empty);
    }

    [Fact]
    public async Task Duplicates_are_found_by_content_not_name()
    {
        using var temp = new TempTree();
        var random = new Random(42);
        var big = new byte[300_000];
        random.NextBytes(big);
        var bigVariant = (byte[])big.Clone();
        bigVariant[150_000] ^= 0xFF; // same size, same head and tail, different middle

        temp.File("a/one.bin", big);
        temp.File("b/two.bin", big);
        temp.File("c/three.bin", big);
        temp.File("d/variant.bin", bigVariant);
        temp.File("small1.txt", 5000, (byte)'q');
        temp.File("small2.txt", 5000, (byte)'q');
        temp.File("small3.txt", 5000, (byte)'r');
        temp.File("unique.bin", 12345);

        using var tree = await new ManagedScanner().ScanAsync(temp.Root, new ScanOptions(), null, TestContext.Current.CancellationToken);
        var groups = await DuplicateFinder.FindAsync(tree, new DuplicateOptions { MinSize = 1 }, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, groups.Count);
        var first = groups[0];
        Assert.Equal(300_000UL, first.FileSize);
        Assert.Equal(["one.bin", "three.bin", "two.bin"], first.Nodes.Select(tree.GetName).Order());
        Assert.Equal(600_000UL, first.WastedBytes);
        Assert.Equal(["small1.txt", "small2.txt"], groups[1].Nodes.Select(tree.GetName).Order());
    }

    [Fact]
    public async Task Duplicates_respect_the_minimum_size()
    {
        using var temp = new TempTree();
        temp.File("x1", 100);
        temp.File("x2", 100);
        using var tree = await new ManagedScanner().ScanAsync(temp.Root, new ScanOptions(), null, TestContext.Current.CancellationToken);
        var groups = await DuplicateFinder.FindAsync(tree, new DuplicateOptions { MinSize = 1000 }, null, TestContext.Current.CancellationToken);
        Assert.Empty(groups);
    }
}
