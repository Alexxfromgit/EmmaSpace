using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace EmmaSpace.Core.Platform;

/// <summary>
/// Lets a double-clicked snapshot reach the Emma Space that is already running. Windows and Linux start
/// a new process for every double-click; that process hands the path over this per-user local pipe
/// and exits, and the running app asks whether to open it. (macOS sends the file to the running app
/// itself.) Only the same user can connect. When nothing answers, e.g. no app is running or it runs
/// as administrator, the new process simply opens the snapshot itself.
/// </summary>
public static partial class SnapshotHandoff
{
    private const int MaxPathBytes = 64 * 1024;
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One name per user, plus the session on Windows (several users can be signed in at once). On
    /// Linux the process "session" differs between apps started from the dock, a terminal or the file
    /// manager, so it isn't part of the name there; the socket only accepts the same user anyway.
    /// </summary>
    public static string DefaultPipeName { get; } = OperatingSystem.IsWindows()
        ? $"EmmaSpace.Snapshots.{Sanitize(Environment.UserName)}.{Process.GetCurrentProcess().SessionId}"
        : $"EmmaSpace.Snapshots.{Sanitize(Environment.UserName)}";

    /// <summary>
    /// Sends <paramref name="snapshotPath"/> to a running Emma Space. True once that app has accepted it;
    /// false if none is listening (found within <paramref name="connectTimeout"/>) or it didn't answer.
    /// A running app always has the pipe waiting, so a short timeout is enough.
    /// </summary>
    public static bool TrySend(string snapshotPath, TimeSpan connectTimeout, string? pipeName = null)
    {
        var path = Encoding.UTF8.GetBytes(snapshotPath);
        if (path.Length is 0 or > MaxPathBytes) return false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(connectTimeout);
            using var cancel = new CancellationTokenSource(ExchangeTimeout);
            var pid = new byte[4];
            Wait(pipe.ReadExactlyAsync(pid, cancel.Token));
            // This process was started by the user's double-click, so it may pass the right to come to
            // the front on to the running app, which then shows its question on top.
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(BitConverter.ToInt32(pid));

            Wait(pipe.WriteAsync(BitConverter.GetBytes(path.Length), cancel.Token));
            Wait(pipe.WriteAsync(path, cancel.Token));
            var ack = new byte[1];
            Wait(pipe.ReadExactlyAsync(ack, cancel.Token));
            return ack[0] == 1;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }

        static void Wait(ValueTask task) => task.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Starts accepting snapshots from new processes; <paramref name="received"/> runs on a background
    /// thread. Null if the pipe can't be created, or (outside Windows, where only one process can own
    /// the socket) if another Emma Space of this user already listens.
    /// </summary>
    public static IDisposable? Listen(Action<string> received, string? pipeName = null)
    {
        pipeName ??= DefaultPipeName;
        if (!OperatingSystem.IsWindows() && IsListening(pipeName)) return null;
        var listener = new Listener(pipeName, received);
        return listener.Start() ? listener : null;
    }

    // Outside Windows a new server replaces the socket of an existing one, so don't start a second.
    private static bool IsListening(string pipeName)
    {
        try
        {
            using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            probe.Connect(TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class Listener(string pipeName, Action<string> received) : IDisposable
    {
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);
        private readonly CancellationTokenSource _stop = new();
        private NamedPipeServerStream? _first;

        public bool Start()
        {
            try
            {
                _first = Create();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[handoff] not listening: {ex.Message}");
                return false;
            }
            _ = Task.Run(LoopAsync);
            return true;
        }

        private NamedPipeServerStream Create() => new(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private NamedPipeServerStream? TryCreate()
        {
            try
            {
                return Create();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[handoff] {ex.Message}");
                return null;
            }
        }

        // One instance always waits for the next client: it is created before the current client is
        // served, so a double-click finds the pipe at once (and on Linux the socket stays bound).
        private async Task LoopAsync()
        {
            var waiting = Interlocked.Exchange(ref _first, null);
            var backoff = TimeSpan.FromMilliseconds(250);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    waiting ??= TryCreate();
                    if (waiting is null)
                    {
                        await Task.Delay(backoff, _stop.Token);
                        backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                        continue;
                    }
                    backoff = TimeSpan.FromMilliseconds(250);

                    var pipe = waiting;
                    waiting = null;
                    try
                    {
                        await pipe.WaitForConnectionAsync(_stop.Token);
                        waiting = TryCreate();
                        if (await ReceiveAsync(pipe) is { } path) Deliver(path);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException ||
                                               ex is OperationCanceledException && !_stop.IsCancellationRequested)
                    {
                        Trace.WriteLine($"[handoff] {ex.Message}"); // a client that went away or sent garbage
                    }
                    finally
                    {
                        await pipe.DisposeAsync();
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            finally
            {
                if (waiting is not null) await waiting.DisposeAsync();
            }
        }

        private void Deliver(string path)
        {
            try
            {
                received(path);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[handoff] handler failed: {ex}"); // never stop listening because of one file
            }
        }

        private async Task<string?> ReceiveAsync(NamedPipeServerStream pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(ExchangeTimeout);
            await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), timeout.Token);
            var header = new byte[4];
            await pipe.ReadExactlyAsync(header, timeout.Token);
            var length = BitConverter.ToInt32(header);
            if (length is <= 0 or > MaxPathBytes) return null;
            var path = new byte[length];
            await pipe.ReadExactlyAsync(path, timeout.Token);
            await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
            return Encoding.UTF8.GetString(path);
        }

        public void Dispose()
        {
            _stop.Cancel();
            Interlocked.Exchange(ref _first, null)?.Dispose();
        }
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        return chars.Length == 0 ? "user" : new string(chars);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int dwProcessId);
}
