using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using EmmaSpace.Core.Interop;

namespace EmmaSpace.App.Services;

/// <summary>
/// Restarts Emma Space as administrator so the engine can read the NTFS master file table,
/// which is many times faster than walking folders.
/// </summary>
public sealed class ElevationService
{
    public const string ScanArgument = "--scan";

    public bool IsElevated => NativeEngine.IsElevated;

    public bool CanElevate => OperatingSystem.IsWindows() && NativeEngine.IsAvailable && !IsElevated;

    /// <summary>Starts an elevated copy that scans <paramref name="path"/>, then exits. False if the user declined.</summary>
    public bool TryRestartElevated(string? path)
    {
        var exe = Environment.ProcessPath;
        if (!CanElevate || exe is null) return false;
        var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        if (!string.IsNullOrWhiteSpace(path))
        {
            start.ArgumentList.Add(ScanArgument);
            start.ArgumentList.Add(path);
        }

        try
        {
            Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC prompt declined
        {
            return false;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        return true;
    }

    /// <summary>The path passed with <see cref="ScanArgument"/>, if any.</summary>
    public static string? StartupScanPath(IReadOnlyList<string>? args)
    {
        if (args is null) return null;
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == ScanArgument) return args[i + 1];
        return null;
    }
}
