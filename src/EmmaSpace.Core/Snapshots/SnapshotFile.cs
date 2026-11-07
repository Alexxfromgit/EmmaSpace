using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Snapshots;

/// <summary>
/// Saves and loads scans as <c>.emmasnap</c> files: a small header followed by the raw node
/// array and name pool, Brotli-compressed. Loading is a straight memory copy, no parsing.
/// </summary>
public static class SnapshotFile
{
    public const string Extension = ".emmasnap";
    private static ReadOnlySpan<byte> Magic => "EMSNAP01"u8;
    private const int FormatVersion = 1;

    public static async Task SaveAsync(FileTree tree, string path, CancellationToken ct = default)
    {
        var temp = path + ".tmp";
        await using (var file = File.Create(temp))
        {
            await Task.Run(() => Save(tree, file), ct);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static Task<FileTree> LoadAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var file = File.OpenRead(path);
            return Load(file);
        }, ct);

    public static void Save(FileTree tree, Stream output)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(tree.Info.RootPath);
        writer.Write(tree.Info.ScannedAt.ToUnixTimeMilliseconds());
        writer.Write((long)tree.Info.Elapsed.TotalMilliseconds);
        writer.Write((int)tree.Info.Mode);
        writer.Write((int)tree.Info.Source);
        writer.Write(tree.Info.Errors);
        writer.Write(tree.Count);
        writer.Write(tree.Names.Length);
        writer.Write(Marshal.SizeOf<EsNode>());
        writer.Flush();

        using var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true);
        brotli.Write(MemoryMarshal.AsBytes(tree.Nodes));
        brotli.Write(MemoryMarshal.AsBytes(tree.Names));
    }

    public static FileTree Load(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        Span<byte> magic = stackalloc byte[8];
        input.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("Not an Emma Space snapshot.");
        var version = reader.ReadInt32();
        if (version != FormatVersion) throw new InvalidDataException($"Unsupported snapshot version {version}.");

        var rootPath = reader.ReadString();
        var scannedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64()).ToLocalTime();
        var elapsed = TimeSpan.FromMilliseconds(reader.ReadInt64());
        var mode = (ScanMode)reader.ReadInt32();
        reader.ReadInt32(); // original scan source, informational
        var errors = reader.ReadInt32();
        var nodeCount = reader.ReadInt32();
        var namesLength = reader.ReadInt32();
        var nodeSize = reader.ReadInt32();
        if (nodeSize != Marshal.SizeOf<EsNode>() || nodeCount <= 0 || namesLength < 0)
            throw new InvalidDataException("Snapshot header is corrupt.");

        var nodes = new EsNode[nodeCount];
        var names = new char[namesLength];
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true))
        {
            brotli.ReadExactly(MemoryMarshal.AsBytes(nodes.AsSpan()));
            brotli.ReadExactly(MemoryMarshal.AsBytes(names.AsSpan()));
        }

        Validate(nodes, namesLength);
        var info = new ScanInfo(rootPath, scannedAt, elapsed, mode, ScanSource.Snapshot, errors);
        return new FileTree(new ArrayNodeStorage(nodes, names), info);
    }

    // Guards against corrupt files producing out-of-range indexes later on.
    private static void Validate(EsNode[] nodes, int namesLength)
    {
        var count = (uint)nodes.Length;
        for (var i = 0; i < nodes.Length; i++)
        {
            ref readonly var n = ref nodes[i];
            var linksOk = (n.Parent == EsNode.None ? i == 0 : n.Parent < i) &&
                          (n.FirstChild == EsNode.None || (n.FirstChild > i && n.FirstChild < count)) &&
                          (n.NextSibling == EsNode.None || n.NextSibling < count);
            if (!linksOk || (long)n.NameOffset + n.NameLength > namesLength)
                throw new InvalidDataException($"Snapshot node {i} is corrupt.");
        }
    }
}
