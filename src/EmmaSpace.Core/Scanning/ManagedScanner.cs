using System.Diagnostics;
using System.IO.Enumeration;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Scanning;

/// <summary>
/// Pure .NET scanner. Used where the native engine is unavailable or unsupported, and to
/// cross-check the native engine in tests. Allocation size is estimated from 4 KiB clusters.
/// </summary>
public sealed class ManagedScanner : IScanner
{
    private const ulong ClusterSize = 4096;
    // FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    private const uint CloudAttributes = 0x1000 | 0x40000 | 0x400000;
    private const uint RecallOnDataAccess = 0x400000;

    public string Name => "Managed";

    public Task<FileTree> ScanAsync(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return Task.Factory.StartNew(() => Scan(rootPath, options, progress, cancellationToken), cancellationToken,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private readonly record struct Entry(string Name, bool IsDirectory, bool IsReparse, ulong Length, long MTime,
        long ATime, uint Attributes);

    private static FileTree Scan(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var root = NormalizeRoot(rootPath);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists)
        {
            if (File.Exists(root)) throw new ScanException(EsStatus.InvalidArgument, NativeScanner.Describe(EsStatus.InvalidArgument, root));
            throw new ScanException(EsStatus.NotFound, NativeScanner.Describe(EsStatus.NotFound, root));
        }

        var builder = new TreeBuilder();
        builder.AddRoot(root, new DateTimeOffset(rootInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds(), (uint)rootInfo.Attributes);

        var enumeration = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
            BufferSize = 64 * 1024,
        };

        var stack = new Stack<(int Node, string Path)>();
        // Same rule as for child folders: a virtualized cloud root is recorded but never listed.
        if (((uint)rootInfo.Attributes & RecallOnDataAccess) != 0)
            builder.AddFlags(FileTree.Root, NodeFlags.Cloud);
        else
            stack.Push((FileTree.Root, root));
        ulong files = 0, dirs = 0, bytes = 0;
        uint errors = 0;
        var lastReport = 0L;
        var entries = new List<Entry>();

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (node, path) = stack.Pop();
            entries.Clear();
            try
            {
                var enumerable = new FileSystemEnumerable<Entry>(path, static (ref FileSystemEntry e) => new Entry(
                    e.FileName.ToString(),
                    e.IsDirectory,
                    (e.Attributes & FileAttributes.ReparsePoint) != 0,
                    e.IsDirectory ? 0UL : (ulong)e.Length,
                    e.LastWriteTimeUtc.ToUnixTimeMilliseconds(),
                    e.LastAccessTimeUtc.ToUnixTimeMilliseconds(),
                    (uint)e.Attributes), enumeration);
                foreach (var entry in enumerable) entries.Add(entry);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                builder.MarkError(node);
                errors++;
            }

            foreach (var e in entries)
            {
                if (e.IsDirectory)
                {
                    var childPath = Path.Join(path, e.Name);
                    var link = e.IsReparse && !options.FollowReparsePoints && IsLink(new DirectoryInfo(childPath));
                    // Virtualized cloud folder: listing it would fetch it from the network; nothing is stored locally.
                    var cloud = (e.Attributes & RecallOnDataAccess) != 0;
                    var skip = link || cloud;
                    var flags = NodeFlags.Directory | (link ? NodeFlags.Reparse : NodeFlags.None) | (cloud ? NodeFlags.Cloud : NodeFlags.None);
                    var child = builder.AddChild(node, e.Name, flags, mtime: e.MTime, atime: e.ATime, attributes: e.Attributes);
                    dirs++;
                    if (!skip) stack.Push((child, childPath));
                }
                else
                {
                    var length = e.Length;
                    // File symlinks keep the Reparse flag so nothing (e.g. duplicate hashing) follows them.
                    var flags = e.IsReparse && IsLink(new FileInfo(Path.Join(path, e.Name))) ? NodeFlags.Reparse : NodeFlags.None;
                    if ((e.Attributes & CloudAttributes) != 0)
                    {
                        // Online-only data takes no local space; the managed API cannot see partial hydration.
                        length = 0;
                        flags |= NodeFlags.Cloud;
                    }
                    var allocated = (length + ClusterSize - 1) / ClusterSize * ClusterSize;
                    builder.AddChild(node, e.Name, flags, length, allocated, e.MTime, e.ATime, e.Attributes);
                    files++;
                    bytes += length;
                }
            }

            if (progress is not null && stopwatch.ElapsedMilliseconds - lastReport >= 100)
            {
                lastReport = stopwatch.ElapsedMilliseconds;
                progress.Report(new ScanProgress(files, dirs, bytes, errors, ScanPhase.Enumerating, path));
            }
        }

        progress?.Report(new ScanProgress(files, dirs, bytes, errors, ScanPhase.Finalizing, root));
        var info = new ScanInfo(root, startedAt, stopwatch.Elapsed, ScanMode.Walker, ScanSource.Managed, (int)errors);
        var tree = builder.Build(info);
        progress?.Report(new ScanProgress(files, dirs, bytes, errors, ScanPhase.Done, root));
        return tree;
    }

    // Junctions and symlinks have a link target; other reparse points (e.g. OneDrive
    // placeholders) do not and contain real data, so they are treated like the native engine does.
    private static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // unreadable: treat as a link and don't descend
        }
    }

    /// <summary>
    /// The absolute folder to scan, as both scanners use it: relative paths are resolved against
    /// the current folder, a bare drive ("C:") means its root ("C:\"), and trailing separators are
    /// dropped except on a root. Invalid input becomes a <see cref="ScanException"/>.
    /// </summary>
    internal static string NormalizeRoot(string path)
    {
        var input = path.Trim();
        if (OperatingSystem.IsWindows() && input.Length == 2 && input[1] == ':' && char.IsAsciiLetter(input[0]))
            input += Path.DirectorySeparatorChar; // "C:" alone would mean "the current folder on C:"
        string full;
        try
        {
            full = Path.GetFullPath(input);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ScanException(EsStatus.InvalidArgument, $"'{path}' is not a valid folder path.");
        }
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.Ordinal) ? full : Path.TrimEndingDirectorySeparator(full);
    }
}
