using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EmmaSpace.Core.Snapshots;
using Microsoft.Win32;

namespace EmmaSpace.Core.Platform;

/// <summary>
/// The .emmasnap file type as each system knows it, so a double-clicked snapshot opens in Emma Space
/// and shows its own icon. Windows: per-user registry classes, written by the installer hooks.
/// Linux: a desktop entry, MIME type and icons in the user's data folder, written when the AppImage
/// starts. macOS: the document type in the app bundle's Info.plist (build/macos/Info.plist).
/// </summary>
public static partial class SnapshotFileType
{
    public const string Description = "Emma Space snapshot";
    public const string ProgId = "EmmaSpace.Snapshot";
    public const string MimeType = "application/x-emmaspace-snapshot";

    /// <summary>The first launch argument that names an existing snapshot (how a double-click arrives).</summary>
    public static string? FindInArguments(IEnumerable<string>? args) => args?.FirstOrDefault(IsSnapshot);

    public static bool IsSnapshot(string? path) =>
        !string.IsNullOrEmpty(path) && path.EndsWith(SnapshotFile.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    // Windows

    /// <summary>
    /// Registers the file type for the current user: "<paramref name="exePath"/> "%1"" opens it and
    /// <paramref name="iconPath"/> is its icon. A choice the user made in "Open with" still wins.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void RegisterOnWindows(string exePath, string iconPath, string extension = SnapshotFile.Extension, string progId = ProgId)
    {
        using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
        {
            using (var type = classes.CreateSubKey(progId))
            {
                type.SetValue("", Description);
                type.SetValue("FriendlyTypeName", Description);
                using (var icon = type.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{iconPath}\",0");
                using (var command = type.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{exePath}\" \"%1\"");
            }
            using (var ext = classes.CreateSubKey(extension))
            {
                ext.SetValue("", progId);
                ext.SetValue("Content Type", MimeType);
                using var openWith = ext.CreateSubKey("OpenWithProgids");
                openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
            }
        }
        NotifyShellOfAssociationChange();
    }

    /// <summary>Removes what <see cref="RegisterOnWindows"/> wrote (on uninstall).</summary>
    [SupportedOSPlatform("windows")]
    public static void UnregisterOnWindows(string extension = SnapshotFile.Extension, string progId = ProgId)
    {
        using (var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true))
        {
            if (classes is null) return;
            classes.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
            using (var ext = classes.OpenSubKey(extension, writable: true))
            {
                if (ext is not null)
                {
                    using (var openWith = ext.OpenSubKey("OpenWithProgids", writable: true)) openWith?.DeleteValue(progId, throwOnMissingValue: false);
                    if (ext.GetValue("") as string == progId)
                    {
                        ext.DeleteValue("", throwOnMissingValue: false);
                        if (ext.GetValue("Content Type") as string == MimeType) ext.DeleteValue("Content Type", throwOnMissingValue: false);
                    }
                }
            }
            // Drop the extension key if nothing else uses it.
            using (var ext = classes.OpenSubKey(extension))
            {
                var empty = ext is not null && ext.ValueCount == 0 && ext.GetSubKeyNames().All(name =>
                {
                    using var sub = ext.OpenSubKey(name);
                    return sub is not null && sub.ValueCount == 0 && sub.SubKeyCount == 0;
                });
                if (empty) classes.DeleteSubKeyTree(extension, throwOnMissingSubKey: false);
            }
        }
        NotifyShellOfAssociationChange();
    }

    [SupportedOSPlatform("windows")]
    private static void NotifyShellOfAssociationChange() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, nint.Zero, nint.Zero);

    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0;

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    // Linux

    /// <summary>Freedesktop names: the desktop entry is <c>{DesktopId}.desktop</c>, its icon <c>{DesktopId}</c>.</summary>
    public const string DesktopId = "com.alexxfromgit.emmaspace";
    public static string MimeIconName => MimeType.Replace('/', '-');

    /// <summary>
    /// Makes the AppImage at <paramref name="appImage"/> known to the desktop: an Emma Space menu entry
    /// that opens .emmasnap files, the MIME type (by extension and by the file's first bytes) and both
    /// icons, all under <paramref name="dataHome"/> (normally ~/.local/share). Files are only rewritten
    /// when they change, e.g. when the AppImage moved. Returns true if anything was written.
    /// <paramref name="refreshDesktop"/> false skips the desktop tools (caches, default app), for tests.
    /// </summary>
    public static bool InstallOnLinux(string appImage, byte[] appIconPng, byte[] fileIconPng, string dataHome,
        Action<string>? log = null, bool refreshDesktop = true)
    {
        var applications = Path.Combine(dataHome, "applications");
        var mimePackages = Path.Combine(dataHome, "mime", "packages");
        var icons = Path.Combine(dataHome, "icons", "hicolor", "256x256");
        var changed = false;
        changed |= WriteIfChanged(Path.Combine(applications, DesktopId + ".desktop"), Encoding.UTF8.GetBytes(DesktopEntry(appImage)));
        changed |= WriteIfChanged(Path.Combine(mimePackages, DesktopId + ".xml"), Encoding.UTF8.GetBytes(MimeInfo()));
        changed |= WriteIfChanged(Path.Combine(icons, "apps", DesktopId + ".png"), appIconPng);
        changed |= WriteIfChanged(Path.Combine(icons, "mimetypes", MimeIconName + ".png"), fileIconPng);
        if (!changed || !refreshDesktop) return changed;

        // Refresh the desktop's caches. Each tool is optional: without it the desktop picks the files up later.
        TryRun(log, "update-mime-database", Path.Combine(dataHome, "mime"));
        TryRun(log, "update-desktop-database", applications);
        // The user's icon folder has no icon cache (one would hide icons other apps add later without
        // rebuilding it); toolkits notice new icons by the theme folder's modification time.
        try
        {
            Directory.SetLastWriteTimeUtc(Path.Combine(dataHome, "icons", "hicolor"), DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"icons: {ex.Message}");
        }
        // Become the default only where the user hasn't chosen an app for snapshots.
        if (string.IsNullOrWhiteSpace(TryRead(log, "xdg-mime", "query", "default", MimeType)))
            TryRun(log, "xdg-mime", "default", DesktopId + ".desktop", MimeType);
        return true;
    }

    public static string DesktopEntry(string appImage) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=Emma Space
        GenericName=Disk space analyzer
        Comment=See what is using your disk space
        Exec={QuoteExec(appImage)} %f
        Icon={DesktopId}
        Terminal=false
        Categories=Utility;
        MimeType={MimeType};
        StartupWMClass=EmmaSpace

        """;

    public static string MimeInfo() =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
          <mime-type type="{MimeType}">
            <comment>{Description}</comment>
            <icon name="{MimeIconName}"/>
            <glob pattern="*{SnapshotFile.Extension}"/>
            <magic priority="60">
              <match type="string" offset="0" value="{SnapshotFile.MagicPrefix}"/>
            </magic>
          </mime-type>
        </mime-info>

        """;

    /// <summary>
    /// A path as one argument of a desktop entry's Exec key: quoted, with '"', '`', '$' and '\' escaped
    /// inside the quotes, then '\' escaped again for the string value, and '%' doubled.
    /// </summary>
    internal static string QuoteExec(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }
        quoted.Append('"');
        return quoted.ToString().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal);
    }

    private static bool WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    private static void TryRun(Action<string>? log, string tool, params string[] args) => TryRead(log, tool, args);

    /// <summary>Runs a desktop tool and returns its output, or null (logged) if it is missing or fails.</summary>
    private static string? TryRead(Action<string>? log, string tool, params string[] args)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;
            // Both pipes drain in the background, so a chatty tool can't block and the timeout applies.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                log?.Invoke($"{tool}: timed out");
                return null;
            }
            process.WaitForExit(); // let the output readers finish
            if (process.ExitCode == 0) return output.Result;
            log?.Invoke($"{tool}: exit code {process.ExitCode}: {error.Result.Trim()}");
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log?.Invoke($"{tool}: {ex.Message}");
            return null;
        }
    }
}
