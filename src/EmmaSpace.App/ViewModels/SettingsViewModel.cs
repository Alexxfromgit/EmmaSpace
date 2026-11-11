using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmaSpace.App.Services;
using EmmaSpace.Core.Platform;

namespace EmmaSpace.App.ViewModels;

/// <summary>The Settings window: the colour theme, automatic update checks and the duplicate search's memory.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly HashCacheService _hashCache;

    public SettingsViewModel(SettingsService settings, UpdateViewModel updates, HashCacheService hashCache)
    {
        _settings = settings;
        _hashCache = hashCache;
        Updates = updates;
    }

    /// <summary>"Remember file fingerprints": duplicate searches skip files unchanged since an earlier one.</summary>
    public bool RememberFileHashes
    {
        get => _hashCache.Enabled;
        set
        {
            if (_hashCache.Enabled == value) return;
            _hashCache.Enabled = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    public partial string? HashCacheStatus { get; private set; }

    // Kept in step with what FileHashCache and DuplicateFinder.ReadPlan actually do.
    public static string RememberHashesHint { get; } =
        "Searching again then skips files of 1 MB and up that have not changed since, as the system reports it" +
        (OperatingSystem.IsWindows() ? " (on drives that keep a change journal, such as the system drive)" : "") +
        ". Smaller files, files changed in the last minute and files on network drives are always read. " +
        $"Moving a copy to the {FileActions.TrashName} from the Duplicates tab first compares it byte by byte with another copy.";

    [RelayCommand]
    private async Task ClearHashCacheAsync()
    {
        await _hashCache.ClearAsync();
        HashCacheStatus = "Forgotten. The next search reads every file again.";
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
