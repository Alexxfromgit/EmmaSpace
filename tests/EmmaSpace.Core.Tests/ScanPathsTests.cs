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
    public void A_folder_whose_name_ends_in_a_space_is_not_the_one_without_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows drops trailing spaces from names itself.");
        var parent = Directory.CreateTempSubdirectory("emmaspace-scanpaths-").FullName;
        try
        {
            var plain = Directory.CreateDirectory(Path.Combine(parent, "a")).FullName;
            var spaced = Path.Combine(parent, "a ");
            Assert.True(ScanPaths.AreSame(spaced, plain)); // no such folder: the space is a typo
            Directory.CreateDirectory(spaced);
            Assert.Equal(spaced, ScanPaths.FromTyped(spaced));
            Assert.False(ScanPaths.AreSame(spaced, plain)); // a real folder of its own
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
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
