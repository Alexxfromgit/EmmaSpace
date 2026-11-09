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
