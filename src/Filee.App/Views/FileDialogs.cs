// Native open / save dialogs for the workspace pages.

using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Filee.App.Views;

internal static class FileDialogs
{
    public static readonly FilePickerFileType Pdf = new("PDF") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] };

    public static async Task<IReadOnlyList<string>> OpenFilesAsync(Control owner, bool multiple, params FilePickerFileType[] types)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
            return [];
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = multiple,
            FileTypeFilter = types.Length == 0 ? null : types,
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public static async Task<string?> OpenFolderAsync(Control owner)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
            return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Save As for a PDF; the dialog asks before replacing an existing file.</summary>
    public static async Task<string?> SavePdfAsync(Control owner, string suggestedName, string? folder = null)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
            return null;
        var start = folder is null ? null : await top.StorageProvider.TryGetFolderFromPathAsync(folder);
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            DefaultExtension = "pdf",
            FileTypeChoices = [Pdf],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }
}
