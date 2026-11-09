using Avalonia;
using EmmaSpace.Core.Platform;
using Velopack;

namespace EmmaSpace.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/update/uninstall hooks when started by the Velopack installer.
        // On Windows the hooks register .emmasnap files to open here with their own icon (installed next
        // to the exe); the app folder ("current") stays the same across updates.
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows()) velopack = WithSnapshotFileHooks(velopack);
        velopack.Run();

        // A snapshot double-clicked while Emma Space is open goes to that window, which asks whether to
        // open it, instead of starting a second one. (macOS sends it to the running app by itself.)
        if (!OperatingSystem.IsMacOS() && Services.ElevationService.StartupScanPath(args) is null &&
            SnapshotFileType.FindInArguments(args) is { } snapshot &&
            SnapshotHandoff.TrySend(Path.GetFullPath(snapshot), TimeSpan.FromMilliseconds(150)))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static VelopackApp WithSnapshotFileHooks(VelopackApp velopack) => velopack
        .OnAfterInstallFastCallback(_ => RegisterSnapshotFiles())
        .OnAfterUpdateFastCallback(_ => RegisterSnapshotFiles())
        .OnBeforeUninstallFastCallback(_ => SnapshotFileType.UnregisterOnWindows());

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterSnapshotFiles()
    {
        if (Environment.ProcessPath is { } exe)
            SnapshotFileType.RegisterOnWindows(exe, Path.Combine(AppContext.BaseDirectory, "emmasnap.ico"));
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
