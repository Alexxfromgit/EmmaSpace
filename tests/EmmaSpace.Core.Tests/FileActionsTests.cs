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
    public async Task Moving_a_missing_path_names_the_platform_bin_or_the_missing_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), "emmaspace-missing-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAnyAsync<IOException>(() => new FileActions().MoveToTrashAsync(missing));
        Assert.DoesNotContain(OperatingSystem.IsWindows() ? "Trash" : "Recycle Bin", ex.Message);
    }
}
