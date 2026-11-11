using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmmaSpace.App.Services;

public sealed class AppSettings
{
    public string? LastPath { get; set; }
    public int DuplicateMinSizeIndex { get; set; } = 1;
    public int OldFilesAgeIndex { get; set; } = 1;
    public bool OldFilesByAccess { get; set; }
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Remember file hashes between duplicate searches, so unchanged files are not read again.</summary>
    public bool UseHashCache { get; set; } = true;

    /// <summary>"System", "Light" or "Dark"; read with <see cref="EmmaSpace.Core.Platform.AppThemes.Parse"/>.</summary>
    public string? Theme { get; set; }

    /// <summary>Columns the user hid, by table key (see <see cref="EmmaSpace.App.Controls.ColumnPicker"/>) and then header text.</summary>
    public Dictionary<string, List<string>>? HiddenColumns { get; set; }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Small JSON settings file in the per-user application data folder.</summary>
public sealed class SettingsService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EmmaSpace", "settings.json");

    public SettingsService() => Current = Load();

    public AppSettings Current { get; }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
        }
        catch (IOException)
        {
            // Settings are a convenience; never fail the app over them.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }
}
