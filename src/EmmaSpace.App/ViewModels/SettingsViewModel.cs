using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using EmmaSpace.App.Services;
using EmmaSpace.Core.Platform;

namespace EmmaSpace.App.ViewModels;

/// <summary>The Settings window: the colour theme and automatic update checks.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public SettingsViewModel(SettingsService settings, UpdateViewModel updates)
    {
        _settings = settings;
        Updates = updates;
    }

    /// <summary>"Check automatically", "Check now" and their results, shared with the version menu.</summary>
    public UpdateViewModel Updates { get; }

    public AppTheme Theme
    {
        get => AppThemes.Parse(_settings.Current.Theme);
        set
        {
            if (Theme == value) return;
            _settings.Current.Theme = AppThemes.ToSetting(value);
            _settings.Save();
            Apply(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSystemTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
        }
    }

    // One per radio button; a button only ever sets its own to true.
    public bool IsSystemTheme { get => Theme == AppTheme.System; set { if (value) Theme = AppTheme.System; } }
    public bool IsLightTheme { get => Theme == AppTheme.Light; set { if (value) Theme = AppTheme.Light; } }
    public bool IsDarkTheme { get => Theme == AppTheme.Dark; set { if (value) Theme = AppTheme.Dark; } }

    public static string SystemThemeHint { get; } =
        $"System follows the light or dark appearance of {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "your desktop")}.";

    public static string AutoCheckHint => "Shortly after it starts, Emma Space looks for a new version and downloads it in the background. It installs when you restart.";

    public static string CannotUpdateHint => "This copy can't update itself. Updates come with the installed app from the releases page.";

    /// <summary>Switches every open window to the theme; called at startup and whenever it changes.</summary>
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
