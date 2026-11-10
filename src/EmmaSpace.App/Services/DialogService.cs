using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using EmmaSpace.App.ViewModels;
using EmmaSpace.App.Views;
using EmmaSpace.Core.Platform;

namespace EmmaSpace.App.Services;

public interface IDialogService
{
    Task<string?> PickFolderAsync();

    Task<string?> OpenFileAsync(string title, string filterName, string extension);

    Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string extension);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);

    Task ShowErrorAsync(string title, string message);

    Task SetClipboardTextAsync(string text);

    /// <summary>Opens the Settings window, unless a dialog (Settings itself, or a question) is already open.</summary>
    Task ShowSettingsAsync();
}

public sealed class DialogService(Window owner, Func<SettingsViewModel> settings) : IDialogService
{
    public async Task<string?> PickFolderAsync()
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to analyze",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> OpenFileAsync(string title, string filterName, string extension)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = ["*" + extension] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string extension)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = SaveFileName.Suggestion(suggestedName, extension),
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = ["*" + extension] }],
            ShowOverwritePrompt = true, // the GTK dialog (Linux without a desktop portal) skips it otherwise
        });
        if (file?.TryGetLocalPath() is not { } picked) return null;

        // The dialog asked about replacing the name it returned, not the one fixed up here.
        var path = SaveFileName.WithSingleExtension(picked, extension);
        if (path != picked && File.Exists(path) &&
            !await ConfirmAsync("Replace file?", $"\"{Path.GetFileName(path)}\" already exists. Do you want to replace it?",
                "Replace", destructive: true))
            return null;
        return path;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false) =>
        new MessageDialog(title, message, confirmText, "Cancel", destructive, DialogArt.Question).ShowDialog<bool>(owner);

    public Task ShowErrorAsync(string title, string message) =>
        new MessageDialog(title, message, "OK", null, false, DialogArt.Error).ShowDialog<bool>(owner);

    public Task ShowSettingsAsync() =>
        // Ctrl+, and the macOS menu item still work while a dialog is open; don't stack Settings on top of it.
        owner.OwnedWindows.Count > 0 ? Task.CompletedTask
            : new SettingsWindow { DataContext = settings() }.ShowDialog(owner);

    public async Task SetClipboardTextAsync(string text)
    {
        if (owner.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
