using EmmaSpace.Core.Platform;

namespace EmmaSpace.Core.Tests;

public class SaveFileNameTests
{
    [Theory]
    // macOS's save panel appends the extension to a suggested name that already has it.
    [InlineData("/Users/emma/scan-2026-11-07-2338.emmasnap.emmasnap", "/Users/emma/scan-2026-11-07-2338.emmasnap")]
    [InlineData("/Users/emma/scan.emmasnap.EMMASNAP.emmasnap", "/Users/emma/scan.emmasnap")]
    [InlineData(@"C:\Users\Emma\scan.emmasnap", @"C:\Users\Emma\scan.emmasnap")]
    [InlineData("/home/emma/My Disk.EmmaSnap", "/home/emma/My Disk.EmmaSnap")]
    // Missing or different extension: appended.
    [InlineData("/home/emma/scan", "/home/emma/scan.emmasnap")]
    [InlineData("/home/emma/disk.old", "/home/emma/disk.old.emmasnap")]
    // A folder named like the extension is not part of the file name.
    [InlineData("/home/emma/x.emmasnap/scan", "/home/emma/x.emmasnap/scan.emmasnap")]
    // Nothing but the extension: kept as a name, not collapsed to nothing.
    [InlineData("/home/emma/.emmasnap.emmasnap", "/home/emma/.emmasnap.emmasnap")]
    public void The_saved_path_ends_in_the_extension_exactly_once(string picked, string expected)
    {
        Assert.Equal(expected, SaveFileName.WithSingleExtension(picked, ".emmasnap"));
        Assert.Equal(expected, SaveFileName.WithSingleExtension(picked, "emmasnap"));
    }

    [Fact]
    public void The_suggestion_leaves_the_extension_to_the_macOS_save_panel()
    {
        var expected = OperatingSystem.IsMacOS() ? "scan-2026-11-07-2338" : "scan-2026-11-07-2338.emmasnap";
        Assert.Equal(expected, SaveFileName.Suggestion("scan-2026-11-07-2338.emmasnap", ".emmasnap"));
        Assert.Equal("report", SaveFileName.Suggestion("report", ".emmasnap"));
    }
}
