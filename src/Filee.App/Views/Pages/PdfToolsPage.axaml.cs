using Avalonia.Controls;
using Filee.App.ViewModels.Pages;

namespace Filee.App.Views.Pages;

public partial class PdfToolsPage : UserControl
{
    /// <summary>Pictures Windows OCR decodes.</summary>
    private static readonly Avalonia.Platform.Storage.FilePickerFileType OcrPictures = new("Pictures")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tif", "*.tiff"],
        MimeTypes = ["image/*"],
    };

    public PdfToolsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not PdfToolsPageViewModel vm)
                return;
            vm.PickPdfsAsync = multiple => FileDialogs.OpenFilesAsync(this, multiple, FileDialogs.Pdf);
            vm.PickSavePathAsync = name => FileDialogs.SavePdfAsync(this, name);
            vm.PickOcrFilesAsync = () => FileDialogs.OpenFilesAsync(this, true, FileDialogs.Pdf, OcrPictures);
            _ = vm.LoadOcrLanguagesAsync();
        };
        // One column of cards in a narrow window.
        SizeChanged += (_, e) => Cards.Columns = e.NewSize.Width < 900 ? 1 : 2;
    }
}
