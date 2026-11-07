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

    public void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", ["-R", path]);
        else
            Process.Start("xdg-open", [Path.GetDirectoryName(path) ?? path]);
    }

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

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(nint hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);
}
