using EmmaSpace.Core.Platform;

namespace EmmaSpace.Core.Tests;

public class FileActionsTests
{
    [Fact]
    public void Trash_is_named_the_way_the_platform_names_it()
    {
        var expected = OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";
        Assert.Equal(expected, FileActions.TrashName);
    }

    [Fact]
    public void A_missing_platform_tool_is_reported_as_an_io_error()
    {
        var ex = Assert.Throws<IOException>(() => FileActions.Run("emmaspace-no-such-tool-" + Guid.NewGuid().ToString("N")));
        Assert.Contains("Could not start", ex.Message);
    }

    // A shell running a one-line script, standing in for a launcher such as xdg-open.
    private static void RunScript(TimeSpan timeout, string windowsScript, string posixScript)
    {
        if (OperatingSystem.IsWindows())
            FileActions.RunLauncher("cmd.exe", timeout, "/c", windowsScript);
        else
            FileActions.RunLauncher("/bin/sh", timeout, "-c", posixScript);
    }

    [Fact]
    public void A_launcher_that_fails_is_reported_with_its_error_output()
    {
        var ex = Assert.Throws<IOException>(() =>
            RunScript(TimeSpan.FromSeconds(30), "echo no folder opener 1>&2 & exit /b 4", "echo no folder opener >&2; exit 4"));
        Assert.Equal("no folder opener", ex.Message);
    }

    [Fact]
    public void A_launcher_that_fails_silently_is_reported_with_its_exit_code()
    {
        var ex = Assert.Throws<IOException>(() => RunScript(TimeSpan.FromSeconds(30), "exit /b 3", "exit 3"));
        Assert.Contains("exit code 3", ex.Message);
    }

    [Fact]
    public void A_launcher_that_succeeds_is_not_waited_on_for_what_it_started()
    {
        // The launched program inherits the error pipe and keeps it open well past the launcher's exit.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX background job.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        RunScript(TimeSpan.FromSeconds(30), "", "sleep 5 & exit 0");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_launcher_still_running_after_the_timeout_counts_as_having_opened_the_item()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        RunScript(TimeSpan.FromMilliseconds(300), "ping -n 6 127.0.0.1 >nul", "sleep 5");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_missing_launcher_is_reported_as_an_io_error()
    {
        var ex = Assert.Throws<IOException>(() =>
            FileActions.RunLauncher("emmaspace-no-such-tool-" + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(1)));
        Assert.Contains("Could not start", ex.Message);
    }

    [Fact]
    public async Task Showing_a_deleted_item_in_its_folder_reports_an_error()
    {
        // Windows and Linux say so themselves; on macOS the message is Finder's (open -R).
        var missing = Path.Combine(Path.GetTempPath(), "emmaspace-missing-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAnyAsync<IOException>(() => new FileActions().RevealAsync(missing));
    }

    [Theory]
    [InlineData(@"C:\Movies\Crash.mkv", @"/select,""C:\Movies\Crash.mkv""")]
    [InlineData(@"C:\My Movies\Crash [1996], uncut.mkv", @"/select,""C:\My Movies\Crash [1996], uncut.mkv""")]
    public void Explorer_gets_the_path_quoted_after_the_comma(string path, string expected) =>
        Assert.Equal(expected, FileActions.ExplorerSelectArguments(path));

    [Theory]
    [InlineData("/home/emma/Movies/Crash.mkv", "file:///home/emma/Movies/Crash.mkv")]
    [InlineData("/home/emma/My Movies/a,b #1 100%.mkv", "file:///home/emma/My%20Movies/a%2Cb%20%231%20100%25.mkv")]
    [InlineData("/home/emma/Фильмы", "file:///home/emma/%D0%A4%D0%B8%D0%BB%D1%8C%D0%BC%D1%8B")]
    public void File_manager_uris_escape_every_segment(string path, string expected) =>
        Assert.Equal(expected, FileActions.FileUri(path));

    [Fact]
    public async Task Moving_a_missing_path_names_the_platform_bin_or_the_missing_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), "emmaspace-missing-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAnyAsync<IOException>(() => new FileActions().MoveToTrashAsync(missing));
        Assert.DoesNotContain(OperatingSystem.IsWindows() ? "Trash" : "Recycle Bin", ex.Message);
    }
}
