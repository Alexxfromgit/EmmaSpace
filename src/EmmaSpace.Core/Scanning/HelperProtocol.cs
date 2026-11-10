using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EmmaSpace.Core.Interop;

namespace EmmaSpace.Core.Scanning;

/// <summary>What <c>emmascan-helper</c> reported at the end of its scan.</summary>
internal sealed record HelperResult(EsStatus Status, ScanMode Mode, uint Errors, ulong ElapsedMs, EsNode[] Nodes, char[] Names);

/// <summary>
/// Reads the output of <c>emmascan-helper</c> (native/emmascan/tools/emmascan_helper.cpp): a header,
/// progress frames, then one result frame with the raw node array and name pool. Little-endian.
/// </summary>
internal static class HelperProtocol
{
    public const uint Version = 1;
    private const byte FrameProgress = 1;
    private const byte FrameResult = 2;
    private const int MaxPathLength = 1 << 16;
    private const int ChunkBytes = 4 << 20; // a multiple of both element sizes

    /// <exception cref="InvalidDataException">The stream is not helper output, or it ended early.</exception>
    public static async Task<HelperResult> ReadAsync(Stream stream, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("The helper protocol is little-endian.");
        var header = new byte[12];
        await Fill(stream, header, ct);
        if (header[0] != 'E' || header[1] != 'M' || header[2] != 'S' || header[3] != 'H')
            throw new InvalidDataException("The fast scan helper sent something unexpected.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var nodeSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (version != Version || nodeSize != (uint)Marshal.SizeOf<EsNode>())
            throw new InvalidDataException($"The fast scan helper speaks protocol {version} (node size {nodeSize}); expected {Version}.");
        // The helper writes its header first thing, so it runs as root now: the password (if any) was accepted.
        progress?.Report(new ScanProgress(0, 0, 0, 0, ScanPhase.Starting, ""));

        var type = new byte[1];
        var progressFields = new byte[40];
        var resultFields = new byte[32];
        while (true)
        {
            await Fill(stream, type, ct);
            switch (type[0])
            {
                case FrameProgress:
                {
                    await Fill(stream, progressFields, ct);
                    var files = BinaryPrimitives.ReadUInt64LittleEndian(progressFields);
                    var directories = BinaryPrimitives.ReadUInt64LittleEndian(progressFields.AsSpan(8));
                    var bytes = BinaryPrimitives.ReadUInt64LittleEndian(progressFields.AsSpan(16));
                    var errors = BinaryPrimitives.ReadUInt32LittleEndian(progressFields.AsSpan(24));
                    var phase = (ScanPhase)BinaryPrimitives.ReadUInt32LittleEndian(progressFields.AsSpan(28));
                    var pathLength = BinaryPrimitives.ReadUInt32LittleEndian(progressFields.AsSpan(36));
                    if (pathLength > MaxPathLength) throw new InvalidDataException("A progress frame is too long.");
                    var path = new byte[pathLength * 2];
                    await Fill(stream, path, ct);
                    progress?.Report(new ScanProgress(files, directories, bytes, errors, phase,
                        new string(MemoryMarshal.Cast<byte, char>(path))));
                    break;
                }
                case FrameResult:
                {
                    await Fill(stream, resultFields, ct);
                    var f = resultFields.AsSpan();
                    var status = (EsStatus)BinaryPrimitives.ReadUInt32LittleEndian(f);
                    var mode = (ScanMode)BinaryPrimitives.ReadUInt32LittleEndian(f[4..]);
                    var errors = BinaryPrimitives.ReadUInt32LittleEndian(f[8..]);
                    var elapsed = BinaryPrimitives.ReadUInt64LittleEndian(f[12..]);
                    var nodeCount = BinaryPrimitives.ReadUInt32LittleEndian(f[20..]);
                    var namesLength = BinaryPrimitives.ReadUInt64LittleEndian(f[24..]);
                    if (nodeCount > Array.MaxLength || namesLength > (ulong)Array.MaxLength)
                        throw new InvalidDataException("The scan result is too large.");
                    if (status == EsStatus.Ok && nodeCount == 0) throw new InvalidDataException("The scan result has no root.");

                    var nodes = new EsNode[nodeCount];
                    await FillArray(stream, nodes, ct);
                    var names = new char[namesLength];
                    await FillArray(stream, names, ct);
                    if (status == EsStatus.Ok) Validate(nodes, names);
                    return new HelperResult(status, mode, errors, elapsed, nodes, names);
                }
                default:
                    throw new InvalidDataException($"The fast scan helper sent an unknown frame ({type[0]}).");
            }
        }
    }

    /// <summary>
    /// The result must be a tree: every node but the root reached exactly once from its parent's child list,
    /// children after their parent, names inside the pool. Then a damaged stream cannot crash or hang the views.
    /// </summary>
    internal static void Validate(ReadOnlySpan<EsNode> nodes, ReadOnlySpan<char> names)
    {
        var count = (uint)nodes.Length;
        if (count == 0 || nodes[0].Parent != EsNode.None) throw Damaged(0);
        var reached = new bool[count];
        for (var i = 0u; i < count; i++)
        {
            ref readonly var n = ref nodes[(int)i];
            if ((ulong)n.NameOffset + n.NameLength > (ulong)names.Length) throw Damaged(i);
            // Paths are built by joining names, so below the root each must be one component: never empty, "." or
            // "..", and without "/" or NUL; otherwise opening or trashing it could reach outside the scanned folder.
            if (i > 0)
            {
                var name = names.Slice((int)n.NameOffset, n.NameLength);
                if (name.IsEmpty || name is "." or ".." || name.ContainsAny('/', '\0')) throw Damaged(i);
            }
            if (i > 0 && (n.Parent >= i || !reached[i])) throw Damaged(i);
            for (var c = n.FirstChild; c != EsNode.None; c = nodes[(int)c].NextSibling)
            {
                if (c >= count || c <= i || reached[c] || nodes[(int)c].Parent != i) throw Damaged(c);
                reached[c] = true;
            }
        }
    }

    private static InvalidDataException Damaged(uint node) => new($"The scan result is damaged (node {node}).");

    private static async Task Fill(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, ct);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The fast scan helper stopped before sending its result.");
        }
    }

    // Reads array.Length elements straight into the array, a few MiB at a time.
    private static async Task FillArray<T>(Stream stream, T[] array, CancellationToken ct) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var total = (long)array.Length * size;
        var buffer = new byte[(int)Math.Min(ChunkBytes, total)];
        for (long done = 0; done < total;)
        {
            var n = (int)Math.Min(buffer.Length, total - done);
            await Fill(stream, buffer.AsMemory(0, n), ct);
            buffer.AsSpan(0, n).CopyTo(MemoryMarshal.AsBytes(array.AsSpan((int)(done / size), n / size)));
            done += n;
        }
    }
}
