using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EmmaSpace.Core.Platform;
using EmmaSpace.Core.Snapshots;
using Microsoft.Win32;

namespace EmmaSpace.Core.Tests;

public partial class SnapshotFileTypeTests
{
    [Fact]
    public void A_double_clicked_snapshot_is_found_among_the_launch_arguments()
    {
        var dir = Directory.CreateTempSubdirectory("emmaspace-snapfile-").FullName;
        try
        {
            var snapshot = Path.Combine(dir, "My Disk.EmmaSnap");
            File.WriteAllText(snapshot, "x");
            var other = Path.Combine(dir, "notes.txt");
            File.WriteAllText(other, "x");

            Assert.Equal(snapshot, SnapshotFileType.FindInArguments(["--verbose", other, snapshot]));
            Assert.Null(SnapshotFileType.FindInArguments([other, Path.Combine(dir, "missing.emmasnap")]));
            Assert.Null(SnapshotFileType.FindInArguments([]));
            Assert.Null(SnapshotFileType.FindInArguments(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_renamed_snapshot_is_recognized_by_its_first_bytes()
    {
        // The Linux MIME type matches snapshots by content too, so the desktop may hand over one without the extension.
        var dir = Directory.CreateTempSubdirectory("emmaspace-snapfile-").FullName;
        try
        {
            var renamed = Path.Combine(dir, "weekly");
            using (var tree = TreeFixtures.Sample())
            using (var stream = File.Create(renamed))
                SnapshotFile.Save(tree, stream);
            var text = Path.Combine(dir, "notes");
            File.WriteAllText(text, "EMSNA"); // shorter than the prefix
            var other = Path.Combine(dir, "other.bin");
            File.WriteAllText(other, "not a snapshot at all");

            Assert.True(SnapshotFileType.IsSnapshot(renamed));
            Assert.False(SnapshotFileType.IsSnapshot(text));
            Assert.False(SnapshotFileType.IsSnapshot(other));
            Assert.False(SnapshotFileType.IsSnapshot(dir));
            Assert.Equal(renamed, SnapshotFileType.FindInArguments([other, renamed]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Linux_registration_is_retried_until_the_desktop_has_taken_it_in()
    {
        var home = Directory.CreateTempSubdirectory("emmaspace-xdg-").FullName;
        try
        {
            byte[] appIcon = [1, 2, 3], fileIcon = [4, 5, 6];
            var calls = new List<string>();
            var mimeDatabase = SnapshotFileType.ToolStatus.Failed;
            (SnapshotFileType.ToolStatus, string) Run(Action<string>? _, string tool, string[] args)
            {
                calls.Add($"{tool} {string.Join(' ', args)}");
                return tool switch
                {
                    "update-mime-database" => (mimeDatabase, ""),
                    "update-desktop-database" => (SnapshotFileType.ToolStatus.Missing, ""), // not installed: nothing to retry
                    _ when args[0] == "query" => (SnapshotFileType.ToolStatus.Ok, "\n"), // no default app chosen yet
                    _ => (SnapshotFileType.ToolStatus.Ok, ""),
                };
            }
            bool Install() => SnapshotFileType.InstallOnLinux("/opt/EmmaSpace.AppImage", appIcon, fileIcon, home, null, Run);
            var stamp = SnapshotFileType.RegistrationStampPath(home);

            // The MIME database refresh fails: the files are written, but nothing records the registration as done.
            Assert.True(Install());
            Assert.Contains(calls, c => c.StartsWith("xdg-mime default com.alexxfromgit.emmaspace.desktop", StringComparison.Ordinal));
            Assert.False(File.Exists(stamp));

            // The next start refreshes again although no file changed, and this time it works.
            calls.Clear();
            mimeDatabase = SnapshotFileType.ToolStatus.Ok;
            Assert.True(Install());
            Assert.Contains(calls, c => c.StartsWith("update-mime-database", StringComparison.Ordinal));
            Assert.True(File.Exists(stamp));

            // Then nothing is left to do.
            calls.Clear();
            Assert.False(Install());
            Assert.Empty(calls);

            // A moved AppImage registers again, and an app the user chose for snapshots stays their default.
            calls.Clear();
            Assert.True(SnapshotFileType.InstallOnLinux("/home/emma/EmmaSpace.AppImage", appIcon, fileIcon, home, null,
                (log, tool, args) => args is ["query", ..] ? (SnapshotFileType.ToolStatus.Ok, "other.desktop\n") : Run(log, tool, args)));
            Assert.DoesNotContain(calls, c => c.StartsWith("xdg-mime default", StringComparison.Ordinal));
            Assert.False(SnapshotFileType.InstallOnLinux("/home/emma/EmmaSpace.AppImage", appIcon, fileIcon, home, null, Run));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Snapshots_start_with_the_bytes_the_linux_mime_type_matches()
    {
        using var tree = TreeFixtures.Sample();
        using var stream = new MemoryStream();
        SnapshotFile.Save(tree, stream);
        Assert.StartsWith(SnapshotFile.MagicPrefix, Encoding.ASCII.GetString(stream.ToArray(), 0, 8));
        Assert.Contains($"value=\"{SnapshotFile.MagicPrefix}\"", SnapshotFileType.MimeInfo());
        Assert.Contains("<glob pattern=\"*.emmasnap\"/>", SnapshotFileType.MimeInfo());
    }

    [Theory]
    [InlineData("/home/emma/Apps/EmmaSpace.AppImage", "\"/home/emma/Apps/EmmaSpace.AppImage\"")]
    [InlineData("/home/emma/My Apps/Emma Space.AppImage", "\"/home/emma/My Apps/Emma Space.AppImage\"")]
    // '$', '"' and '\' are escaped inside the quotes, then every '\' again for the string value; '%' is doubled.
    [InlineData("/x/$HOME/a\"b\\c%d", "\"/x/\\\\$HOME/a\\\\\"b\\\\\\\\c%%d\"")]
    public void Desktop_entries_quote_the_appimage_path(string path, string expected) =>
        Assert.Equal(expected, SnapshotFileType.QuoteExec(path));

    [Fact]
    public void The_linux_install_writes_the_desktop_files_once_and_again_when_the_appimage_moves()
    {
        var home = Directory.CreateTempSubdirectory("emmaspace-xdg-").FullName;
        try
        {
            byte[] appIcon = [1, 2, 3], fileIcon = [4, 5, 6];
            Assert.True(SnapshotFileType.InstallOnLinux("/opt/Emma Space.AppImage", appIcon, fileIcon, home, refreshDesktop: false));

            var desktop = File.ReadAllText(Path.Combine(home, "applications", "com.alexxfromgit.emmaspace.desktop"));
            Assert.Contains("Exec=\"/opt/Emma Space.AppImage\" %f", desktop);
            Assert.Contains("MimeType=application/x-emmaspace-snapshot;", desktop);
            Assert.Contains("Icon=com.alexxfromgit.emmaspace", desktop);
            Assert.True(File.Exists(Path.Combine(home, "mime", "packages", "com.alexxfromgit.emmaspace.xml")));
            Assert.Equal(appIcon, File.ReadAllBytes(Path.Combine(home, "icons", "hicolor", "256x256", "apps", "com.alexxfromgit.emmaspace.png")));
            Assert.Equal(fileIcon, File.ReadAllBytes(Path.Combine(home, "icons", "hicolor", "256x256", "mimetypes", "application-x-emmaspace-snapshot.png")));

            Assert.False(SnapshotFileType.InstallOnLinux("/opt/Emma Space.AppImage", appIcon, fileIcon, home, refreshDesktop: false));
            Assert.True(SnapshotFileType.InstallOnLinux("/home/emma/EmmaSpace.AppImage", appIcon, fileIcon, home, refreshDesktop: false));
            Assert.Contains("Exec=\"/home/emma/EmmaSpace.AppImage\" %f",
                File.ReadAllText(Path.Combine(home, "applications", "com.alexxfromgit.emmaspace.desktop")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Windows_opens_registered_snapshots_in_the_app_with_their_icon_and_forgets_them_on_uninstall()
    {
        if (OperatingSystem.IsWindows()) RegistersAndUnregisters();
        else Assert.Skip("The registry exists only on Windows.");
    }

    // A made-up extension and ProgId, so the real .emmasnap registration on this machine is untouched.
    [SupportedOSPlatform("windows")]
    private static void RegistersAndUnregisters()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var extension = $".emmatest{id}";
        var progId = $"EmmaSpace.Test{id}";
        const string exe = @"C:\Program Files\Emma Space\EmmaSpace.exe";
        const string icon = @"C:\Program Files\Emma Space\emmasnap.ico";
        try
        {
            SnapshotFileType.RegisterOnWindows(exe, icon, extension, progId);
            Assert.Equal($"\"{exe}\" \"%1\"", Query(AssocStrCommand, extension));
            Assert.Equal($"\"{icon}\",0", Query(AssocStrDefaultIcon, extension));
            Assert.Equal(SnapshotFileType.Description, Query(AssocStrFriendlyDocName, extension));

            SnapshotFileType.UnregisterOnWindows(extension, progId);
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes");
            Assert.Null(classes?.OpenSubKey(extension));
            Assert.Null(classes?.OpenSubKey(progId));
        }
        finally
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            classes?.DeleteSubKeyTree(extension, throwOnMissingSubKey: false);
            classes?.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
        }
    }

    private const int AssocStrCommand = 1, AssocStrFriendlyDocName = 3, AssocStrDefaultIcon = 15;

    /// <summary>Asks the shell how it resolves the extension, as Explorer does on a double-click.</summary>
    private static string? Query(int what, string extension)
    {
        uint length = 1024;
        var buffer = new char[length];
        var hr = AssocQueryStringW(0, what, extension, null, buffer, ref length);
        return hr < 0 ? null : new string(buffer, 0, (int)length - 1);
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AssocQueryStringW(int flags, int str, string pszAssoc, string? pszExtra, [Out] char[] pszOut, ref uint pcchOut);
}
