using System.Runtime.InteropServices;
using System.Text;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Export;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Scanning;
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
    public void Diff_finds_changes_in_folders_whose_totals_did_not_change()
    {
        static FileTree Build(string fileA, ulong sizeA, string fileB, ulong sizeB)
        {
            var b = new TreeBuilder();
            b.AddRoot(@"C:\Root");
            var docs = b.AddChild(0, "docs", NodeFlags.Directory);
            b.AddChild(docs, fileA, NodeFlags.None, sizeA);
            b.AddChild(docs, fileB, NodeFlags.None, sizeB);
            return b.Build(Info());
        }

        // Same folder size and file count: one file renamed, one grew while another shrank.
        using var older = Build("report.txt", 100, "grows.bin", 50);
        using var newer = Build("report-final.txt", 70, "grows.bin", 80);
        var diff = SnapshotDiff.Compare(older, newer, ct: TestContext.Current.CancellationToken);

        var byName = diff.Changes.ToDictionary(c => Path.GetFileName(c.Path), c => c.Kind);
        Assert.Equal(ChangeKind.Removed, byName["report.txt"]);
        Assert.Equal(ChangeKind.Added, byName["report-final.txt"]);
        Assert.Equal(ChangeKind.Grown, byName["grows.bin"]);
        Assert.False(byName.ContainsKey("docs")); // its total really is unchanged
    }

    [Fact]
    public void Compare_only_accepts_scans_of_the_same_location()
    {
        var sep = Path.DirectorySeparatorChar;
        using var a = TreeFixtures.Sample(Path.Join("data", "A"));
        using var aWithSlash = TreeFixtures.Sample(Path.Join("data", "A") + sep);
        using var b = TreeFixtures.Sample(Path.Join("data", "B"));
        Assert.True(SnapshotDiff.SameLocation(a, aWithSlash));
        Assert.False(SnapshotDiff.SameLocation(a, b));
    }

    [Fact]
    public async Task Saving_copies_the_tree_before_any_removal_can_interleave()
    {
        using var tree = TreeFixtures.Sample();
        var path = Path.Combine(Path.GetTempPath(), "emmaspace-" + Guid.NewGuid().ToString("N") + SnapshotFile.Extension);
        try
        {
            var save = SnapshotFile.SaveAsync(tree, path, TestContext.Current.CancellationToken);
            // Runs while the save may still be compressing in the background.
            tree.Remove(TreeFixtures.Find(tree, "videos"));
            await save;

            using var loaded = await SnapshotFile.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(3365UL, loaded.Nodes[FileTree.Root].Size); // the state when saving started
            Assert.NotEqual(FileTree.None, TreeFixtures.Find(loaded, "videos"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Exports_copy_the_tree_before_any_removal_can_interleave()
    {
        using var tree = TreeFixtures.Sample();
        var csv = Path.Combine(Path.GetTempPath(), "emmaspace-" + Guid.NewGuid().ToString("N") + ".csv");
        var html = Path.ChangeExtension(csv, ".html");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var csvExport = CsvExporter.ExportAsync(tree, csv, new CsvExportOptions { IncludeFiles = true, MaxDepth = null }, ct);
            var htmlExport = HtmlReportExporter.ExportAsync(tree, html, ct);
            // Runs while the exports may still be reading in the background.
            tree.Remove(TreeFixtures.Find(tree, "videos"));
            await Task.WhenAll(csvExport, htmlExport);

            var rows = await File.ReadAllLinesAsync(csv, ct);
            Assert.Contains(rows, r => r.Contains("movie.mp4", StringComparison.Ordinal));
            Assert.StartsWith(Escape(tree.RootPath) + ",Folder,3365,", rows[1]); // totals from the same state
            var report = await File.ReadAllTextAsync(html, ct);
            Assert.Contains("movie.mp4", report, StringComparison.Ordinal);
            Assert.Contains("videos", report, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(csv);
            File.Delete(html);
        }

        static string Escape(string s) => s.Contains(',') ? "\"" + s + "\"" : s;
    }

    [Fact]
    public void Removals_bump_the_tree_revision()
    {
        using var tree = TreeFixtures.Sample();
        var before = tree.Revision;
        tree.Remove(TreeFixtures.Find(tree, "videos"));
        Assert.Equal(before + 1, tree.Revision);
        using var copy = tree.Copy();
        tree.Remove(TreeFixtures.Find(tree, "docs"));
        Assert.NotEqual(FileTree.None, TreeFixtures.Find(copy, "docs")); // a copy is not affected
    }

    [Fact]
    public void Snapshots_with_unreachable_live_nodes_are_rejected()
    {
        var nodes = new EsNode[2];
        nodes[0] = new EsNode { Parent = EsNode.None, FirstChild = EsNode.None, NextSibling = EsNode.None, Flags = NodeFlags.Directory };
        nodes[1] = new EsNode { Parent = 0, FirstChild = EsNode.None, NextSibling = EsNode.None, NameLength = 1, Size = 10, FileCount = 1 }; // live, but no chain leads to it
        using var stream = WriteRawSnapshot(nodes, ['a'], nodes.Length, 1);
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(stream));
    }

    [Fact]
    public void Snapshots_keep_removed_nodes_unlinked()
    {
        using var tree = TreeFixtures.Sample();
        tree.Remove(TreeFixtures.Find(tree, "docs")); // docs and its children are deleted and unlinked
        using var stream = new MemoryStream();
        SnapshotFile.Save(tree, stream);
        stream.Position = 0;
        using var loaded = SnapshotFile.Load(stream); // must still validate
        Assert.Equal(tree.Nodes[FileTree.Root].Size, loaded.Nodes[FileTree.Root].Size);
    }

    [Theory]
    [InlineData(long.MaxValue, 0L)]
    [InlineData(0L, long.MaxValue)]
    [InlineData(0L, -1L)]
    public void Snapshots_with_invalid_header_times_are_reported_as_invalid(long scannedAtMs, long elapsedMs)
    {
        var nodes = new[] { new EsNode { Parent = EsNode.None, FirstChild = EsNode.None, NextSibling = EsNode.None, Flags = NodeFlags.Directory } };
        using var stream = WriteRawSnapshot(nodes, [], 1, 0, scannedAtMs, elapsedMs);
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(stream));
    }

    [Fact]
    public void Snapshots_with_out_of_range_item_times_are_rejected()
    {
        var nodes = new[] { new EsNode { Parent = EsNode.None, FirstChild = EsNode.None, NextSibling = EsNode.None, Flags = NodeFlags.Directory, MTime = long.MaxValue } };
        using var stream = WriteRawSnapshot(nodes, [], 1, 0);
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(stream));
    }

    [Fact]
    public void Out_of_range_times_are_shown_as_unknown_instead_of_throwing()
    {
        Assert.Null(UnixTime.ToDateTimeOffset(long.MaxValue));
        Assert.Null(UnixTime.ToDateTimeOffset(0));
        Assert.Equal(2024, UnixTime.ToDateTimeOffset(1704067200000)!.Value.UtcDateTime.Year);
    }

    [Fact]
    public void Scan_roots_are_made_absolute()
    {
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "docs"), ManagedScanner.NormalizeRoot("docs"));
        if (OperatingSystem.IsWindows()) Assert.Equal(@"C:\", ManagedScanner.NormalizeRoot("C:"));
        Assert.Throws<ScanException>(() => ManagedScanner.NormalizeRoot("bad" + (char)0 + "path")); // NUL: never valid in a path
    }

    [Fact]
    public void Scan_roots_keep_leading_and_trailing_spaces()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows drops trailing spaces from names itself.");
        Assert.Equal("/tmp/reports ", ManagedScanner.NormalizeRoot("/tmp/reports "));
        Assert.Equal("/tmp/ reports", ManagedScanner.NormalizeRoot("/tmp/ reports"));
    }

    [Fact]
    public async Task The_native_scanner_accepts_relative_paths()
    {
        Assert.SkipUnless(NativeEngine.IsAvailable, "Native engine not built for this platform.");
        using var temp = new TempTree();
        temp.File("a.bin", 1234);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, temp.Root);
        using var tree = await new NativeScanner().ScanAsync(relative, new ScanOptions(), null, TestContext.Current.CancellationToken);
        Assert.Equal(1234UL, tree.Nodes[FileTree.Root].Size);
        Assert.True(Path.IsPathFullyQualified(tree.RootPath));
    }

    [Fact]
    public void Snapshots_with_cyclic_sibling_chains_are_rejected()
    {
        var nodes = new EsNode[2];
        nodes[0] = new EsNode { Parent = EsNode.None, FirstChild = 1, NextSibling = EsNode.None, Flags = NodeFlags.Directory };
        nodes[1] = new EsNode { Parent = 0, FirstChild = EsNode.None, NextSibling = 1, NameLength = 1, FileCount = 1 }; // points at itself
        using var stream = WriteRawSnapshot(nodes, ['a'], nodes.Length, 1);
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

    // root -> docs -> file (10 bytes), fully linked, with the given name for the file and the given
    // cached totals on both folders.
    private static EsNode[] DocsWithFile(int fileNameLength, ulong folderSize, uint folderFiles)
    {
        var nodes = new EsNode[3];
        nodes[0] = new EsNode { Parent = EsNode.None, FirstChild = 1, NextSibling = EsNode.None, Flags = NodeFlags.Directory,
                                Size = folderSize, FileCount = folderFiles, DirCount = 1 };
        nodes[1] = new EsNode { Parent = 0, FirstChild = 2, NextSibling = EsNode.None, Flags = NodeFlags.Directory,
                                NameOffset = 0, NameLength = 4, Size = folderSize, FileCount = folderFiles };
        nodes[2] = new EsNode { Parent = 1, FirstChild = EsNode.None, NextSibling = EsNode.None,
                                NameOffset = 4, NameLength = (ushort)fileNameLength, Size = 10, FileCount = 1 };
        return nodes;
    }

    private static MemoryStream DocsSnapshot(string fileName, ulong folderSize = 10, uint folderFiles = 1)
    {
        var names = ("docs" + fileName).ToCharArray();
        return WriteRawSnapshot(DocsWithFile(fileName.Length, folderSize, folderFiles), names, 3, names.Length);
    }

    [Fact]
    public void Consistent_raw_snapshots_load()
    {
        using var stream = DocsSnapshot("file.bin");
        using var tree = SnapshotFile.Load(stream);
        Assert.Equal(10UL, tree.Nodes[FileTree.Root].Size);
    }

    [Fact]
    public void Snapshots_whose_folder_totals_disagree_with_their_contents_are_rejected()
    {
        // A non-empty folder claiming to be empty would be offered for clean-up, and removing the
        // file would underflow its totals.
        using var zero = DocsSnapshot("file.bin", folderSize: 0, folderFiles: 0);
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(zero));
        using var tooBig = DocsSnapshot("file.bin", folderSize: 11, folderFiles: 1);
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(tooBig));
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("nul\0inside")]
    public void Snapshots_with_names_that_leave_their_folder_are_rejected(string name)
    {
        using var stream = DocsSnapshot(name);
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(stream));
    }

    [Fact]
    public void Snapshots_with_backslash_names_are_rejected_only_where_it_separates_folders()
    {
        using var stream = DocsSnapshot(@"..\outside.bin");
        if (OperatingSystem.IsWindows())
            Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(stream));
        else
            SnapshotFile.Load(stream).Dispose(); // a legal file name on macOS and Linux
    }

    [Fact]
    public void Snapshots_of_folders_with_long_paths_load()
    {
        var root = Path.Join(Path.GetTempPath(), new string('d', 700));
        var b = new TreeBuilder();
        b.AddRoot(root);
        using var tree = b.Build(new ScanInfo(root, DateTimeOffset.Now, TimeSpan.Zero, ScanMode.Walker, ScanSource.Managed, 0));
        using var stream = new MemoryStream();
        SnapshotFile.Save(tree, stream);
        stream.Position = 0;
        using var loaded = SnapshotFile.Load(stream);
        Assert.Equal(root, loaded.RootPath);
    }

    // Writes a snapshot with an arbitrary header (counts) and payload, bypassing SnapshotFile.Save.
    private static MemoryStream WriteRawSnapshot(EsNode[] nodes, char[] names, int nodeCount, int namesLength,
        long scannedAtMs = 0, long elapsedMs = 0)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("EMSNAP01"u8);
            w.Write(1);
            w.Write(@"C:\Root");
            w.Write(scannedAtMs);
            w.Write(elapsedMs);
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
