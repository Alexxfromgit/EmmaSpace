namespace EmmaSpace.Core.Platform;

/// <summary>The app's colour theme, chosen in Settings.</summary>
public enum AppTheme
{
    /// <summary>Follows the operating system's light or dark setting.</summary>
    System,
    Light,
    Dark,
}

public static class AppThemes
{
    /// <summary>
    /// Reads the theme saved in the settings file. Anything missing or unknown (an older or hand-edited file)
    /// means <see cref="AppTheme.System"/>, so a bad value never stops the rest of the settings from loading.
    /// </summary>
    public static AppTheme Parse(string? value) =>
        Enum.TryParse<AppTheme>(value?.Trim(), ignoreCase: true, out var theme) && Enum.IsDefined(theme) ? theme : AppTheme.System;

    /// <summary>The value saved in the settings file: the theme's name.</summary>
    public static string ToSetting(AppTheme theme) => theme.ToString();
}
