using EmmaSpace.Core.Platform;

namespace EmmaSpace.Core.Tests;

public class AppThemeTests
{
    [Theory]
    [InlineData("System", AppTheme.System)]
    [InlineData("Light", AppTheme.Light)]
    [InlineData("Dark", AppTheme.Dark)]
    [InlineData("dark", AppTheme.Dark)]
    [InlineData(" LIGHT ", AppTheme.Light)]
    public void Parse_reads_saved_names(string value, AppTheme expected) => Assert.Equal(expected, AppThemes.Parse(value));

    [Theory]
    [InlineData(null)]          // settings saved before there was a theme
    [InlineData("")]
    [InlineData("Purple")]      // hand-edited or from a newer version
    [InlineData("7")]           // a number that isn't a theme
    [InlineData("Light, Dark")] // flag syntax Enum.TryParse would otherwise accept
    public void Parse_falls_back_to_system(string? value) => Assert.Equal(AppTheme.System, AppThemes.Parse(value));

    [Theory]
    [InlineData(AppTheme.System)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void Saved_value_reads_back(AppTheme theme) => Assert.Equal(theme, AppThemes.Parse(AppThemes.ToSetting(theme)));
}
