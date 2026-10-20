// PDF workspace view: native file dialogs for the view model, files dropped to open, thumbnails dragged to reorder, and
// marks drawn / moved on the preview.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;

namespace Filee.App.Views.Pages;

public partial class PdfEditorPage : UserControl
{
    private const double PixelsPerPoint = 96.0 / 72.0;
    private static readonly FilePickerFileType ImageType = new("Images")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.heic"],
        MimeTypes = ["image/*"],
    };

    private static readonly FilePickerFileType CertificateType = new("Certificate") { Patterns = ["*.pfx", "*.p12"] };

    private Point? _drawStart;
    private PdfMarkItem? _moving;
    private PdfMarkItem? _resizing;
    private Point _moveLast;
    private Point? _thumbPress;
    private PdfPageItem? _thumbItem;
    private bool _thumbDragging;
    private int _dropIndex;

    public PdfEditorPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();

        foreach (var target in new Control[] { EmptyDrop, Thumbs })
        {
            DragDrop.SetAllowDrop(target, true);
            DragDrop.AddDragOverHandler(target, (_, e) =>
            {
                e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
                if (target == EmptyDrop)
                    EmptyDrop.Classes.Add("over");
            });
            DragDrop.AddDragLeaveHandler(target, (_, _) => EmptyDrop.Classes.Remove("over"));
            DragDrop.AddDropHandler(target, OnDropFiles);
        }

        Overlay.PointerPressed += OnOverlayPressed;
        Overlay.PointerMoved += OnOverlayMoved;
        Overlay.PointerReleased += OnOverlayReleased;

        Thumbs.AddHandler(PointerPressedEvent, OnThumbPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Thumbs.AddHandler(PointerMovedEvent, OnThumbMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Thumbs.AddHandler(PointerReleasedEvent, OnThumbReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Thumbs.SelectionChanged += (_, _) => Vm?.SelectionChanged();

        PreviewScroller.SizeChanged += (_, e) =>
        {
            if (Vm is not null)
                Vm.ViewportSize = e.NewSize;
        };

        // Edits in the side panel change the selected mark directly; only typing / sliding by the person counts.
        MarkText.TextChanged += (_, _) => { if (MarkText.IsFocused) Vm?.MarkEdited(); };
        MarkLine.ValueChanged += (_, _) => { if (MarkLine.IsPointerOver || MarkLine.IsFocused) Vm?.MarkEdited(); };
        MarkFont.ValueChanged += (_, _) => { if (MarkFont.IsPointerOver || MarkFont.IsFocused) Vm?.MarkEdited(); };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && e.Source is not TextBox && Vm is not null)
            {
                Vm.DeleteSelectionCommand.Execute(null);
                e.Handled = true;
            }
            // Arrow keys nudge the selected mark (Shift: 10 points).
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && e.Source is not TextBox
                     && Vm is { SelectedMark: { IsReplaceText: false } mark } nudger)
            {
                var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
                nudger.BeginMarkChange();
                nudger.MoveMark(mark, e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 },
                    e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 });
                e.Handled = true;
            }
        };
    }

    private PdfEditorPageViewModel? Vm => DataContext as PdfEditorPageViewModel;

    private static ILocalizer Loc => Services.AppHost.Get<ILocalizer>();

    private PdfEditorPageViewModel? _attached;

    private void Attach()
    {
        if (Vm is not { } vm)
            return;
        if (_attached != vm)
        {
            _attached = vm;
            // Opening or closing a document replaces what had focus: keep the shortcuts (Ctrl+O, Ctrl+W, …) working.
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PdfEditorPageViewModel.HasDocument))
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Focus());
            };
        }
        vm.ConfirmAsync = async (title, message) => Owner is { } owner && await PromptWindow.ConfirmAsync(owner, title, message, Loc["pdf.discard"]);
        vm.AskUnsavedAsync = async (title, message) => Owner is { } owner
            ? await PromptWindow.AskUnsavedAsync(owner, title, message)
            : UnsavedChoice.Cancel;
        vm.AskPasswordAsync = async name => Owner is { } owner
            ? await PromptWindow.AskAsync(owner, Loc["pdf.password_title"], Loc.Format("pdf.password_message", name), password: true)
            : null;
        vm.PickPdfsAsync = PickPdfsAsync;
        vm.PickImageAsync = PickImageAsync;
        vm.PickSavePathAsync = PickSavePathAsync;
        vm.CreateSignatureAsync = async () => Owner is { } owner ? await SignatureWindow.CreateAsync(owner) : null;
        vm.PickCertificateAsync = async () => (await FileDialogs.OpenFilesAsync(this, false, CertificateType)).FirstOrDefault();
        vm.RenderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Vm is not null)
            Vm.RenderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        Focus();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private Task<IReadOnlyList<string>> PickPdfsAsync(bool multiple) => FileDialogs.OpenFilesAsync(this, multiple, FileDialogs.Pdf);

    private async Task<string?> PickImageAsync() => (await FileDialogs.OpenFilesAsync(this, false, ImageType)).FirstOrDefault();

    private Task<string?> PickSavePathAsync(string suggested) =>
        FileDialogs.SavePdfAsync(this, suggested, Vm?.DocumentPath is { } document ? Path.GetDirectoryName(document) : null);

    private async void OnDropFiles(object? sender, DragEventArgs e)
    {
        EmptyDrop.Classes.Remove("over");
        if (Vm is not { } vm)
            return;
        var pdf = (e.DataTransfer.TryGetFiles() ?? [])
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .FirstOrDefault(p => File.Exists(p) && p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf is null)
            return;
        e.DragEffects = DragDropEffects.Copy;
        await vm.OpenAsync(pdf);
    }

    // ───────── Drawing and moving marks ─────────

    private double Scale => (Vm?.Zoom ?? 1) * PixelsPerPoint;

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || !e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed)
            return;
        Focus();
        var point = e.GetPosition(Overlay);
        if (vm.Tool == PdfTool.Select)
        {
            // The corner handle of a selected picture or signature resizes it.
            if (vm.SelectedMark is { IsImage: true } selected
                && Math.Abs(point.X - (selected.Left + selected.PixelWidth)) <= 10
                && Math.Abs(point.Y - (selected.Top + selected.PixelHeight)) <= 10)
            {
                vm.BeginMarkChange();
                _resizing = selected;
                e.Pointer.Capture(Overlay);
                e.Handled = true;
                return;
            }
            var mark = vm.MarkAt(point.X / Scale, point.Y / Scale);
            vm.SelectedMark = mark;
            if (mark is not null)
            {
                vm.BeginMarkChange();
                _moving = mark;
                _moveLast = point;
                e.Pointer.Capture(Overlay);
            }
            return;
        }
        _drawStart = point;
        UpdateRubberBand(point, point);
        // A line drag shows a line; a box preview made lines look like they'd fill the box and come out diagonal.
        RubberLine.IsVisible = vm.Tool == PdfTool.Line;
        RubberBand.IsVisible = vm.Tool is not (PdfTool.Note or PdfTool.EditText or PdfTool.Line);
        e.Pointer.Capture(Overlay);
        e.Handled = true;
    }

    private void OnOverlayMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(Overlay);
        if (_resizing is not null && Vm is { } resizer)
            resizer.ResizeMark(_resizing, point.X / Scale - _resizing.X);
        else if (_moving is not null && Vm is { } vm)
        {
            vm.MoveMark(_moving, (point.X - _moveLast.X) / Scale, (point.Y - _moveLast.Y) / Scale);
            _moveLast = point;
        }
        else if (_drawStart is { } start)
            UpdateRubberBand(start, point);
    }

    private async void OnOverlayReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_resizing is not null)
        {
            _resizing = null;
            return;
        }
        if (_moving is not null)
        {
            _moving = null;
            return;
        }
        if (_drawStart is not { } start || Vm is not { } vm)
            return;
        _drawStart = null;
        RubberBand.IsVisible = false;
        RubberLine.IsVisible = false;
        var end = e.GetPosition(Overlay);
        var scale = Scale;
        if (vm.Tool == PdfTool.Line)
            await vm.AddMarkAsync(vm.Tool, start.X / scale, start.Y / scale, (end.X - start.X) / scale, (end.Y - start.Y) / scale);
        else
            await vm.AddMarkAsync(vm.Tool, Math.Min(start.X, end.X) / scale, Math.Min(start.Y, end.Y) / scale,
                Math.Abs(end.X - start.X) / scale, Math.Abs(end.Y - start.Y) / scale);
        // One shape per tool use, then back to selecting (the new mark is selected for editing). Switching the tool
        // clears the selection, so take the new mark first.
        var added = vm.SelectedMark;
        vm.SelectedTool = vm.Tools[0];
        vm.SelectedMark = added;
        if (added?.HasText == true)
        {
            MarkText.Focus();
            MarkText.SelectAll();
        }
    }

    private void UpdateRubberBand(Point a, Point b)
    {
        Canvas.SetLeft(RubberBand, Math.Min(a.X, b.X));
        Canvas.SetTop(RubberBand, Math.Min(a.Y, b.Y));
        RubberBand.Width = Math.Abs(b.X - a.X);
        RubberBand.Height = Math.Abs(b.Y - a.Y);
        RubberLine.StartPoint = a;
        RubberLine.EndPoint = b;
    }

    // ───────── Reordering thumbnails by dragging ─────────

    private void OnThumbPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Thumbs).Properties.IsLeftButtonPressed)
            return;
        _thumbPress = e.GetPosition(Thumbs);
        _thumbItem = (e.Source as Control)?.DataContext as PdfPageItem;
        _thumbDragging = false;
    }

    private void OnThumbMoved(object? sender, PointerEventArgs e)
    {
        if (_thumbPress is not { } press || _thumbItem is null)
            return;
        var point = e.GetPosition(Thumbs);
        if (!_thumbDragging && Math.Abs(point.Y - press.Y) < 8)
            return;
        _thumbDragging = true;
        _dropIndex = DropIndexAt(point.Y, out var markerY);
        DropMarker.Margin = new Thickness(6, Math.Max(0, markerY - 1.5), 6, 0);
        DropMarker.IsVisible = true;
    }

    private void OnThumbReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dragging = _thumbDragging;
        var item = _thumbItem;
        _thumbPress = null;
        _thumbItem = null;
        _thumbDragging = false;
        DropMarker.IsVisible = false;
        if (!dragging || item is null || Vm is not { } vm)
            return;
        var moving = item.IsSelected ? vm.Pages.Where(p => p.IsSelected).ToList() : [item];
        // Index among the pages that stay.
        var target = vm.Pages.Take(_dropIndex).Count(p => !moving.Contains(p));
        vm.MovePages(moving, target);
        e.Handled = true;
    }

    /// <summary>The list position a drop at <paramref name="y"/> goes to, and where to draw the marker.</summary>
    private int DropIndexAt(double y, out double markerY)
    {
        var count = Vm?.Pages.Count ?? 0;
        markerY = 0;
        for (var i = 0; i < count; i++)
        {
            if (Thumbs.ContainerFromIndex(i) is not { } container || container.TranslatePoint(new Point(0, 0), Thumbs) is not { } top)
                continue;
            if (y < top.Y + container.Bounds.Height / 2)
            {
                markerY = top.Y;
                return i;
            }
            markerY = top.Y + container.Bounds.Height;
        }
        return count;
    }
}
