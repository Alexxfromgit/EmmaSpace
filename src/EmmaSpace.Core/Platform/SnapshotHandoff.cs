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

    /// <summary>One name per user and session, so other users' instances never see each other.</summary>
    public static string DefaultPipeName { get; } =
        $"EmmaSpace.Snapshots.{Sanitize(Environment.UserName)}.{Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// Sends <paramref name="snapshotPath"/> to a running Emma Space. True once that app has accepted it;
    /// false if none is listening (found within <paramref name="connectTimeout"/>) or it didn't answer.
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
    /// thread. Null if the pipe can't be created (e.g. on Linux another Emma Space already listens).
    /// </summary>
    public static IDisposable? Listen(Action<string> received, string? pipeName = null)
    {
        var listener = new Listener(pipeName ?? DefaultPipeName, received);
        return listener.Start() ? listener : null;
    }

    private sealed class Listener(string pipeName, Action<string> received) : IDisposable
    {
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

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = Interlocked.Exchange(ref _first, null) ?? Create();
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    if (await ReceiveAsync(pipe) is { } path) received(path);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
                {
                    // A client that went away or sent garbage, or the pipe couldn't be made: try again shortly.
                    Trace.WriteLine($"[handoff] {ex.Message}");
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), _stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                finally
                {
                    if (pipe is not null) await pipe.DisposeAsync();
                }
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
