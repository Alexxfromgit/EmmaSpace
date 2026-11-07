using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Scanning;

/// <summary>Scans through the C++ engine. Results stay in native memory (zero copy).</summary>
public sealed class NativeScanner : IScanner
{
    private const int PathBufferLength = 1024;
    private static readonly TimeSpan s_progressInterval = TimeSpan.FromMilliseconds(100);

    public string Name => "Native";

    public Task<FileTree> ScanAsync(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        // The engine needs an absolute path (on Windows it prefixes \\?\, which does not resolve
        // relative paths), so normalize exactly like the managed scanner.
        var root = ManagedScanner.NormalizeRoot(rootPath);
        return Task.Factory.StartNew(() => Scan(root, options, progress, cancellationToken), cancellationToken,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static unsafe FileTree Scan(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.Now;
        ScanHandle handle;
        fixed (char* root = rootPath)
        {
            var native = new EsScanOptions
            {
                StructSize = (uint)sizeof(EsScanOptions),
                Mode = (uint)options.Mode,
                ThreadCount = (uint)Math.Max(0, options.ThreadCount),
                Flags = options.FollowReparsePoints ? NativeMethods.FollowReparse : 0,
                RootPath = root,
            };
            var started = NativeMethods.ScanStart(&native, out handle);
            if (started != EsStatus.Ok)
            {
                handle.Dispose();
                throw new ScanException(started, Describe(started, rootPath));
            }
        }

        var keep = false;
        try
        {
            using var registration = ct.Register(() => NativeMethods.ScanCancel(handle));
            var pathBuffer = new char[PathBufferLength];
            EsStatus status;
            while ((status = NativeMethods.ScanWait(handle, (uint)s_progressInterval.TotalMilliseconds)) == EsStatus.Timeout)
            {
                if (progress is not null) Report(handle, pathBuffer, progress);
            }

            if (status == EsStatus.Cancelled) throw new OperationCanceledException(ct);
            if (status != EsStatus.Ok) throw new ScanException(status, Describe(status, rootPath));
            if (progress is not null) Report(handle, pathBuffer, progress);

            EsResult result;
            status = NativeMethods.ScanGetResult(handle, &result);
            if (status != EsStatus.Ok) throw new ScanException(status, Describe(status, rootPath));

            var info = new ScanInfo(rootPath, startedAt, TimeSpan.FromMilliseconds(result.ElapsedMs),
                (ScanMode)result.Mode, ScanSource.Native, (int)result.Errors);
            var tree = new FileTree(new NativeNodeStorage(handle, result), info);
            keep = true;
            return tree;
        }
        finally
        {
            if (!keep) handle.Dispose();
        }
    }

    private static unsafe void Report(ScanHandle handle, char[] buffer, IProgress<ScanProgress> progress)
    {
        EsProgress p;
        fixed (char* b = buffer)
        {
            if (NativeMethods.ScanGetProgress(handle, &p, b, (uint)buffer.Length) != EsStatus.Ok) return;
        }
        progress.Report(new ScanProgress(p.Files, p.Directories, p.Bytes, p.Errors, (ScanPhase)p.Phase,
            new string(buffer, 0, (int)p.PathLength)));
    }

    internal static string Describe(EsStatus status, string path) => status switch
    {
        EsStatus.NotFound => $"'{path}' was not found.",
        EsStatus.AccessDenied => $"Access to '{path}' was denied.",
        EsStatus.InvalidArgument => $"'{path}' is not a folder that can be scanned.",
        EsStatus.NotSupported => "This scan mode is not supported on this system.",
        EsStatus.OutOfMemory => "The scan ran out of memory.",
        _ => $"The scan failed ({status}).",
    };
}
