using System.Diagnostics;
using Avalonia.Platform;
using EmmaSpace.Core.Platform;

namespace EmmaSpace.App.Services;

/// <summary>
/// Linux AppImages carry their desktop entry inside the image, so the desktop doesn't know them. When
/// Emma Space runs as an AppImage it adds itself to the user's applications and registers .emmasnap
/// files (see <see cref="SnapshotFileType.InstallOnLinux"/>). Windows does this from the installer
/// hooks in Program, macOS from the app bundle's Info.plist.
/// </summary>
public static class DesktopIntegration
{
    public static void StartOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Set by the AppImage runtime to the image's own path; absent for development builds.
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrEmpty(appImage) || !File.Exists(appImage)) return;

        _ = Task.Run(() =>
        {
            try
            {
                var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                if (SnapshotFileType.InstallOnLinux(appImage, ReadAsset("emmaspace.png"), ReadAsset("emmasnap.png"), dataHome, Log))
                    Log($"Registered {appImage} with the desktop");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log(ex.Message);
            }
        });
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://EmmaSpace/Assets/{name}"));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void Log(string message) => Trace.WriteLine($"[desktop] {message}");
}
