using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using EmmaSpace.Core.Formatting;
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

    /// <summary>How every snapshot file starts, whatever its format version (used to recognize the file type).</summary>
    public const string MagicPrefix = "EMSNAP";
    private static ReadOnlySpan<byte> Magic => "EMSNAP01"u8;
    private const int FormatVersion = 1;

    // Far above any real scan (a 5.8 M-entry system drive is well under 10 M nodes), but bounded,
    // so a malformed header cannot ask for an arbitrary amount of memory.
    internal const int MaxNodes = 64 * 1024 * 1024;
    // Entry names are at most 255 UTF-16 units on every supported file system; the root's name is
    // its full path, which can be far longer (long paths), so it gets an allowance of its own.
    private const int MaxNameCharsPerNode = 512;
    private const int MaxRootNameChars = ushort.MaxValue;

    /// <summary>
    /// Saves a snapshot. Call it on the thread that changes the tree (the UI thread): the arrays
    /// are copied before the first await, so a removal while the file is being compressed cannot
    /// produce a half-updated snapshot, and native memory is not touched in the background.
    /// </summary>
    public static async Task SaveAsync(FileTree tree, string path, CancellationToken ct = default)
    {
        using var copy = tree.Copy();
        var temp = path + ".tmp";
        await using (var file = File.Create(temp))
        {
            await Task.Run(() => Save(copy, file), ct);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static Task<FileTree> LoadAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var file = File.OpenRead(path);
            return Load(file);
        }, ct);

    /// <summary>Saves synchronously, on the calling thread (which must be the one changing the tree).</summary>
    public static void Save(FileTree tree, Stream output) => Write(tree.Info, tree.Nodes, tree.Names, output);

    private static void Write(ScanInfo info, ReadOnlySpan<EsNode> nodes, ReadOnlySpan<char> names, Stream output)
    {
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(info.RootPath);
            writer.Write(info.ScannedAt.ToUnixTimeMilliseconds());
            writer.Write((long)info.Elapsed.TotalMilliseconds);
            writer.Write((int)info.Mode);
            writer.Write((int)info.Source);
            writer.Write(info.Errors);
            writer.Write(nodes.Length);
            writer.Write(names.Length);
            writer.Write(Marshal.SizeOf<EsNode>());
        }

        using var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true);
        brotli.Write(MemoryMarshal.AsBytes(nodes));
        brotli.Write(MemoryMarshal.AsBytes(names));
    }

    /// <summary>
    /// What a snapshot file is of and when it was taken, read from its header only (cheap, for asking
    /// the user before loading). Null if it isn't a readable snapshot.
    /// </summary>
    public static (string RootPath, DateTimeOffset ScannedAt)? TryReadSummary(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            using var reader = new BinaryReader(input, Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion) return null;
            var rootPath = ReadRootPath(reader);
            var scannedAtMs = reader.ReadInt64();
            return UnixTime.IsValid(scannedAtMs) ? (rootPath, DateTimeOffset.FromUnixTimeMilliseconds(scannedAtMs).ToLocalTime()) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null; // IOException includes EndOfStreamException (a truncated file)
        }
    }

    /// <summary>
    /// The root path as BinaryWriter.Write(string) stored it (a 7-bit encoded byte count, then UTF-8),
    /// with the count checked first, so a damaged or hostile header can't ask for gigabytes.
    /// </summary>
    private static string ReadRootPath(BinaryReader reader)
    {
        int bytes;
        try
        {
            bytes = reader.Read7BitEncodedInt();
        }
        catch (FormatException)
        {
            throw new InvalidDataException("Snapshot header is corrupt.");
        }
        // UTF-8 needs at most 3 bytes per UTF-16 unit.
        if (bytes < 0 || bytes > MaxRootNameChars * 3) throw new InvalidDataException("Snapshot header is corrupt.");
        var utf8 = reader.ReadBytes(bytes);
        if (utf8.Length != bytes) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(utf8);
    }

    public static FileTree Load(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        Span<byte> magic = stackalloc byte[8];
        input.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("Not an Emma Space snapshot.");
        var version = reader.ReadInt32();
        if (version != FormatVersion) throw new InvalidDataException($"Unsupported snapshot version {version}.");

        var rootPath = ReadRootPath(reader);
        var scannedAtMs = reader.ReadInt64();
        var elapsedMs = reader.ReadInt64();
        if (!UnixTime.IsValid(scannedAtMs) || elapsedMs < 0 || elapsedMs > (long)TimeSpan.MaxValue.TotalMilliseconds)
            throw new InvalidDataException("Snapshot header has an invalid date or duration.");
        var scannedAt = DateTimeOffset.FromUnixTimeMilliseconds(scannedAtMs).ToLocalTime();
        var elapsed = TimeSpan.FromMilliseconds(elapsedMs);
        var mode = (ScanMode)reader.ReadInt32();
        reader.ReadInt32(); // original scan source, informational
        var errors = reader.ReadInt32();
        var nodeCount = reader.ReadInt32();
        var namesLength = reader.ReadInt32();
        var nodeSize = reader.ReadInt32();
        if (nodeSize != Marshal.SizeOf<EsNode>() || nodeCount <= 0 || nodeCount > MaxNodes || namesLength < 0 ||
            namesLength > (long)(nodeCount - 1) * MaxNameCharsPerNode + MaxRootNameChars)
            throw new InvalidDataException("Snapshot header is corrupt.");

        EsNode[] nodes;
        char[] names;
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true))
        {
            nodes = ReadArray<EsNode>(brotli, nodeCount);
            names = ReadArray<char>(brotli, namesLength);
        }

        Validate(nodes, names);
        var info = new ScanInfo(rootPath, scannedAt, elapsed, mode, ScanSource.Snapshot, errors);
        return new FileTree(new ArrayNodeStorage(nodes, names), info);
    }

    /// <summary>
    /// Reads <paramref name="count"/> items, growing the array only as data actually arrives, so a
    /// small truncated file fails early instead of first allocating what its header claims.
    /// </summary>
    private static T[] ReadArray<T>(Stream stream, int count) where T : unmanaged
    {
        const int chunk = 1 << 20;
        var items = new T[Math.Min(count, chunk)];
        var filled = 0;
        while (filled < count)
        {
            if (filled == items.Length) Array.Resize(ref items, (int)Math.Min(count, (long)items.Length * 2));
            var want = Math.Min(items.Length - filled, chunk);
            stream.ReadExactly(MemoryMarshal.AsBytes(items.AsSpan(filled, want)));
            filled += want;
        }
        return items;
    }

    // Guards against corrupt or crafted files: out-of-range indexes, endless loops or shared
    // children, names that would make GetPath leave the root, and cached totals that disagree with
    // the nodes below them. Every child chain must lead only to nodes that name this parent, and
    // no node may appear in more than one chain (or twice in one).
    private static void Validate(EsNode[] nodes, char[] names)
    {
        var count = (uint)nodes.Length;
        for (var i = 0; i < nodes.Length; i++)
        {
            ref readonly var n = ref nodes[i];
            var linksOk = (n.Parent == EsNode.None ? i == 0 : n.Parent < i) &&
                          (n.FirstChild == EsNode.None || (n.FirstChild > i && n.FirstChild < count && n.IsDirectory)) &&
                          (n.NextSibling == EsNode.None || n.NextSibling < count);
            if (!linksOk || (long)n.NameOffset + n.NameLength > names.Length || !UnixTime.IsValid(n.MTime) ||
                !UnixTime.IsValid(n.ATime))
                throw new InvalidDataException($"Snapshot node {i} is corrupt.");
            // E.g. "a\b", legal on macOS and Linux, in a snapshot opened on Windows: its path would
            // name a different file here, so the snapshot cannot be shown on this system.
            if (i > 0 && !IsEntryName(names.AsSpan((int)n.NameOffset, n.NameLength)))
                throw new InvalidDataException(
                    $"Snapshot contains a name that is not valid on this system: \"{SafeForMessage(names.AsSpan((int)n.NameOffset, n.NameLength))}\".");
        }

        var linked = new bool[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            for (var c = nodes[i].FirstChild; c != EsNode.None; c = nodes[c].NextSibling)
            {
                if (linked[c] || nodes[c].Parent != (uint)i)
                    throw new InvalidDataException($"Snapshot node {c} is linked inconsistently.");
                linked[c] = true;
            }
        }

        // Every live node must be reachable from the root: the tree views walk child chains while
        // analyses scan the arrays, so an unlinked live node would show up in one but not the other
        // (and removing it would corrupt the totals). Removed nodes are unlinked on purpose and
        // marked deleted together with everything below them.
        if (nodes[0].IsDeleted) throw new InvalidDataException("Snapshot root is marked deleted.");
        for (var i = 1; i < nodes.Length; i++)
        {
            if (nodes[i].IsDeleted) continue;
            if (!linked[i] || nodes[nodes[i].Parent].IsDeleted)
                throw new InvalidDataException($"Snapshot node {i} is not reachable from the root.");
        }

        ValidateTotals(nodes);
    }

    // Folder sizes and counts are cached subtree totals that clean-up suggestions and removals
    // rely on (an "empty" folder with files inside, or a removal that underflows its ancestors),
    // so they must equal the sums over the live nodes below. Children always follow their parent,
    // so one backwards pass sums every subtree.
    private static void ValidateTotals(EsNode[] nodes)
    {
        var sums = new (ulong Size, ulong Allocated, uint Files, uint Dirs)[nodes.Length];
        try
        {
            for (var i = nodes.Length - 1; i >= 0; i--)
            {
                ref readonly var n = ref nodes[i];
                if (n.IsDeleted) continue;
                var expected = n.IsDirectory ? sums[i] : (Size: n.Size, Allocated: n.Allocated, Files: 1u, Dirs: 0u);
                if (n.Size != expected.Size || n.Allocated != expected.Allocated || n.FileCount != expected.Files ||
                    n.DirCount != expected.Dirs)
                    throw new InvalidDataException($"Snapshot node {i} has totals that do not match its contents.");
                if (i == 0) break;
                ref var p = ref sums[n.Parent];
                p.Size = checked(p.Size + n.Size);
                p.Allocated = checked(p.Allocated + n.Allocated);
                p.Files += n.FileCount;
                p.Dirs += n.DirCount + (n.IsDirectory ? 1u : 0u);
            }
        }
        catch (OverflowException)
        {
            throw new InvalidDataException("Snapshot totals overflow.");
        }
    }

    // A single path component: GetPath joins these under the root, so a separator, NUL or a
    // "." / ".." component would point file actions somewhere else.
    private static bool IsEntryName(ReadOnlySpan<char> name) =>
        !name.IsEmpty && name.Length <= MaxNameCharsPerNode && name is not "." and not ".." &&
        name.IndexOfAny(s_forbiddenNameChars) < 0;

    private static string SafeForMessage(ReadOnlySpan<char> name) =>
        new string(name[..Math.Min(name.Length, 80)]).Replace('\0', '\uFFFD');

    // Backslash separates folders only on Windows; elsewhere it is a valid character in names.
    private static readonly System.Buffers.SearchValues<char> s_forbiddenNameChars =
        System.Buffers.SearchValues.Create(OperatingSystem.IsWindows() ? "/\\\0" : "/\0");
}
