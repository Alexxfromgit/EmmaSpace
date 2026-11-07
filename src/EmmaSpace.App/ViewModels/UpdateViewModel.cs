using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmaSpace.App.Services;

namespace EmmaSpace.App.ViewModels;

/// <summary>The version label in the status bar, "Check for updates", and the "update ready" banner.</summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private readonly UpdateService _updates;
    private readonly SettingsService _settings;
    private readonly ElevationService _elevation;

    public UpdateViewModel(UpdateService updates, SettingsService settings, ElevationService elevation)
    {
        _updates = updates;
        _settings = settings;
        _elevation = elevation;
        if (updates.IsInstalled && updates.PendingVersion is { } pending) ReadyVersion = pending;
    }

    public string VersionText { get; } = "v" + UpdateService.CurrentVersion;

    /// <summary>Installed copies update themselves; a development or portable build cannot.</summary>
    public bool CanUpdate => _updates.IsInstalled;

    public bool AutoCheck
    {
        get => _settings.Current.CheckForUpdates;
        set
        {
            if (_settings.Current.CheckForUpdates == value) return;
            _settings.Current.CheckForUpdates = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Version that is downloaded and installs on restart; null while there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateReady), nameof(HasStatus), nameof(ReadyText))]
    public partial string? ReadyVersion { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateReady), nameof(HasStatus))]
    public partial bool Dismissed { get; private set; }

    /// <summary>Result of a check the user asked for ("You're up to date", errors); null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusText { get; private set; }

    public bool IsUpdateReady => ReadyVersion is not null && !Dismissed;
    public bool HasStatus => StatusText is not null && !IsUpdateReady;

    public string ReadyText => ReadyVersion is null ? ""
        : $"Emma Space {ReadyVersion} is ready. Restart to update, or it installs the next time you start the app.";

    /// <summary>Background check shortly after start, unless turned off or running as administrator.</summary>
    public async Task CheckOnStartupAsync()
    {
        // An elevated copy (the fast NTFS scan) is short-lived; leave updating to the normal one.
        if (!CanUpdate || !AutoCheck || _elevation.IsElevated || ReadyVersion is not null) return;
        await Task.Delay(StartupDelay);
        try
        {
            if (await _updates.CheckAndDownloadAsync() is { } version) ReadyVersion = version;
        }
        catch (Exception ex)
        {
            // Offline, rate-limited or no public feed: an automatic check stays quiet.
            UpdateService.Log($"Automatic update check failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (!CanUpdate)
        {
            StatusText = "Updates are only available in the installed app.";
            return;
        }

        StatusText = "Checking for updates…";
        try
        {
            if (await _updates.CheckAndDownloadAsync() is { } version)
            {
                Dismissed = false;
                ReadyVersion = version;
                StatusText = null;
            }
            else
            {
                StatusText = ReadyVersion is null ? $"Emma Space {UpdateService.CurrentVersion} is the latest version." : null;
            }
        }
        catch (Exception ex)
        {
            UpdateService.Log($"Update check failed: {ex}");
            StatusText = "Couldn't check for updates. Check your internet connection and try again.";
        }
    }

    [RelayCommand]
    private void RestartToUpdate() => _updates.ApplyAndRestart();

    [RelayCommand]
    private void Dismiss()
    {
        Dismissed = true;
        StatusText = null;
    }

    [RelayCommand]
    private void ShowReleaseNotes()
    {
        var url = ReadyVersion is { } v ? UpdateService.ReleaseNotesUrl(v) : UpdateService.ReleaseNotesUrl(UpdateService.CurrentVersion);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateService.Log($"Couldn't open {url}: {ex.Message}");
        }
    }
}
