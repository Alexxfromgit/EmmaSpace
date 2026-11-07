using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using EmmaSpace.App.Views;

namespace EmmaSpace.App.Services;

public interface IDialogService
{
    Task<string?> PickFolderAsync();

    Task<string?> OpenFileAsync(string title, string filterName, string extension);

    Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string extension);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);

    Task ShowErrorAsync(string title, string message);

    Task SetClipboardTextAsync(string text);
}

public sealed class DialogService(Window owner) : IDialogService
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
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = ["*" + extension] }],
        });
        return file?.TryGetLocalPath();
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false) =>
        new MessageDialog(title, message, confirmText, "Cancel", destructive).ShowDialog<bool>(owner);

    public Task ShowErrorAsync(string title, string message) =>
        new MessageDialog(title, message, "OK", null, false).ShowDialog<bool>(owner);

    public async Task SetClipboardTextAsync(string text)
    {
        if (owner.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
