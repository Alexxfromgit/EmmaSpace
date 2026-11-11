using EmmaSpace.Core.Platform;

namespace EmmaSpace.Core.Tests;

public sealed class MacPrivacyTests
{
    private const string Home = "/Users/emma";

    [Theory]
    [InlineData("/")]
    [InlineData("/Users")]
    [InlineData("/Users/emma")]
    [InlineData("/Users/emma/")]
    [InlineData("/users/EMMA")] // APFS folder names are case-insensitive by default
    [InlineData("/Users/emma/Library")]
    [InlineData("/Users/emma/Library/Containers/com.apple.mail")]
    [InlineData("/Library")]
    [InlineData("/System/Volumes/Data")]
    public void Scans_that_reach_private_data_get_the_hint(string root)
    {
        Assert.True(MacPrivacy.MayCoverProtectedData(root, Home));
    }

    [Theory]
    [InlineData("/Applications")]
    [InlineData("/Users/emma/Projects")]
    [InlineData("/Users/emma/Librarian")] // a name that only starts like Library
    [InlineData("/Users/emmaline")]
    [InlineData("/Volumes/Backup")]
    [InlineData("")]
    public void Other_scans_do_not(string root)
    {
        Assert.False(MacPrivacy.MayCoverProtectedData(root, Home));
    }

    [Fact]
    public void Full_Disk_Access_is_known_on_macOS_only()
    {
        var granted = MacPrivacy.HasFullDiskAccess();
        if (OperatingSystem.IsMacOS()) Assert.NotNull(granted);
        else Assert.Null(granted);
    }
}
