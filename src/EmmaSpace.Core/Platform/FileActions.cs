using System.Diagnostics;
using System.Runtime.InteropServices;
using EmmaSpace.Core.Interop;

namespace EmmaSpace.Core.Platform;

public interface IFileActions
{
    /// <summary>Moves a file or folder to the Recycle Bin / Trash. Throws on failure.</summary>
    Task MoveToTrashAsync(string path);

    /// <summary>Shows a file or folder selected in its parent folder. Faults with IOException or Win32Exception.</summary>
    Task RevealAsync(string path);

    void Open(string path);

    bool CanShowProperties { get; }

    void ShowProperties(string path);
}

public sealed partial class FileActions : IFileActions
{
    /// <summary>What the platform calls its bin: "Recycle Bin" on Windows, "Trash" on macOS and Linux desktops.</summary>
    public static string TrashName { get; } = OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    public Task MoveToTrashAsync(string path) => Task.Run(() =>
    {
        if (NativeEngine.IsAvailable)
        {
            var status = NativeMethods.MoveToTrash(path);
            switch (status)
            {
                case EsStatus.Ok:
                    return;
                case EsStatus.NotSupported:
                    break; // fall through to the platform tools below
                case EsStatus.Cancelled:
                    throw new OperationCanceledException($"Moving to the {TrashName} was cancelled.");
                default:
                    throw new IOException(status switch
                    {
                        EsStatus.NotFound => $"'{path}' no longer exists.",
                        EsStatus.AccessDenied => $"Access to '{path}' was denied.",
                        _ => $"Could not move '{path}' to the {TrashName} ({status}).",
                    });
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            // Finder's delete moves to Trash and supports "Put Back".
            var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
            Run("osascript", "-e", $"tell application \"Finder\" to delete POSIX file \"{escaped}\"");
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            Run("gio", "trash", path);
            return;
        }
        throw new PlatformNotSupportedException($"Moving to the {TrashName} needs the native engine on this system.");
    });

    /// <summary>
    /// Opens the folder that contains <paramref name="path"/> (a file or a folder) in the system file
    /// manager, with the item selected. Runs in the background, since a network folder can take a
    /// while to answer; a missing item or a failed launch faults the task (IOException or Win32Exception).
    /// </summary>
    public Task RevealAsync(string path)
    {
        if (OperatingSystem.IsWindows()) return RevealOnWindowsAsync(path);
        return Task.Run(() =>
        {
            if (OperatingSystem.IsMacOS())
                Run("open", "-R", path); // open reports a missing item itself
            else
                RevealOnLinux(path);
        });
    }

    private static IOException NotFound(string path) => new($"'{path}' could not be found. It may have been moved or deleted.");

    // The shell call explorer.exe /select makes internally, without its command line: a path with a
    // space must be quoted after the comma (/select,"C:\a b\c.txt"), and .NET's argument quoting wraps
    // the whole argument instead, so Explorer ignored it and opened Documents. The call is made on its
    // own STA thread because the shell needs COM there.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Task RevealOnWindowsAsync(string path)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = CoInitializeEx(nint.Zero, CoinitApartmentThreaded) >= 0;
            try
            {
                var item = ILCreateFromPathW(path);
                if (item == nint.Zero)
                {
                    // The shell could not resolve the path: gone, or a location it can't parse.
                    if (!File.Exists(path) && !Directory.Exists(path)) throw NotFound(path);
                    StartExplorerSelect(path);
                }
                else
                {
                    try
                    {
                        if (SHOpenFolderAndSelectItems(item, 0, nint.Zero, 0) < 0) StartExplorerSelect(path);
                    }
                    finally
                    {
                        ILFree(item);
                    }
                }
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex); // never let it escape: an exception on this thread would end the app
            }
            finally
            {
                if (initialized) CoUninitialize();
            }
        }) { IsBackground = true, Name = "Show in folder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>The fallback: explorer.exe /select,"path", quoted the way Explorer parses it.</summary>
    internal static string ExplorerSelectArguments(string path) => $"/select,\"{path}\"";

    private static void StartExplorerSelect(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", ExplorerSelectArguments(path)) { UseShellExecute = false })?.Dispose();

    // File managers that implement org.freedesktop.FileManager1 (Nautilus, Dolphin, Nemo, Caja, Thunar
    // and others) open the parent folder with the item selected. Without one, open the parent folder.
    // --print-reply makes dbus-send wait, so a missing file manager shows up as a failure.
    private static void RevealOnLinux(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw NotFound(path);
        try
        {
            Run("dbus-send", "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems", "array:string:" + FileUri(path), "string:");
        }
        catch (IOException)
        {
            RunLauncher("xdg-open", LauncherTimeout, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path);
        }
    }

    /// <summary>How long a launcher gets to report a failure before it counts as having opened the item.</summary>
    internal static readonly TimeSpan LauncherTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs a launcher such as xdg-open and reports its failure (a nonzero exit) as an IOException with
    /// its error output. Unlike <see cref="Run"/>, it does not wait for the launched program: some
    /// launchers stay until the program they started closes, and that program can inherit the output
    /// pipes. So a launcher still running after <paramref name="timeout"/> counts as success, and the
    /// error output is only read once the launcher has failed, for a moment at most.
    /// </summary>
    internal static void RunLauncher(string fileName, TimeSpan timeout, params string[] args)
    {
        Process? started;
        try
        {
            started = Process.Start(new ProcessStartInfo(fileName, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException($"Could not start {fileName}: {ex.Message}", ex);
        }
        using var process = started ?? throw new IOException($"Could not start {fileName}.");
        var error = process.StandardError.ReadToEndAsync(); // drained as it runs, so a chatty launcher can't fill the pipe
        if (!process.WaitForExit(timeout) || process.ExitCode == 0) return;
        var message = "";
        try
        {
            if (error.Wait(TimeSpan.FromSeconds(1))) message = error.Result.Trim();
        }
        catch (AggregateException)
        {
            // no error text to show; the exit code still says it failed
        }
        throw new IOException(message.Length == 0 ? $"{fileName} failed (exit code {process.ExitCode})." : message);
    }

    /// <summary>
    /// A file:// URI for an absolute POSIX path, every segment percent-encoded. That covers '#', '%'
    /// and spaces, and commas too, at which dbus-send would otherwise split the array.
    /// </summary>
    internal static string FileUri(string path) =>
        "file://" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public void Open(string path)
    {
        if (OperatingSystem.IsMacOS())
            Process.Start("open", [path]);
        else if (OperatingSystem.IsLinux())
            Process.Start("xdg-open", [path]);
        else
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public bool CanShowProperties => OperatingSystem.IsWindows();

    public void ShowProperties(string path)
    {
        if (OperatingSystem.IsWindows()) SHObjectProperties(nint.Zero, ShopFilePath, path, null);
    }

    // Failures (including a missing tool such as gio) surface as IOException, which the app turns
    // into an error dialog. The tool's own output is discarded rather than mixed into the app's.
    internal static void Run(string fileName, params string[] args)
    {
        Process? started;
        try
        {
            started = Process.Start(new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException($"Could not start {fileName}: {ex.Message}", ex);
        }
        using var process = started ?? throw new IOException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync(); // drained alongside stderr, so neither pipe fills up
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        output.Wait();
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? $"{fileName} failed." : error.Trim());
    }

    private const uint ShopFilePath = 0x2;
    private const uint CoinitApartmentThreaded = 0x2;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ILCreateFromPathW(string pszPath);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(nint pidl);

    /// <summary>With no child items, opens the parent of <paramref name="pidlFolder"/> and selects it.</summary>
    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(nint pidlFolder, uint cidl, nint apidl, uint dwFlags);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(nint hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);
}
