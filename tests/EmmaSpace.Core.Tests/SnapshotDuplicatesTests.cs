using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Snapshots;

namespace EmmaSpace.Core.Tests;

/// <summary>Duplicate search results saved in snapshots (format version 2).</summary>
public class SnapshotDuplicatesTests
{
    /// <summary>
    /// root: a.bin, b.bin, c.bin (100 each), d.bin, e.bin (200 each), folder/f.bin (100), unique.bin (7),
    /// cloud.bin (100, a cloud placeholder the search never opens).
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public readonly FileTree Tree;
        public readonly int A, B, C, D, E, Folder, F, Unique, Cloud;

        public Fixture()
        {
            var b = new TreeBuilder();
            b.AddRoot(@"C:\Root");
            A = b.AddChild(0, "a.bin", NodeFlags.None, 100);
            B = b.AddChild(0, "b.bin", NodeFlags.None, 100);
            C = b.AddChild(0, "c.bin", NodeFlags.None, 100);
            D = b.AddChild(0, "d.bin", NodeFlags.None, 200);
            E = b.AddChild(0, "e.bin", NodeFlags.None, 200);
            Folder = b.AddChild(0, "folder", NodeFlags.Directory);
            F = b.AddChild(Folder, "f.bin", NodeFlags.None, 100);
            Unique = b.AddChild(0, "unique.bin", NodeFlags.None, 7);
            Cloud = b.AddChild(0, "cloud.bin", NodeFlags.Cloud, 100);
            Tree = b.Build(new ScanInfo(@"C:\Root", DateTimeOffset.Now, TimeSpan.FromSeconds(1), ScanMode.Walker, ScanSource.Managed, 0));
        }

        public DuplicateResult Result() => new(50,
        [
            new DuplicateGroup(200, [D, E]),
            new DuplicateGroup(100, [A, B, C, F]),
        ]);

        public void Dispose() => Tree.Dispose();
    }

    private static int VersionOf(MemoryStream stream) => BitConverter.ToInt32(stream.GetBuffer(), 8);

    private static LoadedSnapshot SaveAndLoad(FileTree tree, DuplicateResult? duplicates, out int version)
    {
        using var stream = new MemoryStream();
        SnapshotFile.Save(tree, stream, duplicates);
        version = VersionOf(stream);
        stream.Position = 0;
        return SnapshotFile.LoadSnapshot(stream);
    }

    [Fact]
    public void Duplicates_round_trip_with_the_tree()
    {
        using var f = new Fixture();
        var loaded = SaveAndLoad(f.Tree, f.Result(), out var version);
        using var tree = loaded.Tree;

        Assert.Equal(2, version);
        Assert.True(f.Tree.Nodes.SequenceEqual(tree.Nodes));
        var duplicates = Assert.IsType<DuplicateResult>(loaded.Duplicates);
        Assert.Equal(50UL, duplicates.MinSize);
        Assert.Equal(2, duplicates.Groups.Count);
        Assert.Equal(200UL, duplicates.Groups[0].FileSize);
        Assert.Equal([f.D, f.E], duplicates.Groups[0].Nodes);
        Assert.Equal([f.A, f.B, f.C, f.F], duplicates.Groups[1].Nodes);
    }

    [Fact]
    public void A_snapshot_without_a_search_stays_version_1_and_loads_without_duplicates()
    {
        using var f = new Fixture();
        var loaded = SaveAndLoad(f.Tree, null, out var version);
        using var tree = loaded.Tree;
        Assert.Equal(1, version); // older Emma Space releases can still open it
        Assert.Null(loaded.Duplicates);
    }

    [Fact]
    public void A_search_that_found_nothing_is_saved_too()
    {
        using var f = new Fixture();
        var loaded = SaveAndLoad(f.Tree, new DuplicateResult(1, []), out var version);
        using var tree = loaded.Tree;
        Assert.Equal(2, version);
        Assert.Empty(Assert.IsType<DuplicateResult>(loaded.Duplicates).Groups);
    }

    [Fact]
    public void Copies_removed_since_the_search_are_left_out()
    {
        using var f = new Fixture();
        f.Tree.Remove(f.A);
        f.Tree.Remove(f.D); // leaves e.bin alone: no longer a duplicate
        f.Tree.Remove(f.Folder); // takes f.bin with it

        var loaded = SaveAndLoad(f.Tree, f.Result(), out _);
        using var tree = loaded.Tree;
        var group = Assert.Single(Assert.IsType<DuplicateResult>(loaded.Duplicates).Groups);
        Assert.Equal([f.B, f.C], group.Nodes);
    }

    [Fact]
    public void Copies_the_search_would_skip_are_not_saved()
    {
        using var f = new Fixture();
        var loaded = SaveAndLoad(f.Tree, new DuplicateResult(1, [new DuplicateGroup(100, [f.A, f.Cloud]), new DuplicateGroup(100, [f.B, f.C])]), out _);
        using var tree = loaded.Tree;
        var group = Assert.Single(loaded.Duplicates!.Groups);
        Assert.Equal([f.B, f.C], group.Nodes);
    }

    [Fact]
    public void Load_without_duplicates_still_reads_a_version_2_snapshot()
    {
        using var f = new Fixture();
        using var stream = new MemoryStream();
        SnapshotFile.Save(f.Tree, stream, f.Result());
        stream.Position = 0;
        using var tree = SnapshotFile.Load(stream); // the Compare tab's way in
        Assert.Equal(f.Tree.Count, tree.Count);
    }

    [Fact]
    public async Task SaveAsync_writes_duplicates_that_LoadSnapshotAsync_reads_back()
    {
        using var temp = new TempTree();
        using var f = new Fixture();
        var path = Path.Combine(temp.Root, "with-duplicates" + SnapshotFile.Extension);
        await SnapshotFile.SaveAsync(f.Tree, path, f.Result(), TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\Root", SnapshotFile.TryReadSummary(path)?.RootPath); // the header reads the same in both versions
        var loaded = await SnapshotFile.LoadSnapshotAsync(path, TestContext.Current.CancellationToken);
        using var tree = loaded.Tree;
        Assert.Equal(2, loaded.Duplicates!.Groups.Count);
    }

    // Damaged or crafted duplicate sections: each must be refused, never shown with file actions on it.

    public static TheoryData<string> CorruptSections =>
    [
        "index out of range", "folder as a copy", "root as a copy", "size mismatch", "node in two groups",
        "single copy", "smaller than the minimum", "too many groups", "negative count", "trailing data",
        "cloud placeholder as a copy", "truncated",
    ];

    [Theory]
    [MemberData(nameof(CorruptSections))]
    public void Corrupt_duplicate_sections_are_rejected(string damage)
    {
        using var f = new Fixture();
        using var stream = WriteVersion2(f.Tree, w =>
        {
            void Group(ulong size, params int[] nodes)
            {
                w.Write(size);
                w.Write(nodes.Length);
                foreach (var n in nodes) w.Write((uint)n);
            }

            w.Write(damage == "smaller than the minimum" ? 150UL : 1UL);
            switch (damage)
            {
                case "too many groups":
                    w.Write(f.Tree.Count);
                    return;
                case "negative count":
                    w.Write(1);
                    w.Write(100UL);
                    w.Write(-1);
                    return;
            }
            w.Write(damage == "node in two groups" ? 2 : 1);
            switch (damage)
            {
                case "index out of range": Group(100, f.A, f.Tree.Count); break;
                case "folder as a copy": Group(100, f.A, f.Folder); break;
                case "root as a copy": Group(100, f.A, FileTree.Root); break;
                case "size mismatch": Group(100, f.A, f.D); break;
                case "node in two groups": Group(100, f.A, f.B); Group(100, f.B, f.C); break;
                case "single copy": Group(100, f.A); break;
                case "smaller than the minimum": Group(100, f.A, f.B); break;
                case "trailing data": Group(100, f.A, f.B); w.Write((byte)0); break;
                case "cloud placeholder as a copy": Group(100, f.A, f.Cloud); break;
                case "truncated": w.Write(100UL); w.Write(3); w.Write((uint)f.A); break; // two of three copies missing
            }
        });
        Assert.Throws<InvalidDataException>(() => SnapshotFile.LoadSnapshot(stream));
    }

    [Fact]
    public void A_wrong_checksum_is_rejected()
    {
        using var f = new Fixture();
        using var stream = new MemoryStream();
        SnapshotFile.Save(f.Tree, stream, f.Result());
        var bytes = stream.ToArray();
        bytes[ChecksumOffset(f.Tree)] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => SnapshotFile.LoadSnapshot(new MemoryStream(bytes)));
    }

    [Fact]
    public void A_damaged_root_path_is_caught_by_the_checksum()
    {
        using var f = new Fixture();
        using var stream = new MemoryStream();
        SnapshotFile.Save(f.Tree, stream, f.Result());
        var bytes = stream.ToArray();
        // "C:\Root" -> "C:\Rook": the saved copies would then name files in another folder.
        var offset = 8 + 4 + 1 + Encoding.UTF8.GetByteCount(@"C:\Roo");
        Assert.Equal((byte)'t', bytes[offset]);
        bytes[offset] = (byte)'k';
        Assert.Throws<InvalidDataException>(() => SnapshotFile.LoadSnapshot(new MemoryStream(bytes)));
    }

    [Fact]
    public void A_copy_changed_into_another_file_by_damage_is_caught_by_the_checksum()
    {
        using var f = new Fixture();
        // Valid in every other way after the damage: f.bin is a live file of the same size, in no other group.
        using var stream = WriteVersion2(f.Tree, w =>
        {
            w.Write(1UL);
            w.Write(1);
            w.Write(100UL);
            w.Write(2);
            w.Write((uint)f.A);
            w.Write((uint)f.B);
        }, section => section[^4] = (byte)f.F);
        Assert.Throws<InvalidDataException>(() => SnapshotFile.LoadSnapshot(stream));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undecodable_compressed_data_is_reported_as_damaged(bool withDuplicates)
    {
        using var f = new Fixture();
        using var stream = new MemoryStream();
        SnapshotFile.Save(f.Tree, stream, withDuplicates ? f.Result() : null);
        var bytes = stream.ToArray();
        var body = ChecksumOffset(f.Tree) + (withDuplicates ? 16 : 0);
        new Random(3).NextBytes(bytes.AsSpan(body));
        // The callers report IOException and InvalidDataException as "Could not open snapshot".
        var error = Record.Exception(() => SnapshotFile.LoadSnapshot(new MemoryStream(bytes)));
        Assert.True(error is InvalidDataException or IOException, error?.GetType().FullName);
    }

    /// <summary>Where the header ends (and a version 2 checksum starts): after the fixed fields and the root path.</summary>
    private static int ChecksumOffset(FileTree tree) =>
        8 + 4 + 1 + Encoding.UTF8.GetByteCount(tree.Info.RootPath) + 8 + 8 + 4 + 4 + 4 + 4 + 4 + 4;

    /// <summary>
    /// A version 2 snapshot of <paramref name="tree"/> with a hand-written duplicates section and a correct
    /// checksum, taken before <paramref name="damage"/> (if any) changes the section.
    /// </summary>
    private static MemoryStream WriteVersion2(FileTree tree, Action<BinaryWriter> write, Action<byte[]>? damage = null)
    {
        byte[] section;
        using (var buffer = new MemoryStream())
        {
            using (var w = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true)) write(w);
            section = buffer.ToArray();
        }
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("EMSNAP01"u8);
            w.Write(2);
            w.Write(tree.Info.RootPath);
            w.Write(tree.Info.ScannedAt.ToUnixTimeMilliseconds());
            w.Write(0L);
            w.Write((int)tree.Info.Mode);
            w.Write((int)tree.Info.Source);
            w.Write(0);
            w.Write(tree.Count);
            w.Write(tree.Names.Length);
            w.Write(Marshal.SizeOf<EsNode>());
        }
        // The checksum covers the header above and everything compressed below.
        var hasher = new XxHash128();
        hasher.Append(ms.ToArray());
        hasher.Append(MemoryMarshal.AsBytes(tree.Nodes));
        hasher.Append(MemoryMarshal.AsBytes(tree.Names));
        hasher.Append(section);
        var checksum = hasher.GetCurrentHashAsUInt128();
        damage?.Invoke(section);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ulong)checksum);
            w.Write((ulong)(checksum >> 64));
        }
        using (var brotli = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(MemoryMarshal.AsBytes(tree.Nodes));
            brotli.Write(MemoryMarshal.AsBytes(tree.Names));
            brotli.Write(section);
        }
        ms.Position = 0;
        return ms;
    }
}
