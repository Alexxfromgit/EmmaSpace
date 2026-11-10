using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Snapshots;

/// <summary>A loaded snapshot: the tree, and the duplicate search results saved with it, if any.</summary>
public sealed record LoadedSnapshot(FileTree Tree, DuplicateResult? Duplicates);

/// <summary>
/// Saves and loads scans as <c>.emmasnap</c> files: a small header followed by the raw node
/// array and name pool, Brotli-compressed. Loading is a straight memory copy, no parsing.
/// Version 2 adds the results of a duplicate search after the name pool; a snapshot without
/// one is still written as version 1, so older Emma Space releases can open it.
/// </summary>
public static class SnapshotFile
{
    public const string Extension = ".emmasnap";

    /// <summary>How every snapshot file starts, whatever its format version (used to recognize the file type).</summary>
    public const string MagicPrefix = "EMSNAP";
    private static ReadOnlySpan<byte> Magic => "EMSNAP01"u8;
    private const int TreeVersion = 1;
    private const int DuplicatesVersion = 2;

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
    public static Task SaveAsync(FileTree tree, string path, CancellationToken ct = default) => SaveAsync(tree, path, null, ct);

    /// <summary>
    /// Saves a snapshot together with the results of a duplicate search (null for none). Copies of
    /// files removed since the search are left out, and so are groups with fewer than two copies left.
    /// </summary>
    public static async Task SaveAsync(FileTree tree, string path, DuplicateResult? duplicates, CancellationToken ct = default)
    {
        using var copy = tree.Copy();
        var saved = Prune(copy, duplicates);
        var temp = path + ".tmp";
        await using (var file = File.Create(temp))
        {
            await Task.Run(() => Save(copy, file, saved), ct);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static Task<FileTree> LoadAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var file = File.OpenRead(path);
            return Load(file);
        }, ct);

    public static Task<LoadedSnapshot> LoadSnapshotAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var file = File.OpenRead(path);
            return LoadSnapshot(file);
        }, ct);

    /// <summary>
    /// Saves synchronously, on the calling thread (which must be the one changing the tree), with the
    /// results of a duplicate search if <paramref name="duplicates"/> is not null.
    /// </summary>
    public static void Save(FileTree tree, Stream output, DuplicateResult? duplicates = null) =>
        Write(tree.Info, tree.Nodes, tree.Names, Prune(tree, duplicates), output);

    /// <summary>
    /// The groups as they stand in <paramref name="tree"/> now: only live files of the group's size,
    /// each in one group at most, and only groups that still have two or more copies.
    /// </summary>
    private static DuplicateResult? Prune(FileTree tree, DuplicateResult? duplicates)
    {
        if (duplicates is null) return null;
        var nodes = tree.Nodes;
        var seen = new HashSet<int>();
        var groups = new List<DuplicateGroup>(duplicates.Groups.Count);
        foreach (var group in duplicates.Groups)
        {
            var members = new List<int>(group.Nodes.Count);
            foreach (var node in group.Nodes)
            {
                if (node > FileTree.Root && node < nodes.Length && IsLiveFileOfSize(nodes[node], group.FileSize) && seen.Add(node))
                    members.Add(node);
            }
            if (members.Count > 1) groups.Add(new DuplicateGroup(group.FileSize, members));
        }
        return new DuplicateResult(duplicates.MinSize, groups);
    }

    /// <summary>A copy the duplicate search would compare: a live file of the group's size that is not a link, cloud placeholder or unreadable.</summary>
    private static bool IsLiveFileOfSize(in EsNode node, ulong size) =>
        !node.IsDirectory && !node.IsDeleted && node.Size == size && (node.Flags & DuplicateFinder.SkippedFlags) == 0;

    private static void Write(ScanInfo info, ReadOnlySpan<EsNode> nodes, ReadOnlySpan<char> names, DuplicateResult? duplicates,
        Stream output)
    {
        var section = duplicates is null ? null : DuplicatesSection(duplicates);
        var header = Header(section is null ? TreeVersion : DuplicatesVersion, info.RootPath, info.ScannedAt.ToUnixTimeMilliseconds(),
            (long)info.Elapsed.TotalMilliseconds, (int)info.Mode, (int)info.Source, info.Errors, nodes.Length, names.Length);
        output.Write(header);
        if (section is not null)
        {
            // Version 2: a checksum of the header and everything compressed below. Brotli has none of its own,
            // and a flipped bit could otherwise turn a saved copy into another file of the same size.
            Span<byte> checksum = stackalloc byte[16];
            BinaryPrimitives.WriteUInt128LittleEndian(checksum, Checksum(header, nodes, names, section));
            output.Write(checksum);
        }

        using var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true);
        brotli.Write(MemoryMarshal.AsBytes(nodes));
        brotli.Write(MemoryMarshal.AsBytes(names));
        if (section is not null) brotli.Write(section);
    }

    /// <summary>Version 2's results: minSize u64, groupCount i32, then per group fileSize u64, count i32, count × node u32.</summary>
    private static byte[] DuplicatesSection(DuplicateResult duplicates)
    {
        using var buffer = new MemoryStream();
        using (var section = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            section.Write(duplicates.MinSize);
            section.Write(duplicates.Groups.Count);
            foreach (var group in duplicates.Groups)
            {
                section.Write(group.FileSize);
                section.Write(group.Nodes.Count);
                foreach (var node in group.Nodes) section.Write((uint)node);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// The header up to the version 2 checksum, as written. The loader builds it again from the fields it read,
    /// so the checksum covers what was actually loaded (a damaged root path re-encodes differently).
    /// </summary>
    private static byte[] Header(int version, string rootPath, long scannedAtMs, long elapsedMs, int mode, int source, int errors,
        int nodeCount, int namesLength)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(version);
            writer.Write(rootPath);
            writer.Write(scannedAtMs);
            writer.Write(elapsedMs);
            writer.Write(mode);
            writer.Write(source);
            writer.Write(errors);
            writer.Write(nodeCount);
            writer.Write(namesLength);
            writer.Write(Marshal.SizeOf<EsNode>());
        }
        return buffer.ToArray();
    }

    private static UInt128 Checksum(ReadOnlySpan<byte> header, ReadOnlySpan<EsNode> nodes, ReadOnlySpan<char> names, ReadOnlySpan<byte> section)
    {
        var hasher = new XxHash128();
        hasher.Append(header);
        hasher.Append(MemoryMarshal.AsBytes(nodes));
        hasher.Append(MemoryMarshal.AsBytes(names));
        hasher.Append(section);
        return hasher.GetCurrentHashAsUInt128();
    }

    /// <summary>
    /// Whether the file starts the way every snapshot does (<see cref="MagicPrefix"/>, any format version),
    /// as the Linux MIME type recognizes a snapshot whose name lacks the extension. Reads 6 bytes; false
    /// for anything unreadable. Pipes and devices report no length and are never opened, since opening
    /// a pipe would wait for a writer.
    /// </summary>
    public static bool StartsLikeSnapshot(string path)
    {
        try
        {
            if (new FileInfo(path).Length < MagicPrefix.Length) return false;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            Span<byte> head = stackalloc byte[MagicPrefix.Length];
            return input.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                && head.SequenceEqual(Magic[..MagicPrefix.Length]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
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
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() is not (TreeVersion or DuplicatesVersion))
                return null;
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

    /// <summary>Loads the tree only; duplicate search results saved with it are checked but not returned.</summary>
    public static FileTree Load(Stream input) => LoadSnapshot(input).Tree;

    public static LoadedSnapshot LoadSnapshot(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        Span<byte> magic = stackalloc byte[8];
        input.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("Not an Emma Space snapshot.");
        var version = reader.ReadInt32();
        if (version is not (TreeVersion or DuplicatesVersion))
            throw new InvalidDataException($"Unsupported snapshot version {version}. It may have been saved by a newer Emma Space.");

        var rootPath = ReadRootPath(reader);
        var scannedAtMs = reader.ReadInt64();
        var elapsedMs = reader.ReadInt64();
        if (!UnixTime.IsValid(scannedAtMs) || elapsedMs < 0 || elapsedMs > (long)TimeSpan.MaxValue.TotalMilliseconds)
            throw new InvalidDataException("Snapshot header has an invalid date or duration.");
        var scannedAt = DateTimeOffset.FromUnixTimeMilliseconds(scannedAtMs).ToLocalTime();
        var elapsed = TimeSpan.FromMilliseconds(elapsedMs);
        var modeValue = reader.ReadInt32();
        var mode = (ScanMode)modeValue;
        var source = reader.ReadInt32(); // original scan source, informational (only checksummed)
        var errors = reader.ReadInt32();
        var nodeCount = reader.ReadInt32();
        var namesLength = reader.ReadInt32();
        var nodeSize = reader.ReadInt32();
        if (nodeSize != Marshal.SizeOf<EsNode>() || nodeCount <= 0 || nodeCount > MaxNodes || namesLength < 0 ||
            namesLength > (long)(nodeCount - 1) * MaxNameCharsPerNode + MaxRootNameChars)
            throw new InvalidDataException("Snapshot header is corrupt.");
        var checksum = UInt128.Zero;
        if (version == DuplicatesVersion)
        {
            var lower = reader.ReadUInt64();
            checksum = new UInt128(reader.ReadUInt64(), lower);
        }

        EsNode[] nodes;
        char[] names;
        byte[]? section = null;
        try
        {
            using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
            nodes = ReadArray<EsNode>(brotli, nodeCount);
            names = ReadArray<char>(brotli, namesLength);
            if (version == DuplicatesVersion) section = ReadToEnd(brotli, MaxSectionBytes(nodeCount));
        }
        catch (InvalidOperationException)
        {
            // How BrotliStream reports data it can't decode.
            throw new InvalidDataException("Snapshot data is damaged.");
        }

        DuplicateResult? duplicates = null;
        if (section is not null)
        {
            var header = Header(version, rootPath, scannedAtMs, elapsedMs, modeValue, source, errors, nodeCount, namesLength);
            if (Checksum(header, nodes, names, section) != checksum) throw new InvalidDataException("Snapshot data is damaged.");
            duplicates = ReadDuplicates(section, nodes.Length);
        }
        Validate(nodes, names);
        if (duplicates is not null) ValidateDuplicates(duplicates, nodes);
        var info = new ScanInfo(rootPath, scannedAt, elapsed, mode, ScanSource.Snapshot, errors);
        return new LoadedSnapshot(new FileTree(new ArrayNodeStorage(nodes, names), info), duplicates);
    }

    // The largest duplicates section possible for this many nodes: its two counts, then at most one group per
    // two nodes (12 bytes each) and one 4-byte entry per node, since no node is in two groups.
    private static long MaxSectionBytes(int nodeCount) => 12 + 6L * nodeCount + 4L * nodeCount;

    /// <summary>
    /// The rest of the stream, at most <paramref name="limit"/> bytes, in a buffer that grows only as data
    /// arrives, so a damaged count cannot make it allocate more than the file holds.
    /// </summary>
    private static byte[] ReadToEnd(Stream stream, long limit)
    {
        var buffer = new byte[(int)Math.Min(limit + 1, 64 * 1024)];
        var filled = 0;
        while (true)
        {
            if (filled == buffer.Length)
            {
                if (filled > limit) throw new InvalidDataException("Snapshot has unexpected data at the end.");
                Array.Resize(ref buffer, (int)Math.Min(limit + 1, (long)buffer.Length * 2));
            }
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0) break;
            filled += read;
        }
        if (filled > limit) throw new InvalidDataException("Snapshot has unexpected data at the end.");
        return buffer[..filled];
    }

    /// <summary>
    /// Parses the duplicates section, with every count checked against the bytes actually there before
    /// anything is allocated for it.
    /// </summary>
    private static DuplicateResult ReadDuplicates(ReadOnlySpan<byte> data, int nodeCount)
    {
        var corrupt = new InvalidDataException("Snapshot duplicate results are corrupt.");
        if (data.Length < 12) throw corrupt;
        var minSize = BinaryPrimitives.ReadUInt64LittleEndian(data);
        var groupCount = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        data = data[12..];
        // Every group takes at least 12 + 2 × 4 bytes; no node can be in two groups.
        if (groupCount < 0 || groupCount > nodeCount / 2 || groupCount > data.Length / 20) throw corrupt;
        var groups = new List<DuplicateGroup>(groupCount);
        for (var g = 0; g < groupCount; g++)
        {
            if (data.Length < 12) throw corrupt;
            var fileSize = BinaryPrimitives.ReadUInt64LittleEndian(data);
            var count = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
            data = data[12..];
            if (count < 2 || count > nodeCount || count > data.Length / 4) throw corrupt;
            var members = new int[count];
            for (var i = 0; i < count; i++)
            {
                var node = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
                if (node >= (uint)nodeCount) throw corrupt;
                members[i] = (int)node;
            }
            data = data[(count * 4)..];
            groups.Add(new DuplicateGroup(fileSize, members));
        }
        if (!data.IsEmpty) throw new InvalidDataException("Snapshot has unexpected data at the end.");
        return new DuplicateResult(minSize, groups);
    }

    // Each copy must be a live file of its group's size, in one group only, and no smaller than the
    // search looked at: file actions (move to Trash) work on these nodes.
    private static void ValidateDuplicates(DuplicateResult duplicates, EsNode[] nodes)
    {
        var seen = new bool[nodes.Length];
        var smallest = Math.Max(1, duplicates.MinSize);
        foreach (var group in duplicates.Groups)
        {
            if (group.FileSize < smallest) throw new InvalidDataException("Snapshot duplicate results are corrupt.");
            foreach (var node in group.Nodes)
            {
                if (node == FileTree.Root || seen[node] || !IsLiveFileOfSize(nodes[node], group.FileSize))
                    throw new InvalidDataException("Snapshot duplicate results do not match its files.");
                seen[node] = true;
            }
        }
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
