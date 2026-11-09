using System.Diagnostics;
using System.Runtime.InteropServices;
using EmmaSpace.Core.Interop;

namespace EmmaSpace.Core.Platform;

public interface IFileActions
{
    /// <summary>Moves a file or folder to the Recycle Bin / Trash. Throws on failure.</summary>
    Task MoveToTrashAsync(string path);

    void Reveal(string path);

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
    /// manager, with the item selected.
    /// </summary>
    public void Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException($"'{path}' no longer exists.");
        if (OperatingSystem.IsWindows())
            RevealOnWindows(path);
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", ["-R", path]);
        else
            RevealOnLinux(path);
    }

    // The shell call explorer.exe /select makes internally, without its command line: a path with a
    // space must be quoted after the comma (/select,"C:\a b\c.txt"), and .NET's argument quoting wraps
    // the whole argument instead, so Explorer ignored it and opened Documents. The call is made on its
    // own STA thread because the shell needs COM there, and a slow (e.g. network) folder must not
    // block the UI.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RevealOnWindows(string path)
    {
        var thread = new Thread(() =>
        {
            var initialized = CoInitializeEx(nint.Zero, CoinitApartmentThreaded) >= 0;
            try
            {
                var item = ILCreateFromPathW(path);
                var hr = item == nint.Zero ? -1 : SHOpenFolderAndSelectItems(item, 0, nint.Zero, 0);
                if (item != nint.Zero) ILFree(item);
                if (hr < 0) StartExplorerSelect(path);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Nothing left to try; the app has already returned from the menu click.
            }
            finally
            {
                if (initialized) CoUninitialize();
            }
        }) { IsBackground = true, Name = "Show in folder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>The fallback: explorer.exe /select,"path", quoted the way Explorer parses it.</summary>
    internal static string ExplorerSelectArguments(string path) => $"/select,\"{path}\"";

    private static void StartExplorerSelect(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", ExplorerSelectArguments(path)) { UseShellExecute = false })?.Dispose();

    // File managers that implement org.freedesktop.FileManager1 (Nautilus, Dolphin, Nemo, Caja, Thunar
    // and others) open the parent folder with the item selected. Without one, open the parent folder.
    // In the background, because D-Bus may first have to start the file manager.
    private static void RevealOnLinux(string path) => Task.Run(() =>
    {
        try
        {
            Run("dbus-send", "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems", "array:string:" + FileUri(path), "string:");
        }
        catch (IOException)
        {
            try
            {
                Process.Start("xdg-open", [Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path])?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No xdg-open either; nothing left to try.
            }
        }
    });

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

    // Failures (including a missing tool such as gio) surface as IOException, which the app's
    // trash handler turns into an error dialog.
    internal static void Run(string fileName, params string[] args)
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
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
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
