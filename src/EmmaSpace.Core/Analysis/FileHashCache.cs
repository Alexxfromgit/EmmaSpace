using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EmmaSpace.Core.Platform;

namespace EmmaSpace.Core.Analysis;

/// <summary>
/// Remembers the content hashes of files the duplicate search has read, so searching again skips
/// files that have not changed. An entry is only used for a file whose stamp, read from the open file
/// at that moment, equals the one it was hashed with in every field: volume, file ID, size, last-write
/// time, change time and (Windows) change-journal USN. Only trusted stamps are remembered: ones that
/// every change of the content moves on and no program can move back (see <see cref="FileStamp"/>),
/// which tools that restore a file's times (VeraCrypt, tag editors) can't fool.
/// A file is only remembered once its last change is a minute old when its stamp is taken: file system
/// clocks tick in steps of up to 2 s, and on Linux writes through a memory map update the times only
/// when dirty pages are written back (within about 30 s).
/// Settings text: <c>SettingsViewModel.RememberHashesHint</c>.
/// Saved as a small Brotli-compressed file of fixed-size records, keyed by a hash of the full path
/// (paths themselves are not stored). A missing, damaged or foreign file just means an empty cache.
/// </summary>
public sealed class FileHashCache
{
    /// <summary>Most entries kept, on disk and in memory; the ones used longest ago go first.</summary>
    public const int MaxEntries = 250_000; // about 25 MB on disk

    private static ReadOnlySpan<byte> Magic => "EMHASH01"u8;
    private const int FormatVersion = 3;
    private const long HasSample = 1, HasFull = 2;

    private readonly ConcurrentDictionary<UInt128, Entry> _entries = new();
    private readonly Lock _saveLock = new();
    private readonly string? _path;
    private int _generation;

    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public UInt128 Key;
        public UInt128 Sample;
        public UInt128 Full;
        public UInt128 FileId;
        public ulong Volume;
        public ulong Size;
        public long WriteTimeNs;
        public long ChangeTimeNs;
        public ulong Usn;
        public long LastUsed; // Unix seconds
        public long Flags;

        public readonly bool Matches(in FileStamp s) =>
            s.Trusted && Volume == s.Volume && FileId == s.FileId && Size == s.Size && WriteTimeNs == s.WriteTimeNs &&
            ChangeTimeNs == s.ChangeTimeNs && Usn == s.Usn;
    }

    /// <summary>A cache kept in memory only, or saved to <paramref name="path"/> by <see cref="Save"/>.</summary>
    public FileHashCache(string? path = null) => _path = path;

    /// <summary>Smaller files are not remembered: reading them costs about as much as checking them.</summary>
    internal ulong MinCachedSize { get; init; } = 1024 * 1024;

    /// <summary>How old a file's last change must be, when its stamp is taken, before its hash is remembered (tests set it to zero).</summary>
    internal TimeSpan SettleTime { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The clock <see cref="Set"/> compares a stamp's times with: nanoseconds since the Unix epoch.</summary>
    public static long NowNs() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;

    // Up to this many entries in memory between saves; each save keeps the MaxEntries used most recently.
    private const int MaxEntriesBetweenSaves = MaxEntries + MaxEntries / 4;

    public int Count => _entries.Count;

    /// <summary>How many lookups found a usable hash (for tests).</summary>
    internal int Hits => Volatile.Read(ref _hits);
    private int _hits;

    /// <summary>
    /// Changes with every <see cref="Clear"/>. A search takes it when it starts and passes it to
    /// <see cref="Set"/>, so hashes from a search that was running during a Clear are not remembered.
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Loads the cache saved at <paramref name="path"/>; empty if there is none or it can't be read.</summary>
    public static FileHashCache Load(string path)
    {
        var cache = new FileHashCache(path);
        try
        {
            using var file = File.OpenRead(path);
            cache.Read(file);
        }
        // Brotli reports undecodable data as InvalidOperationException.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            cache._entries.Clear();
        }
        return cache;
    }

    /// <summary>
    /// The cached hash of the file at <paramref name="path"/> if it was taken of a file with exactly this
    /// stamp (<paramref name="full"/>: of the whole file, otherwise of its head and tail).
    /// </summary>
    public bool TryGet(string path, in FileStamp stamp, bool full, out UInt128 hash)
    {
        var key = KeyOf(path);
        if (_entries.TryGetValue(key, out var e) && e.Matches(stamp) && (e.Flags & (full ? HasFull : HasSample)) != 0)
        {
            hash = full ? e.Full : e.Sample;
            Interlocked.Increment(ref _hits);
            var used = e;
            used.LastUsed = Now();
            _entries.TryUpdate(key, used, e); // only if still there: never brings back a cleared entry
            return true;
        }
        hash = default;
        return false;
    }

    /// <summary>
    /// Records a hash taken of the file at <paramref name="path"/> while it had <paramref name="stamp"/> (read at
    /// <paramref name="stampedAtNs"/>, from <see cref="NowNs"/>), unless the stamp isn't trusted, the file is too
    /// small or had changed too recently, the cache is full, or it was cleared since <paramref name="generation"/>
    /// (from <see cref="Generation"/> when the search started).
    /// </summary>
    public void Set(string path, in FileStamp stamp, long stampedAtNs, bool full, UInt128 hash, int generation)
    {
        if (generation != Generation || !CanRemember(stamp, stampedAtNs)) return;
        var key = KeyOf(path);
        if (_entries.Count >= MaxEntriesBetweenSaves && !_entries.ContainsKey(key)) return; // until a save trims it
        var fresh = new Entry
        {
            Key = key, FileId = stamp.FileId, Volume = stamp.Volume, Size = stamp.Size,
            WriteTimeNs = stamp.WriteTimeNs, ChangeTimeNs = stamp.ChangeTimeNs, Usn = stamp.Usn,
        };
        var s = stamp;
        var added = _entries.AddOrUpdate(key, _ => Fill(fresh, full, hash), (_, e) => Fill(e.Matches(s) ? e : fresh, full, hash));
        // A Clear that ran meanwhile may have missed this entry (it adds after clearing): take it out again.
        if (generation != Generation) _entries.TryRemove(new KeyValuePair<UInt128, Entry>(key, added));
    }

    private bool CanRemember(in FileStamp stamp, long stampedAtNs)
    {
        if (!stamp.Trusted || stamp.Size < MinCachedSize || stamp.WriteTimeNs == 0 || stamp.ChangeTimeNs == 0) return false;
        var lastChange = Math.Max(stamp.WriteTimeNs, stamp.ChangeTimeNs);
        return stampedAtNs - lastChange >= SettleTime.Ticks * 100;
    }

    private static Entry Fill(Entry e, bool full, UInt128 hash)
    {
        if (full)
        {
            e.Full = hash;
            e.Flags |= HasFull;
        }
        else
        {
            e.Sample = hash;
            e.Flags |= HasSample;
        }
        e.LastUsed = Now();
        return e;
    }

    /// <summary>
    /// Forgets every entry, including what a search still running would add, and deletes the saved file.
    /// The generation moves on before the entries go, so <see cref="Set"/> can tell an entry it added too late.
    /// </summary>
    public void Clear()
    {
        lock (_saveLock)
        {
            Interlocked.Increment(ref _generation);
            _entries.Clear();
            if (_path is null) return;
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Writes the cache to its file (through a temporary file of its own, so a crash or a second Emma Space
    /// window never leaves half a cache) and drops what didn't fit from memory. Failing to save is not an
    /// error: the next search just reads the files again.
    /// </summary>
    public void Save()
    {
        if (_path is null) return;
        lock (_saveLock)
        {
            var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var file = File.Create(temp))
                {
                    Write(file);
                }
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>The entries to keep: all of them, or the <see cref="MaxEntries"/> used most recently (the rest leave memory too).</summary>
    private Entry[] Kept()
    {
        var entries = _entries.Values.ToArray();
        if (entries.Length <= MaxEntries) return entries;
        entries.AsSpan().Sort((a, b) => b.LastUsed.CompareTo(a.LastUsed));
        var kept = entries[..MaxEntries];
        foreach (var dropped in entries.AsSpan(MaxEntries)) _entries.TryRemove(dropped.Key, out _);
        return kept;
    }

    internal void Write(Stream output)
    {
        var entries = Kept();
        var bytes = MemoryMarshal.AsBytes(entries.AsSpan());
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(DuplicateFinder.SampleSize); // sample hashes depend on it
            writer.Write(Unsafe.SizeOf<Entry>());
            writer.Write(entries.Length);
            // Detects a damaged file before any entry is trusted.
            var checksum = XxHash128.HashToUInt128(bytes);
            writer.Write((ulong)checksum);
            writer.Write((ulong)(checksum >> 64));
        }
        using var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true);
        brotli.Write(bytes);
    }

    internal void Read(Stream input)
    {
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion ||
            reader.ReadInt32() != DuplicateFinder.SampleSize || reader.ReadInt32() != Unsafe.SizeOf<Entry>())
            throw new InvalidDataException("Not a hash cache of this version.");
        var count = reader.ReadInt32();
        var lower = reader.ReadUInt64();
        var checksum = new UInt128(reader.ReadUInt64(), lower);
        if (count < 0 || count > MaxEntries) throw new InvalidDataException("Hash cache is corrupt.");

        var entries = new Entry[count];
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true))
        {
            brotli.ReadExactly(MemoryMarshal.AsBytes(entries.AsSpan()));
        }
        if (XxHash128.HashToUInt128(MemoryMarshal.AsBytes(entries.AsSpan())) != checksum)
            throw new InvalidDataException("Hash cache is corrupt.");
        foreach (var e in entries) _entries[e.Key] = e;
    }

    private static UInt128 KeyOf(string path) => XxHash128.HashToUInt128(MemoryMarshal.AsBytes(path.AsSpan()));

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
