using System.Diagnostics;
using System.Text;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Scanning;

/// <summary>
/// Linux fast scan: runs <c>emmascan-helper</c> as root through pkexec, so whole ext2/3/4 file systems are read
/// straight from the block device, while the app itself keeps running as the user. Anything else the
/// helper walks folder by folder (as root). The result arrives over a pipe (see <see cref="HelperProtocol"/>).
/// </summary>
public sealed class HelperScanner : IScanner
{
    public const string HelperFileName = "emmascan-helper";

    private readonly Func<string, string, ProcessStartInfo> _startInfo;

    /// <param name="startInfo">Builds the command for (helper path, folder); by default pkexec. Tests run the helper directly.</param>
    public HelperScanner(Func<string, string, ProcessStartInfo>? startInfo = null) =>
        _startInfo = startInfo ?? PkexecStartInfo;

    public string Name => "Native (root helper)";

    /// <summary>The helper shipped next to the app, if any.</summary>
    public static string? BundledHelperPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, HelperFileName);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>True on Linux when the helper is shipped and pkexec (polkit) can start it.</summary>
    public static bool IsAvailable => OperatingSystem.IsLinux() && BundledHelperPath is not null && Pkexec is not null;

    /// <summary>
    /// pkexec from a system folder only, never looked up on PATH: a user-writable folder early on PATH (~/.local/bin)
    /// could otherwise put a fake pkexec in front of the password prompt.
    /// </summary>
    internal static string? Pkexec =>
        new[] { "/usr/bin/pkexec", "/bin/pkexec" }.FirstOrDefault(File.Exists);

    public async Task<FileTree> ScanAsync(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        cancellationToken.ThrowIfCancellationRequested();
        var root = ManagedScanner.NormalizeRoot(rootPath);
        var helper = await Task.Run(PrepareHelper, cancellationToken) ??
                     throw new ScanException(EsStatus.NotSupported, "The fast scan helper is missing from this installation.");

        var startedAt = DateTimeOffset.Now;
        var start = _startInfo(helper, root);
        start.UseShellExecute = false;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardErrorEncoding = Encoding.UTF8;

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ScanException(EsStatus.NotSupported, $"Could not start the fast scan ({ex.Message}).");
        }

        // stderr carries pkexec's and the helper's messages; keep the end of it for the error dialog.
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = errors.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // if we stop waiting for it

        // Any byte on stdin cancels the helper: once it runs as root, the user cannot kill it. While pkexec still waits
        // for the password it runs with the user's real uid and can be killed, which also closes the password dialog.
        // The callback runs on the thread that cancels (the UI), so nothing may escape it, and the write must not race
        // with closing stdin below.
        var stdinLock = new object();
        var stdinClosed = false;
        await using var registration = cancellationToken.Register(() =>
        {
            lock (stdinLock)
            {
                if (!stdinClosed)
                {
                    try
                    {
                        process.StandardInput.Write('c');
                        process.StandardInput.Flush();
                    }
                    catch (Exception)
                    {
                        // it has finished
                    }
                }
            }
            if (!KillOnCancel) return;
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // the helper runs as root by now (EPERM), or it has finished
            }
        });

        HelperResult? result = null;
        InvalidDataException? broken = null;
        try
        {
            result = await HelperProtocol.ReadAsync(process.StandardOutput.BaseStream, progress, CancellationToken.None);
        }
        catch (InvalidDataException ex)
        {
            broken = ex;
        }
        finally
        {
            lock (stdinLock)
            {
                stdinClosed = true;
                try
                {
                    process.StandardInput.Close(); // the helper ends its scan if it is still running
                }
                catch (IOException)
                {
                    // it has finished
                }
            }
            // If reading stopped early (damaged data), the helper may be blocked writing into a full pipe, where it would
            // never see the cancel. Closing our end makes its write fail, so it exits.
            process.StandardOutput.Close();
        }

        // The helper exits right after its result, except when a part that cannot be interrupted is still running: a
        // cancelled scan may be stuck in syncfs(), and the process cannot end before that returns. So with a result, or
        // once cancelled, wait only briefly; the helper finishes on its own. Without a result the exit code says what
        // went wrong (pkexec reports there), so wait for it, unless the user cancels meanwhile.
        var briefly = result is not null || cancellationToken.IsCancellationRequested;
        var exited = await WaitForExit(process, briefly ? TimeSpan.FromSeconds(5) : Timeout.InfiniteTimeSpan,
            briefly ? CancellationToken.None : cancellationToken);
        var stderr = exited ? Tail(await ReadOrEmpty(errors)) : "";

        if (result is null)
        {
            if (!exited || cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            // pkexec: 126 = the password dialog was dismissed; 127 = not authorized, no polkit agent, or it could not run
            // the helper (e.g. a folder mounted noexec). Its stderr says which.
            if (process.ExitCode == 126) throw new OperationCanceledException(cancellationToken);
            if (process.ExitCode == 127)
                throw new ScanException(EsStatus.AccessDenied,
                    "Fast scan could not run as administrator: it was not authorized, or the system could not start it." +
                    (stderr.Length > 0 ? $"\n\n{stderr}" : ""));
            throw new ScanException(EsStatus.Internal,
                $"The fast scan helper failed ({broken?.Message ?? $"exit code {process.ExitCode}"})." +
                (stderr.Length > 0 ? $"\n\n{stderr}" : ""));
        }

        if (result.Status == EsStatus.Cancelled || cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        if (result.Status != EsStatus.Ok) throw new ScanException(result.Status, NativeScanner.Describe(result.Status, root));
        var info = new ScanInfo(root, startedAt, TimeSpan.FromMilliseconds(result.ElapsedMs), result.Mode, ScanSource.Native,
            (int)Math.Min(result.Errors, int.MaxValue));
        return new FileTree(new ArrayNodeStorage(result.Nodes, result.Names), info);
    }

    /// <summary>Also kill the process when cancelled (works while pkexec waits for the password). Tests turn it off.</summary>
    internal bool KillOnCancel { get; init; } = true;

    private static async Task<bool> WaitForExit(Process process, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (limit != Timeout.InfiniteTimeSpan) stop.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(stop.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<string> ReadOrEmpty(Task<string> text)
    {
        try
        {
            return await text.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
            return "";
        }
    }

    private static ProcessStartInfo PkexecStartInfo(string helper, string root)
    {
        var start = new ProcessStartInfo(Pkexec ?? throw new ScanException(EsStatus.NotSupported,
            "Fast scan needs pkexec (polkit) in /usr/bin or /bin to ask for administrator rights, and it is not there."));
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add("scan");
        start.ArgumentList.Add(root);
        return start;
    }

    /// <summary>
    /// Root cannot open files inside the user's AppImage mount (FUSE without allow_other), so there the helper is
    /// copied to a private folder first ($XDG_RUNTIME_DIR, mode 0700) and checked against the bundled one right before
    /// each launch. Elsewhere it runs where it is installed.
    /// <para>
    /// Trade-off: that copy belongs to the user, so malware already running as the user could swap it between the check
    /// and pkexec and get root once the password is typed (as it could with any program the user runs through sudo).
    /// A distribution package should install the helper root-owned and run it in place.
    /// </para>
    /// </summary>
    internal static string? PrepareHelper()
    {
        if (BundledHelperPath is not { } bundled) return null;
        if (!OperatingSystem.IsLinux()) return bundled;
        if (Environment.GetEnvironmentVariable("APPIMAGE") is null) return bundled;

        var baseDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        var dir = Path.Combine(baseDir, "emmaspace");
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, privateMode);
        File.SetUnixFileMode(dir, privateMode);
        var copy = Path.Combine(dir, HelperFileName);
        if (!File.Exists(copy) || !SameContent(bundled, copy))
        {
            var temp = copy + ".new";
            File.Copy(bundled, temp, overwrite: true);
            File.SetUnixFileMode(temp, privateMode);
            File.Move(temp, copy, overwrite: true);
        }
        return copy;
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    private static string Tail(string text)
    {
        text = text.Trim();
        return text.Length <= 600 ? text : "…" + text[^600..];
    }
}
