// Drop zone: files dropped on the Home page open the donut (click mode) at the drop position.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Filee.App.Services;
using Microsoft.Extensions.Logging;

namespace Filee.App.Views.Pages;

public partial class HomePage : UserControl
{
    /// <summary>
    /// The first drag over the drop zone of this run is logged: a log without it after "drops don't work" means the
    /// drag never reached Filee (Windows refuses drops from Explorer into a Filee running as administrator).
    /// </summary>
    private static int _dragEnterLogged;

    public HomePage()
    {
        InitializeComponent();
        DragDrop.AddDragEnterHandler(DropZone, (_, e) =>
        {
            DropZone.Classes.Add("over");
            var files = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
            if (Interlocked.Exchange(ref _dragEnterLogged, 1) == 0)
                AppHost.Get<ILogger<HomePage>>().LogInformation("First drag over the drop zone (files: {Files})", files);
        });
        DragDrop.AddDragOverHandler(DropZone, (_, e) =>
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDragLeaveHandler(DropZone, (_, _) => DropZone.Classes.Remove("over"));
        DragDrop.AddDropHandler(DropZone, OnDrop);
        // Two columns on a wide window; on a narrow one the side column moves below and the tiles wrap.
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 900;
            Columns.ColumnDefinitions = ColumnDefinitions.Parse(narrow ? "*,0,0" : e.NewSize.Width < 1200 ? "*,16,300" : "*,18,380");
            // Two quick tools side by side only where their captions fit.
            Columns.RowDefinitions = RowDefinitions.Parse(narrow ? "Auto,Auto" : "Auto");
            Grid.SetColumn(Side, narrow ? 0 : 2);
            Grid.SetRow(Side, narrow ? 1 : 0);
            Side.Margin = new Thickness(0, narrow ? 16 : 0, 0, 0);
            Tools.Columns = e.NewSize.Width is < 760 or (>= 900 and < 1180) ? 3 : 6;
        };
    }

    private async void OnBrowseFiles(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ShowDonut(BrowseFiles, await FileDialogs.OpenFilesAsync(this, multiple: true));

    private async void OnBrowseFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await FileDialogs.OpenFolderAsync(this) is not { } folder)
            return;
        // The files directly in the folder (hidden and system files skipped); the donut offers what fits all of them.
        var files = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true })
            .Select(f => f.FullName).ToList();
        ShowDonut(BrowseFolder, files);
    }

    private void ShowDonut(Control anchor, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return;
        var center = anchor.PointToScreen(new Point(anchor.Bounds.Width / 2, anchor.Bounds.Height / 2));
        AppHost.Get<RadialController>().ShowForFiles(center.X, center.Y, files);
    }

    private ViewModels.MainWindowViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as ViewModels.MainWindowViewModel;

    private void OnEditPdf(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Main?.Navigate("pdf");
    private void OnPdfTools(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Main?.Navigate("pdftools");
    private void OnUtilities(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Main?.Navigate("utilities");
    private void OnAutomation(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Main?.Navigate("watch");

    /// <summary>A recent PDF opens in the editor; other files open in their usual app.</summary>
    private async void OnOpenRecent(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string path || !File.Exists(path))
            return;
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && Main is { } main)
        {
            await main.OpenPdfAsync(path);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppHost.Get<Filee.Core.Platform.IPlatformServices>().RevealInFileManager(path);
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Remove("over");
        var files = (e.DataTransfer.TryGetFiles() ?? [])
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .Where(File.Exists)
            .ToList();
        if (files.Count == 0)
            return;

        e.DragEffects = DragDropEffects.Copy;
        var screenPoint = this.PointToScreen(e.GetPosition(this));
        AppHost.Get<RadialController>().ShowForFiles(screenPoint.X, screenPoint.Y, files);
    }
}
