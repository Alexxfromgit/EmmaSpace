using EmmaSpace.Core.Export;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Snapshots;

namespace EmmaSpace.Core.Tests;

public class SnapshotAndExportTests
{
    [Fact]
    public void Snapshot_round_trips_every_node()
    {
        using var tree = TreeFixtures.Sample();
        using var stream = new MemoryStream();
        SnapshotFile.Save(tree, stream);
        stream.Position = 0;
        using var loaded = SnapshotFile.Load(stream);

        Assert.Equal(tree.Count, loaded.Count);
        Assert.Equal(ScanSource.Snapshot, loaded.Info.Source);
        Assert.Equal(tree.RootPath, loaded.RootPath);
        Assert.True(tree.Nodes.SequenceEqual(loaded.Nodes));
        Assert.True(tree.Names.SequenceEqual(loaded.Names));
    }

    [Fact]
    public void Corrupt_snapshots_are_rejected()
    {
        using var stream = new MemoryStream("not a snapshot at all"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(stream));
    }

    [Fact]
    public void Diff_reports_growth_additions_and_removals()
    {
        using var older = TreeFixtures.Sample();

        var b = new TreeBuilder();
        b.AddRoot(@"C:\Root");
        var videos = b.AddChild(0, "videos", NodeFlags.Directory);
        b.AddChild(videos, "movie.mp4", NodeFlags.None, 1000);
        b.AddChild(videos, "clip.MKV", NodeFlags.None, 900);       // grew 300 -> 900
        b.AddChild(videos, "new.mov", NodeFlags.None, 400);        // added
        var docs = b.AddChild(0, "docs", NodeFlags.Directory);
        b.AddChild(docs, "a.pdf", NodeFlags.None, 50);             // notes.txt removed, sub removed
        b.AddChild(0, "empty", NodeFlags.Directory);
        b.AddChild(0, "big.zip", NodeFlags.None, 2000);
        b.AddChild(0, "README", NodeFlags.None, 5);
        using var newer = b.Build(older.Info);

        var diff = SnapshotDiff.Compare(older, newer, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3365UL, diff.OldTotal);
        Assert.Equal(4355UL, diff.NewTotal);

        var byPath = diff.Changes.ToDictionary(c => Path.GetRelativePath(@"C:\Root", c.Path));
        Assert.Equal(ChangeKind.Grown, byPath["videos"].Kind);
        Assert.Equal(1000, byPath["videos"].Delta);
        Assert.Equal(ChangeKind.Grown, byPath[Path.Join("videos", "clip.MKV")].Kind);
        Assert.Equal(ChangeKind.Added, byPath[Path.Join("videos", "new.mov")].Kind);
        Assert.Equal(ChangeKind.Removed, byPath[Path.Join("docs", "notes.txt")].Kind);
        Assert.Equal(ChangeKind.Removed, byPath[Path.Join("docs", "sub")].Kind);
        Assert.False(byPath.ContainsKey("big.zip"));
        Assert.Equal("videos", Path.GetRelativePath(@"C:\Root", diff.Changes[0].Path));
    }

    [Fact]
    public void Csv_lists_folders_largest_first_and_escapes_paths()
    {
        var b = new TreeBuilder();
        b.AddRoot(@"C:\Root");
        b.AddChild(0, "a, \"quoted\"", NodeFlags.Directory);
        b.AddChild(0, "file.bin", NodeFlags.None, 10);
        using var tree = b.Build(TreeFixtures.Sample().Info);

        var folders = new StringWriter();
        CsvExporter.Write(tree, folders, new CsvExportOptions(), TestContext.Current.CancellationToken);
        var lines = folders.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("\"C:\\Root" + Path.DirectorySeparatorChar + "a, \"\"quoted\"\"\",Folder,", lines[2]);

        var withFiles = new StringWriter();
        CsvExporter.Write(tree, withFiles, new CsvExportOptions { IncludeFiles = true }, TestContext.Current.CancellationToken);
        Assert.Contains(",File,10,", withFiles.ToString());
    }

    [Fact]
    public void Html_report_is_self_contained_and_encoded()
    {
        var b = new TreeBuilder();
        b.AddRoot(@"C:\Root");
        b.AddChild(0, "<script>.txt", NodeFlags.None, 10);
        using var tree = b.Build(TreeFixtures.Sample().Info);
        var html = HtmlReportExporter.Build(tree, TestContext.Current.CancellationToken);
        Assert.StartsWith("<!doctype html>", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;.txt", html);
        Assert.DoesNotContain("http", html);
    }
}
