using EmmaSpace.Core.Scanning;

namespace EmmaSpace.Core.Tests;

public class ScanPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "emmaspace-scanpaths");

    [Fact]
    public void The_scanned_folder_matches_itself_with_a_trailing_separator_or_stray_spaces()
    {
        Assert.True(ScanPaths.AreSame(Root, Root));
        Assert.True(ScanPaths.AreSame(Root + Path.DirectorySeparatorChar, Root));
        Assert.True(ScanPaths.AreSame("  " + Root + " ", Root));
    }

    [Fact]
    public void Another_folder_or_an_empty_box_does_not_match()
    {
        Assert.False(ScanPaths.AreSame(Path.Combine(Root, "child"), Root));
        Assert.False(ScanPaths.AreSame(Root + "2", Root));
        Assert.False(ScanPaths.AreSame("", Root));
        Assert.False(ScanPaths.AreSame("   ", Root));
        Assert.False(ScanPaths.AreSame(null, Root));
    }

    [Fact]
    public void Case_matters_only_on_linux()
    {
        Assert.Equal(!OperatingSystem.IsLinux(), ScanPaths.AreSame(Root.ToUpperInvariant(), Root.ToLowerInvariant()));
    }

    [Fact]
    public void A_bare_drive_letter_names_the_drive_root()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive letters exist only on Windows.");
        Assert.True(ScanPaths.AreSame("C:", @"C:\"));
        Assert.True(ScanPaths.AreSame(@"c:\", @"C:\"));
        Assert.False(ScanPaths.AreSame(@"D:\", @"C:\"));
    }
}
