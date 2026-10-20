// PDF workspace: pages as thumbnails, a preview with marks drawn on it, page operations with undo / redo, and Save /
// Save As through PdfEditor (Filee.Engines). The opened files are only read; saving always writes a new file unless the
// person picks the original in the Save As dialog (which asks before replacing it).

using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Engines.Pdf;

namespace Filee.App.ViewModels.Pages;

/// <summary>Workspace pages: full width, and kept while the main window is open (unsaved work survives switching pages).</summary>
public interface IWorkspacePage;

/// <summary>A signature to place: a transparent PNG and its size on the page in points.</summary>
public sealed record SignatureChoice(string Path, double WidthPoints, double HeightPoints);

/// <summary>The answer to "Save changes?": save, close without saving, or keep the document open.</summary>
public enum UnsavedChoice { Save, DontSave, Cancel }

public enum PdfTool { Select, EditText, Highlight, Underline, StrikeOut, Note, FreeText, Rectangle, Ellipse, Line, Image, Signature }

/// <summary>Where page numbers go, with its name in the UI language.</summary>
public sealed record PositionOption(PdfStampPosition Value, string Title)
{
    public override string ToString() => Title;
}

/// <summary>A drawing tool of the toolbar.</summary>
public sealed record PdfToolOption(PdfTool Tool, Geometry? Icon, string Tip);

/// <summary>A mark on the previewed page; geometry in points of the shown page, <see cref="Left"/> etc. in pixels.</summary>
public sealed partial class PdfMarkItem : ObservableObject
{
    public PdfMarkItem(PdfMark mark)
    {
        Kind = mark.Kind;
        _x = mark.X;
        _y = mark.Y;
        _width = mark.Width;
        _height = mark.Height;
        _color = mark.Color;
        _text = mark.Text;
        _lineWidth = mark.LineWidth;
        _fontSize = mark.FontSize;
        Line = mark.Line;
    }

    /// <summary>For ReplaceText: the page's own line being changed.</summary>
    public PdfTextLine? Line { get; }

    public PdfMarkKind Kind { get; }
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Brush), nameof(FillBrush))] private string _color;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Picture))] private string? _text;
    [ObservableProperty] private double _lineWidth;
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowsResizeHandle))] private bool _isSelected;

    [ObservableProperty] private double _left;
    [ObservableProperty] private double _top;
    [ObservableProperty] private double _pixelWidth;
    [ObservableProperty] private double _pixelHeight;
    [ObservableProperty] private Point _lineStart;
    [ObservableProperty] private Point _lineEnd;
    [ObservableProperty] private double _pixelLineWidth;
    [ObservableProperty] private double _pixelFontSize;
    /// <summary>Text changes: how wide the white cover over the old line is drawn in the preview (a little past it).</summary>
    [ObservableProperty] private double _coverWidth;

    public bool IsBox => Kind is PdfMarkKind.Rectangle;
    public bool IsEllipse => Kind is PdfMarkKind.Ellipse;
    public bool IsLine => Kind is PdfMarkKind.Line;
    public bool IsHighlight => Kind is PdfMarkKind.Highlight;
    public bool IsUnderline => Kind is PdfMarkKind.Underline;
    public bool IsStrikeOut => Kind is PdfMarkKind.StrikeOut;
    public bool IsNote => Kind is PdfMarkKind.Note;
    public bool IsFreeText => Kind is PdfMarkKind.FreeText;
    public bool IsImage => Kind is PdfMarkKind.Image;
    public bool IsReplaceText => Kind is PdfMarkKind.ReplaceText;
    public bool ShowsResizeHandle => IsSelected && IsImage;
    public bool HasText => Kind is PdfMarkKind.Note or PdfMarkKind.FreeText or PdfMarkKind.ReplaceText;
    public bool HasLineWidth => Kind is PdfMarkKind.Rectangle or PdfMarkKind.Ellipse or PdfMarkKind.Line or PdfMarkKind.Underline or PdfMarkKind.StrikeOut;

    public IBrush Brush => Avalonia.Media.Color.TryParse(Color, out var c) ? new SolidColorBrush(c) : Brushes.Gold;
    public IBrush FillBrush => Avalonia.Media.Color.TryParse(Color, out var c) ? new SolidColorBrush(c, 0.4) : Brushes.Gold;

    public Bitmap? Picture
    {
        get
        {
            if (Kind != PdfMarkKind.Image || Text is null || !File.Exists(Text))
                return null;
            try { return new Bitmap(Text); }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or InvalidOperationException) { return null; }
        }
    }

    /// <summary>Pixel geometry for the overlay at <paramref name="scale"/> pixels per point.</summary>
    public void Layout(double scale)
    {
        var size = Kind == PdfMarkKind.Note ? 20 : 0;
        Left = Math.Min(X, X + Width) * scale;
        Top = Math.Min(Y, Y + Height) * scale;
        PixelWidth = Math.Max(1, (size > 0 ? size : Math.Abs(Width)) * scale);
        PixelHeight = Math.Max(1, (size > 0 ? size : Math.Abs(Height)) * scale);
        LineStart = new Point(Width >= 0 ? 0 : -Width * scale, Height >= 0 ? 0 : -Height * scale);
        LineEnd = new Point(Width >= 0 ? Width * scale : 0, Height >= 0 ? Height * scale : 0);
        PixelLineWidth = Math.Max(1, LineWidth * scale);
        PixelFontSize = Math.Max(4, FontSize * scale);
        CoverWidth = PixelWidth + 12;
    }

    public bool Contains(double x, double y)
    {
        var w = Kind == PdfMarkKind.Note ? 20 : Math.Abs(Width);
        var h = Kind == PdfMarkKind.Note ? 20 : Math.Abs(Height);
        var left = Math.Min(X, X + Width) - 4;
        var top = Math.Min(Y, Y + Height) - 4;
        return x >= left && x <= left + w + 8 && y >= top && y <= top + h + 8;
    }

    public PdfMark ToMark(int page) => new(page, Kind, X, Y, Width, Height, Color, Text, LineWidth, FontSize, Line);
}

/// <summary>A form field of the opened document; <see cref="Value"/> is what the person typed or chose.</summary>
public sealed partial class PdfFieldItem(PdfFormField field, Action changed) : ObservableObject
{
    public PdfFormField Field { get; } = field;
    public string Label => Field.Label;
    public IReadOnlyList<string> Options => Field.Options;
    public bool IsText => Field.Kind == PdfFieldKind.Text;
    public bool IsCheckBox => Field.Kind == PdfFieldKind.CheckBox;
    public bool IsChoice => Field.Kind is PdfFieldKind.Choice or PdfFieldKind.Radio;
    public bool IsEditable => !Field.ReadOnly && Field.Kind is not (PdfFieldKind.Signature or PdfFieldKind.Other);
    public bool IsChanged => Value != Field.Value;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsChecked), nameof(IsChanged))] private string _value = field.Value;

    partial void OnValueChanged(string value) => changed();

    public bool IsChecked
    {
        get => Value == "true";
        set => Value = value ? "true" : "false";
    }
}

/// <summary>One page in the thumbnail list.</summary>
public sealed partial class PdfPageItem : ObservableObject
{
    public PdfPageItem(PdfPageRef reference, IEnumerable<PdfMark> marks, Size baseSize)
    {
        _reference = reference;
        _baseSize = baseSize;
        foreach (var mark in marks)
            Marks.Add(new PdfMarkItem(mark));
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Rotation), nameof(SourceName))] private PdfPageRef _reference;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private int _number;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShownSize))] private Size _baseSize;

    public ObservableCollection<PdfMarkItem> Marks { get; } = [];

    public string Key => PdfEditorPageViewModel.KeyOf(Reference);
    public int Rotation => Reference.Rotation;
    public string SourceName => Path.GetFileName(Reference.SourcePath);

    /// <summary>Page size as shown in points: the source's own rotation and the extra one applied.</summary>
    public Size ShownSize => Reference.Rotation is 90 or 270 ? new Size(BaseSize.Height, BaseSize.Width) : BaseSize;
}

public sealed partial class PdfEditorPageViewModel : ObservableObject, IFitsWindowHeight, IWorkspacePage, IDisposable
{
    private const double PixelsPerPoint = 96.0 / 72.0;
    private const double ThumbnailDpi = 22;

    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly RecentFiles _recent;
    private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _thumbnails = [];
    private readonly Dictionary<string, Size> _sizes = [];
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _previewCancel;
    private bool _rendering;

    private sealed record Snapshot(IReadOnlyList<(PdfPageRef Reference, PdfMark[] Marks)> Pages, int Current);

    public PdfEditorPageViewModel(ILocalizer loc, IPlatformServices platform, RecentFiles recent)
    {
        _loc = loc;
        _platform = platform;
        _recent = recent;
        Tools = Enum.GetValues<PdfTool>()
            .Select(t => new PdfToolOption(t, Application.Current?.FindResource($"Icon.{IconOf(t)}") as Geometry, loc[$"pdf.tool.{t}"]))
            .ToList();
        _selectedTool = Tools[0];
        Positions = Enum.GetValues<PdfStampPosition>().Select(p => new PositionOption(p, loc[$"pdf.pos.{p}"])).ToList();
    }

    private static string IconOf(PdfTool tool) => tool switch
    {
        PdfTool.Select => "Pointer",
        PdfTool.FreeText => "Text",
        PdfTool.EditText => "Pencil",
        PdfTool.Signature => "Fingerprint",
        _ => tool.ToString(),
    };

    public IReadOnlyList<PdfToolOption> Tools { get; }

    [ObservableProperty] private PdfToolOption _selectedTool;

    partial void OnSelectedToolChanged(PdfToolOption value)
    {
        if (value is not null)
            SetTool(value.Tool);
    }

    // ───────── Hooks the view fills in (dialogs) ─────────

    /// <summary>Asks a yes / no question: (title, message) → yes.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
    /// <summary>Asks for a password: (file name) → password or null.</summary>
    public Func<string, Task<string?>>? AskPasswordAsync { get; set; }
    /// <summary>Asks where to save: (suggested file name) → path or null.</summary>
    public Func<string, Task<string?>>? PickSavePathAsync { get; set; }
    /// <summary>Asks for PDF files to open or insert.</summary>
    public Func<bool, Task<IReadOnlyList<string>>>? PickPdfsAsync { get; set; }
    /// <summary>Asks for a picture to place.</summary>
    public Func<Task<string?>>? PickImageAsync { get; set; }
    /// <summary>Signature Studio: the signature to place, or null when cancelled.</summary>
    public Func<Task<SignatureChoice?>>? CreateSignatureAsync { get; set; }
    /// <summary>Asks for a certificate file (.pfx / .p12).</summary>
    public Func<Task<string?>>? PickCertificateAsync { get; set; }

    // ───────── State ─────────

    public ObservableCollection<PdfPageItem> Pages { get; } = [];

    /// <summary>The fillable fields of the opened document.</summary>
    public ObservableCollection<PdfFieldItem> Fields { get; } = [];
    public bool HasFields => Fields.Count > 0;

    /// <summary>Recently used PDFs, newest first (refreshed whenever a document opens or closes).</summary>
    public IReadOnlyList<string> RecentDocuments => _recent.Items.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
    public bool HasRecentDocuments => RecentDocuments.Count > 0;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDocument), nameof(Title))] private string? _documentPath;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title))] private string? _savePath;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title))] private bool _isDirty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PageStatus), nameof(HasCurrent))] private PdfPageItem? _currentPage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZoomText))] private double _zoom = 1;
    [ObservableProperty] private Bitmap? _previewBitmap;
    [ObservableProperty] private double _previewWidth;
    [ObservableProperty] private double _previewHeight;
    [ObservableProperty] private double _overlayWidth;
    [ObservableProperty] private double _overlayHeight;
    [ObservableProperty] private PdfTool _tool = PdfTool.Select;
    [ObservableProperty] private string _markColor = "#FFD400";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedMark))] private PdfMarkItem? _selectedMark;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] private string? _message;
    [ObservableProperty] private bool _messageIsError;
    [ObservableProperty] private string? _lastOutput;

    // Document options applied on save.
    [ObservableProperty] private bool _addPageNumbers;
    [ObservableProperty] private string _pageNumberFormat = "{page} / {total}";
    [ObservableProperty] private PdfStampPosition _pageNumberPosition = PdfStampPosition.BottomCenter;
    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _footer = "";
    [ObservableProperty] private string _watermark = "";
    [ObservableProperty] private double _watermarkOpacity = 0.15;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _removeMetadata;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CertificateName))] private string? _certificatePath;
    [ObservableProperty] private string _certificatePassword = "";
    [ObservableProperty] private bool _signWithCertificate;

    public string CertificateName => CertificatePath is null ? _loc["pdf.cert_choose"] : Path.GetFileName(CertificatePath);

    /// <summary>The preview area; while the zoom follows it (until zoomed by hand), the page fits its width.</summary>
    public Size ViewportSize
    {
        get => _viewportSize;
        set
        {
            _viewportSize = value;
            if (_fitsWidth)
                FitWidth();
        }
    }

    private Size _viewportSize = new(800, 600);
    private bool _fitsWidth = true;
    public double RenderScaling { get; set; } = 1;

    public bool HasDocument => DocumentPath is not null;
    public bool HasCurrent => CurrentPage is not null;
    public bool HasSelectedMark => SelectedMark is not null;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string ZoomText => $"{Math.Round(Zoom * 100)}%";

    public string Title => DocumentPath is null ? _loc["pdf.title"]
        : Path.GetFileName(SavePath ?? DocumentPath);

    public string PageStatus
    {
        get
        {
            if (CurrentPage is null)
                return "";
            var selected = Pages.Count(p => p.IsSelected);
            var text = _loc.Format("pdf.page_of", CurrentPage.Number, Pages.Count);
            return selected > 1 ? text + " · " + _loc.Format("pdf.selected", selected) : text;
        }
    }

    public IReadOnlyList<PositionOption> Positions { get; }

    public PositionOption SelectedPosition
    {
        get => Positions.First(p => p.Value == PageNumberPosition);
        set
        {
            if (value is not null)
                PageNumberPosition = value.Value;
        }
    }
    public IReadOnlyList<string> Swatches { get; } = ["#FFD400", "#FF5C5C", "#34C759", "#2F80ED", "#AF52DE", "#000000"];

    internal static string KeyOf(PdfPageRef reference) => $"{reference.SourcePath}|{reference.PageIndex}";

    partial void OnDocumentPathChanged(string? value)
    {
        OnPropertyChanged(nameof(RecentDocuments));
        OnPropertyChanged(nameof(HasRecentDocuments));
    }

    partial void OnZoomChanged(double value) => RefreshPreview();
    partial void OnCurrentPageChanged(PdfPageItem? oldValue, PdfPageItem? newValue)
    {
        // The thumbnail list sets null when its selection is rebuilt (several pages selected, pages moved): the page
        // shown stays.
        if (newValue is null && oldValue is not null && Pages.Contains(oldValue))
        {
            CurrentPage = oldValue;
            return;
        }
        SelectedMark = null;
        RefreshPreview();
    }
    partial void OnSelectedMarkChanged(PdfMarkItem? oldValue, PdfMarkItem? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
    }

    // Changing a document option is an unsaved change.
    partial void OnAddPageNumbersChanged(bool value) => MarkDirty();
    partial void OnPageNumberFormatChanged(string value) => MarkDirty();
    partial void OnPageNumberPositionChanged(PdfStampPosition value)
    {
        OnPropertyChanged(nameof(SelectedPosition));
        MarkDirty();
    }
    partial void OnHeaderChanged(string value) => MarkDirty();
    partial void OnFooterChanged(string value) => MarkDirty();
    partial void OnWatermarkChanged(string value) => MarkDirty();
    partial void OnWatermarkOpacityChanged(double value) => MarkDirty();
    partial void OnPasswordChanged(string value) => MarkDirty();
    partial void OnRemoveMetadataChanged(bool value) => MarkDirty();
    partial void OnSignWithCertificateChanged(bool value) => MarkDirty();

    private void MarkDirty()
    {
        if (HasDocument)
            IsDirty = true;
    }

    public void SelectionChanged() => OnPropertyChanged(nameof(PageStatus));

    private List<PdfPageItem> Selected()
    {
        var selected = Pages.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0 && CurrentPage is not null)
            selected.Add(CurrentPage);
        return selected;
    }

    // ───────── Open / insert ─────────

    [RelayCommand]
    private async Task Open()
    {
        if (PickPdfsAsync is null)
            return;
        var files = await PickPdfsAsync(false);
        if (files.Count > 0)
            await OpenAsync(files[0]);
    }

    [RelayCommand]
    private Task OpenRecent(string path) => OpenAsync(path);

    /// <summary>Opens a PDF in place of the current one (asking first when there are unsaved changes).</summary>
    public async Task<bool> OpenAsync(string path)
    {
        if (!await ResolveUnsavedAsync())
            return false;
        var pages = await LoadSourceAsync(path);
        if (pages is null)
            return false;
        Show(path, pages, 0);
        SavePath = null;
        LastOutput = null;
        Message = null;
        await LoadFieldsAsync(path);
        _recent.Add(path);
        FitWidth();
        return true;
    }

    /// <summary>Shows <paramref name="pages"/> of <paramref name="path"/> as the document, with nothing pending.</summary>
    private void Show(string path, IReadOnlyList<PdfPageRef> pages, int current)
    {
        _undo.Clear();
        _redo.Clear();
        NotifyHistory();
        SelectedMark = null;
        Pages.Clear();
        foreach (var reference in pages)
            Pages.Add(NewItem(reference, []));
        Renumber();
        DocumentPath = path;
        CurrentPage = Pages.Count == 0 ? null : Pages[Math.Clamp(current, 0, Pages.Count - 1)];
        if (CurrentPage is not null)
            CurrentPage.IsSelected = true;
        ResetSaveOptions();
        IsDirty = false;
        _ = RenderThumbnailsAsync();
    }

    /// <summary>Options that are written into the file when saving start empty for each document.</summary>
    private void ResetSaveOptions()
    {
        AddPageNumbers = false;
        Header = "";
        Footer = "";
        Watermark = "";
        Password = "";
        RemoveMetadata = false;
        SignWithCertificate = false;
    }

    /// <summary>
    /// After the document was saved to <paramref name="path"/>: that file becomes the document, so what was saved
    /// (text changes, marks, stamps, form values) is part of it and the next save doesn't apply it a second time.
    /// </summary>
    private async Task ReloadSavedAsync(string path, string? password)
    {
        var current = CurrentPage is null ? 0 : Pages.IndexOf(CurrentPage);
        if (password is not null)
            _passwords[path] = password;
        else
            _passwords.Remove(path);
        // The file changed: drop what was rendered from its old content.
        foreach (var key in _thumbnails.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            _thumbnails.Remove(key);
        foreach (var key in _sizes.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            _sizes.Remove(key);
        var pages = await LoadSourceAsync(path);
        if (pages is null)
            return; // the message says why; the edits stay on screen
        Show(path, pages, current);
        await LoadFieldsAsync(path);
        RefreshPreview();
    }

    [RelayCommand]
    private async Task InsertFromFile()
    {
        if (!HasDocument || PickPdfsAsync is null)
            return;
        var files = await PickPdfsAsync(true);
        if (files.Count == 0)
            return;
        var at = Selected().Select(p => Pages.IndexOf(p)).DefaultIfEmpty(Pages.Count - 1).Max() + 1;
        var added = new List<PdfPageRef>();
        foreach (var file in files)
        {
            var pages = await LoadSourceAsync(file);
            if (pages is null)
                return;
            added.AddRange(pages);
        }
        Change(() =>
        {
            foreach (var (reference, i) in added.Select((r, i) => (r, i)))
                Pages.Insert(at + i, NewItem(reference, []));
        });
        _ = RenderThumbnailsAsync();
        Info(_loc.Format("pdf.inserted", added.Count));
    }

    /// <summary>Reads a source (asking for its password when it needs one); null when it can't be opened.</summary>
    private async Task<IReadOnlyList<PdfPageRef>?> LoadSourceAsync(string path)
    {
        var password = _passwords.GetValueOrDefault(path);
        while (true)
        {
            try
            {
                IsBusy = true;
                var pages = await Task.Run(() => PdfEditor.AllPages(path, password));
                _bytes[path] = await File.ReadAllBytesAsync(path);
                if (password is not null)
                    _passwords[path] = password;
                return pages;
            }
            catch (PdfEditException ex) when (ex.NeedsPassword && AskPasswordAsync is not null)
            {
                IsBusy = false;
                password = await AskPasswordAsync(Path.GetFileName(path));
                if (password is null)
                    return null;
            }
            catch (Exception ex) when (ex is PdfEditException or IOException or UnauthorizedAccessException)
            {
                Error(ex.Message);
                return null;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    private async Task LoadFieldsAsync(string path)
    {
        Fields.Clear();
        try
        {
            var password = _passwords.GetValueOrDefault(path);
            foreach (var field in await Task.Run(() => PdfContentEditing.ReadFields(path, password)))
                Fields.Add(new PdfFieldItem(field, MarkDirty));
        }
        catch (PdfEditException)
        {
            // A form that can't be read leaves the rest of the editor working.
        }
        OnPropertyChanged(nameof(HasFields));
    }

    private PdfPageItem NewItem(PdfPageRef reference, IEnumerable<PdfMark> marks)
    {
        var key = KeyOf(reference);
        var item = new PdfPageItem(reference, marks, _sizes.GetValueOrDefault(key, new Size(595, 842)));
        item.Thumbnail = _thumbnails.GetValueOrDefault(key);
        return item;
    }

    private void Renumber()
    {
        for (var i = 0; i < Pages.Count; i++)
            Pages[i].Number = i + 1;
        OnPropertyChanged(nameof(PageStatus));
    }

    // ───────── Rendering ─────────

    private async Task RenderThumbnailsAsync()
    {
        if (_rendering)
            return;
        _rendering = true;
        try
        {
            while (Pages.FirstOrDefault(p => p.Thumbnail is null) is { } item)
            {
                var reference = item.Reference;
                var key = KeyOf(reference);
                var rendered = await RenderAsync(reference, ThumbnailDpi);
                if (rendered is null)
                    return;
                var bitmap = ToBitmap(rendered);
                _thumbnails[key] = bitmap;
                _sizes[key] = new Size(rendered.WidthPoints, rendered.HeightPoints);
                foreach (var page in Pages.Where(p => p.Key == key))
                {
                    page.Thumbnail = bitmap;
                    page.BaseSize = _sizes[key];
                }
                if (CurrentPage?.Key == key)
                    RefreshPreview();
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private async Task<RenderedPage?> RenderAsync(PdfPageRef reference, double dpi, CancellationToken cancellationToken = default)
    {
        if (!_bytes.TryGetValue(reference.SourcePath, out var bytes))
            return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, cancellationToken);
        try
        {
            await _renderGate.WaitAsync(linked.Token);
            try
            {
                var password = _passwords.GetValueOrDefault(reference.SourcePath);
                return await Task.Run(() => PdfPageRenderer.Render(bytes, reference.PageIndex, dpi, password), linked.Token);
            }
            finally
            {
                _renderGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error(_loc.Format("pdf.render_failed", ex.Message));
            return null;
        }
    }

    internal static Bitmap ToBitmap(RenderedPage page)
    {
        var handle = GCHandle.Alloc(page.Pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(),
                new PixelSize(page.Width, page.Height), new Vector(96, 96), page.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Re-renders the preview for the current page and zoom (the previous request is cancelled).</summary>
    private void RefreshPreview()
    {
        var item = CurrentPage;
        LayoutMarks();
        if (item is null)
        {
            PreviewBitmap = null;
            return;
        }
        var scale = Zoom * PixelsPerPoint;
        PreviewWidth = item.BaseSize.Width * scale;
        PreviewHeight = item.BaseSize.Height * scale;
        OverlayWidth = item.ShownSize.Width * scale;
        OverlayHeight = item.ShownSize.Height * scale;

        _previewCancel?.Cancel();
        var cancel = _previewCancel = new CancellationTokenSource();
        // Sharp on high-DPI screens, but never more than ~40 megapixels.
        var dpi = 72 * scale * RenderScaling;
        var pixels = item.BaseSize.Width * item.BaseSize.Height * dpi * dpi / (72 * 72);
        if (pixels > 40_000_000)
            dpi *= Math.Sqrt(40_000_000 / pixels);
        var reference = item.Reference;
        Dispatcher.UIThread.Post(async () =>
        {
            var rendered = await RenderAsync(reference, dpi, cancel.Token);
            if (rendered is null || cancel.IsCancellationRequested || CurrentPage?.Key != KeyOf(reference))
                return;
            PreviewBitmap = ToBitmap(rendered);
            var key = KeyOf(reference);
            var size = new Size(rendered.WidthPoints, rendered.HeightPoints);
            if (_sizes.GetValueOrDefault(key) != size)
            {
                _sizes[key] = size;
                foreach (var page in Pages.Where(p => p.Key == key))
                    page.BaseSize = size;
                RefreshPreview();
            }
        });
    }

    private void LayoutMarks()
    {
        var scale = Zoom * PixelsPerPoint;
        foreach (var mark in CurrentPage?.Marks ?? [])
            mark.Layout(scale);
    }

    // ───────── Zoom and navigation ─────────

    [RelayCommand]
    private void ZoomIn()
    {
        _fitsWidth = false;
        Zoom = Math.Min(6, Math.Round(Zoom * 1.25, 2));
    }

    [RelayCommand]
    private void ZoomOut()
    {
        _fitsWidth = false;
        Zoom = Math.Max(0.1, Math.Round(Zoom / 1.25, 2));
    }

    [RelayCommand]
    private void FitWidth()
    {
        _fitsWidth = true;
        if (CurrentPage is { } page)
            Zoom = Math.Clamp((ViewportSize.Width - 48) / (page.ShownSize.Width * PixelsPerPoint), 0.1, 6);
    }

    [RelayCommand]
    private void FitPage()
    {
        _fitsWidth = false;
        if (CurrentPage is { } page)
            Zoom = Math.Clamp(Math.Min((ViewportSize.Width - 48) / (page.ShownSize.Width * PixelsPerPoint),
                (ViewportSize.Height - 48) / (page.ShownSize.Height * PixelsPerPoint)), 0.1, 6);
    }

    [RelayCommand] private void NextPage() => GoTo(CurrentPage is null ? 0 : Pages.IndexOf(CurrentPage) + 1);
    [RelayCommand] private void PreviousPage() => GoTo(CurrentPage is null ? 0 : Pages.IndexOf(CurrentPage) - 1);

    public void GoTo(int index)
    {
        if (Pages.Count == 0)
            return;
        index = Math.Clamp(index, 0, Pages.Count - 1);
        foreach (var page in Pages)
            page.IsSelected = false;
        Pages[index].IsSelected = true;
        CurrentPage = Pages[index];
        SelectionChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var page in Pages)
            page.IsSelected = true;
        SelectionChanged();
    }

    // ───────── Page operations (undoable) ─────────

    private Snapshot Take() => new(Pages.Select(p => (p.Reference, p.Marks.Select(m => m.ToMark(0)).ToArray())).ToList(),
        CurrentPage is null ? -1 : Pages.IndexOf(CurrentPage));

    private void Restore(Snapshot snapshot)
    {
        Pages.Clear();
        foreach (var (reference, marks) in snapshot.Pages)
            Pages.Add(NewItem(reference, marks));
        Renumber();
        CurrentPage = snapshot.Current >= 0 && snapshot.Current < Pages.Count ? Pages[snapshot.Current] : Pages.FirstOrDefault();
        if (CurrentPage is not null)
            CurrentPage.IsSelected = true;
        RefreshPreview();
    }

    /// <summary>Runs an edit with an undo step; the document becomes unsaved.</summary>
    private void Change(Action edit)
    {
        if (!HasDocument)
            return;
        _undo.Push(Take());
        _redo.Clear();
        edit();
        Renumber();
        if (CurrentPage is null || !Pages.Contains(CurrentPage))
            CurrentPage = Pages.FirstOrDefault();
        IsDirty = true;
        NotifyHistory();
        RefreshPreview();
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0)
            return;
        _redo.Push(Take());
        Restore(_undo.Pop());
        IsDirty = true;
        NotifyHistory();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0)
            return;
        _undo.Push(Take());
        Restore(_redo.Pop());
        IsDirty = true;
        NotifyHistory();
    }

    [RelayCommand] private void RotateRight() => Rotate(90);
    [RelayCommand] private void RotateLeft() => Rotate(-90);

    private void Rotate(int degrees)
    {
        var selected = Selected();
        if (selected.Count == 0)
            return;
        Change(() =>
        {
            foreach (var page in selected)
            {
                var shown = page.ShownSize;
                foreach (var mark in page.Marks)
                    TurnMark(mark, degrees, shown);
                page.Reference = page.Reference with { Rotation = PdfEditor.NormalizeRotation(page.Reference.Rotation + degrees) };
            }
        });
    }

    /// <summary>Moves a mark with its page when the page turns by ±90° (shown size before turning).</summary>
    internal static void TurnMark(PdfMarkItem mark, int degrees, Size shown)
    {
        (double X, double Y) Turn(double x, double y) => degrees > 0 ? (shown.Height - y, x) : (y, shown.Width - x);
        if (mark.Kind == PdfMarkKind.Note)
        {
            var (nx, ny) = Turn(mark.X + 10, mark.Y + 10);
            (mark.X, mark.Y) = (nx - 10, ny - 10);
            return;
        }
        var start = Turn(mark.X, mark.Y);
        var end = Turn(mark.X + mark.Width, mark.Y + mark.Height);
        mark.X = start.X;
        mark.Y = start.Y;
        mark.Width = end.X - start.X;
        mark.Height = end.Y - start.Y;
    }

    [RelayCommand]
    private void DeletePages()
    {
        var selected = Selected();
        if (selected.Count == 0)
            return;
        if (selected.Count == Pages.Count)
        {
            Error(_loc["pdf.cant_delete_all"]);
            return;
        }
        var first = selected.Min(Pages.IndexOf);
        Change(() =>
        {
            foreach (var page in selected)
                Pages.Remove(page);
            CurrentPage = Pages[Math.Min(first, Pages.Count - 1)];
            CurrentPage.IsSelected = true;
        });
    }

    [RelayCommand]
    private void DuplicatePages()
    {
        var selected = Selected().OrderBy(Pages.IndexOf).ToList();
        if (selected.Count == 0)
            return;
        Change(() =>
        {
            var at = Pages.IndexOf(selected[^1]) + 1;
            foreach (var (page, i) in selected.Select((p, i) => (p, i)))
                Pages.Insert(at + i, NewItem(page.Reference, page.Marks.Select(m => m.ToMark(0))));
        });
    }

    [RelayCommand] private void MoveUp() => MoveSelected(-1);
    [RelayCommand] private void MoveDown() => MoveSelected(1);

    private void MoveSelected(int step)
    {
        var selected = Selected().OrderBy(Pages.IndexOf).ToList();
        if (selected.Count == 0)
            return;
        var target = step < 0 ? Pages.IndexOf(selected[0]) - 1 : Pages.IndexOf(selected[^1]) + 2 - selected.Count;
        if (target < 0 || target + selected.Count > Pages.Count)
            return;
        MovePages(selected, target);
    }

    /// <summary>Moves pages so the first of them ends up at <paramref name="targetIndex"/> (in the list without them).</summary>
    public void MovePages(IReadOnlyList<PdfPageItem> pages, int targetIndex)
    {
        var ordered = pages.OrderBy(Pages.IndexOf).ToList();
        Change(() =>
        {
            foreach (var page in ordered)
                Pages.Remove(page);
            targetIndex = Math.Clamp(targetIndex, 0, Pages.Count);
            foreach (var (page, i) in ordered.Select((p, i) => (p, i)))
            {
                Pages.Insert(targetIndex + i, page);
                page.IsSelected = true;
            }
        });
        SelectionChanged();
    }

    // ───────── Marks ─────────

    /// <summary>Adds a mark drawn on the current page (points of the shown page).</summary>
    public async Task AddMarkAsync(PdfTool tool, double x, double y, double width, double height)
    {
        if (CurrentPage is null || tool == PdfTool.Select)
            return;
        if (tool == PdfTool.EditText)
        {
            await EditTextAtAsync(x, y);
            return;
        }
        var kind = tool switch
        {
            PdfTool.Highlight => PdfMarkKind.Highlight,
            PdfTool.Underline => PdfMarkKind.Underline,
            PdfTool.StrikeOut => PdfMarkKind.StrikeOut,
            PdfTool.Note => PdfMarkKind.Note,
            PdfTool.FreeText => PdfMarkKind.FreeText,
            PdfTool.Rectangle => PdfMarkKind.Rectangle,
            PdfTool.Ellipse => PdfMarkKind.Ellipse,
            PdfTool.Line => PdfMarkKind.Line,
            _ => PdfMarkKind.Image,
        };
        string? text = kind switch
        {
            PdfMarkKind.Note => _loc["pdf.note_default"],
            PdfMarkKind.FreeText => _loc["pdf.text_default"],
            _ => null,
        };
        if (tool == PdfTool.Signature)
        {
            if (CreateSignatureAsync is null || await CreateSignatureAsync() is not { } signature)
                return;
            text = signature.Path;
            // At the click, at the size chosen; a dragged box takes the signature at its own aspect ratio.
            var aspect = signature.HeightPoints / Math.Max(1, signature.WidthPoints);
            if (Math.Abs(width) >= 8)
                (width, height) = (Math.Abs(width), Math.Abs(width) * aspect);
            else
                (width, height) = (signature.WidthPoints, signature.HeightPoints);
        }
        else if (kind == PdfMarkKind.Image)
        {
            text = PickImageAsync is null ? null : await PickImageAsync();
            if (text is null)
                return;
            if (Math.Abs(width) < 8 || Math.Abs(height) < 8)
                (width, height) = (160, 120);
        }
        else if (kind == PdfMarkKind.FreeText && (Math.Abs(width) < 8 || Math.Abs(height) < 8))
            (width, height) = (180, 24);
        else if (kind is not PdfMarkKind.Note and not PdfMarkKind.Line && (Math.Abs(width) < 3 || Math.Abs(height) < 3))
            return; // a click without a drag
        var color = kind == PdfMarkKind.FreeText && MarkColor == "#FFD400" ? "#000000" : MarkColor;
        var page = CurrentPage;
        PdfMarkItem? added = null;
        Change(() =>
        {
            added = new PdfMarkItem(new PdfMark(0, kind, x, y, width, height, color, text,
                LineWidth: kind is PdfMarkKind.Underline or PdfMarkKind.StrikeOut ? 1.5 : 2, FontSize: 14));
            page.Marks.Add(added);
        });
        CurrentPage = page;
        SelectedMark = added;
        LayoutMarks();
    }

    /// <summary>Starts changing the line of the page's own text at a point (shown page, points).</summary>
    private async Task EditTextAtAsync(double x, double y)
    {
        var page = CurrentPage!;
        if (page.Marks.FirstOrDefault(m => m.IsReplaceText && m.Contains(x, y)) is { } existing)
        {
            SelectedMark = existing;
            return;
        }
        if (page.Rotation != 0)
        {
            Error(_loc["pdf.edit_text_turned"]);
            return;
        }
        PdfTextHit hit;
        try
        {
            var reference = page.Reference;
            var password = _passwords.GetValueOrDefault(reference.SourcePath);
            hit = await Task.Run(() => PdfContentEditing.HitTest(reference.SourcePath, reference.PageIndex, x, y, password));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error(ex.Message);
            return;
        }
        if (hit.Line is not { } line)
        {
            Error(_loc[hit.Miss switch
            {
                PdfTextMiss.TurnedPage => "pdf.edit_text_turned",
                PdfTextMiss.NoTextOnPage => "pdf.edit_text_none",
                _ => "pdf.edit_text_miss",
            }]);
            return;
        }
        // The line is in PDF user space; the preview is in shown points from the page's top-left.
        var left = line.Left - hit.OriginX;
        var top = hit.OriginY - line.Top;
        PdfMarkItem? added = null;
        Change(() =>
        {
            added = new PdfMarkItem(new PdfMark(0, PdfMarkKind.ReplaceText, left, top, line.Right - line.Left, line.Top - line.Bottom,
                line.Color, line.Text, FontSize: line.FontSize, Line: line));
            page.Marks.Add(added);
        });
        CurrentPage = page;
        SelectedMark = added;
        LayoutMarks();
    }

    /// <summary>The topmost mark at a point of the current page, or null.</summary>
    public PdfMarkItem? MarkAt(double x, double y) => CurrentPage?.Marks.LastOrDefault(m => m.Contains(x, y));

    /// <summary>Starts moving a mark: one undo step for the whole drag.</summary>
    public void BeginMarkChange()
    {
        _undo.Push(Take());
        _redo.Clear();
        IsDirty = true;
        NotifyHistory();
    }

    /// <summary>Resizes a picture or signature from its bottom-right corner, keeping its aspect ratio.</summary>
    public void ResizeMark(PdfMarkItem mark, double newWidth)
    {
        if (!mark.IsImage || mark.Width == 0)
            return;
        var aspect = mark.Height / mark.Width;
        mark.Width = Math.Clamp(newWidth, 12, 2000);
        mark.Height = mark.Width * aspect;
        mark.Layout(Zoom * PixelsPerPoint);
    }

    public void MoveMark(PdfMarkItem mark, double dx, double dy)
    {
        if (mark.IsReplaceText)
            return; // the page's own text stays where it is
        mark.X += dx;
        mark.Y += dy;
        mark.Layout(Zoom * PixelsPerPoint);
    }

    /// <summary>A property of the selected mark was edited in the side panel.</summary>
    public void MarkEdited()
    {
        IsDirty = true;
        LayoutMarks();
    }

    /// <summary>Delete key: the selected mark, else the selected pages.</summary>
    [RelayCommand]
    private void DeleteSelection()
    {
        if (SelectedMark is not null)
            DeleteMark();
        else
            DeletePages();
    }

    [RelayCommand]
    private void DeleteMark()
    {
        if (SelectedMark is not { } mark || CurrentPage is not { } page)
            return;
        Change(() => page.Marks.Remove(mark));
        CurrentPage = page;
        SelectedMark = null;
    }

    [RelayCommand]
    private void SetColor(string color)
    {
        MarkColor = color;
        if (SelectedMark is { } mark)
        {
            BeginMarkChange();
            mark.Color = color;
        }
    }

    private void SetTool(PdfTool tool)
    {
        Tool = tool;
        SelectedMark = null;
        if (SelectedTool.Tool != tool)
            SelectedTool = Tools.First(t => t.Tool == tool);
    }

    // ───────── Save ─────────

    private PdfSaveOptions SaveOptions(bool isDocument) => new()
    {
        UserPassword = Password.Length > 0 ? Password : null,
        RemoveMetadata = RemoveMetadata,
        SourcePasswords = new Dictionary<string, string>(_passwords, StringComparer.OrdinalIgnoreCase),
        // Extracted pages leave the form behind.
        FormValues = isDocument ? Fields.Where(f => f.IsChanged).ToDictionary(f => f.Field.Name, f => f.Value) : null,
    };

    [RelayCommand]
    private async Task PickCertificate()
    {
        if (PickCertificateAsync is not null && await PickCertificateAsync() is { } path)
        {
            CertificatePath = path;
            SignWithCertificate = true;
        }
    }

    private PdfStamps Stamps() => new()
    {
        PageNumberFormat = AddPageNumbers ? PageNumberFormat : null,
        PageNumberPosition = PageNumberPosition,
        Header = Header,
        Footer = Footer,
        Watermark = Watermark,
        WatermarkOpacity = WatermarkOpacity,
    };

    [RelayCommand]
    private Task Save() => SaveDocumentAsync();

    [RelayCommand]
    private Task SaveAs() => SaveAsDocumentAsync();

    /// <summary>Saves the document (asking where the first time); true only when it was written.</summary>
    public async Task<bool> SaveDocumentAsync() =>
        SavePath is null ? await SaveAsDocumentAsync() : await WriteAsync(Pages.ToList(), SavePath, true);

    /// <summary>Save As; false when the dialog was cancelled or writing failed.</summary>
    public async Task<bool> SaveAsDocumentAsync()
    {
        if (DocumentPath is null || PickSavePathAsync is null)
            return false;
        var path = await PickSavePathAsync(_loc.Format("pdf.edited_name", Path.GetFileNameWithoutExtension(DocumentPath)));
        return path is not null && await WriteAsync(Pages.ToList(), path, true);
    }

    // ───────── Close ─────────

    /// <summary>Asks to save unsaved changes: (title, message) → Save, Don't save or Cancel.</summary>
    public Func<string, string, Task<UnsavedChoice>>? AskUnsavedAsync { get; set; }

    /// <summary>
    /// Settles unsaved changes before the document goes away: true when it may (nothing unsaved, saved, or the person
    /// chose not to save); false keeps it open with every edit (Cancel, a cancelled Save As, or a failed save).
    /// </summary>
    public async Task<bool> ResolveUnsavedAsync()
    {
        if (!IsDirty || DocumentPath is null)
            return true;
        if (AskUnsavedAsync is null)
            return ConfirmAsync is null || await ConfirmAsync(_loc["pdf.discard_title"], _loc["pdf.discard_message"]);
        var choice = await AskUnsavedAsync(_loc["pdf.unsaved_title"], _loc.Format("pdf.unsaved_message", Path.GetFileName(SavePath ?? DocumentPath)));
        return choice switch
        {
            UnsavedChoice.Save => await SaveDocumentAsync() && !IsDirty,
            UnsavedChoice.DontSave => true,
            _ => false,
        };
    }

    /// <summary>Closes the document (asking about unsaved changes); back to the empty editor.</summary>
    [RelayCommand]
    private async Task Close()
    {
        if (DocumentPath is null || !await ResolveUnsavedAsync())
            return;
        _previewCancel?.Cancel();
        _undo.Clear();
        _redo.Clear();
        NotifyHistory();
        SelectedMark = null;
        Pages.Clear();
        Fields.Clear();
        OnPropertyChanged(nameof(HasFields));
        CurrentPage = null;
        PreviewBitmap = null;
        ResetSaveOptions();
        DocumentPath = null;
        SavePath = null;
        LastOutput = null;
        Message = null;
        IsDirty = false;
    }

    /// <summary>Writes the selected pages (with their marks) into a new PDF; the document stays as it is.</summary>
    [RelayCommand]
    private async Task ExtractPages()
    {
        var selected = Selected().OrderBy(Pages.IndexOf).ToList();
        if (DocumentPath is null || selected.Count == 0 || PickSavePathAsync is null)
            return;
        var path = await PickSavePathAsync(_loc.Format("pdf.extract_name", Path.GetFileNameWithoutExtension(DocumentPath)));
        if (path is not null)
            await WriteAsync(selected, path, false);
    }

    /// <summary>Writes the pages; true when the file was written.</summary>
    private async Task<bool> WriteAsync(IReadOnlyList<PdfPageItem> pages, string path, bool isDocument)
    {
        var references = pages.Select(p => p.Reference).ToList();
        var marks = pages.SelectMany((p, i) => p.Marks.Select(m => m.ToMark(i))).ToList();
        var stamps = Stamps();
        var options = SaveOptions(isDocument);
        var certificatePath = SignWithCertificate ? CertificatePath : null;
        var certificatePassword = CertificatePassword;
        if (SignWithCertificate && certificatePath is null)
        {
            Error(_loc["pdf.cert_missing"]);
            return false;
        }
        try
        {
            IsBusy = true;
            await Task.Run(() =>
            {
                using var certificate = certificatePath is null ? null : LoadCertificate(certificatePath, certificatePassword);
                PdfEditor.Save(references, path, marks, stamps,
                    certificate is null ? options : options with { Signing = new PdfSigning(certificate) });
            });
            LastOutput = path;
            _recent.Add(path);
            if (isDocument)
            {
                SavePath = path;
                await ReloadSavedAsync(path, options.UserPassword);
            }
            Info(_loc.Format("pdf.saved", Path.GetFileName(path)));
            return true;
        }
        catch (PdfEditException ex)
        {
            // Nothing was written; the edits stay as they are.
            Error(ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private System.Security.Cryptography.X509Certificates.X509Certificate2 LoadCertificate(string path, string password)
    {
        try
        {
            var certificate = PdfContentEditing.LoadCertificate(path, password);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new PdfEditException(_loc["pdf.cert_no_key"]);
            }
            return certificate;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new PdfEditException(_loc.Format("pdf.cert_bad", Path.GetFileName(path)));
        }
        catch (IOException ex)
        {
            throw new PdfEditException(ex.Message);
        }
    }

    [RelayCommand]
    private void RevealOutput()
    {
        if (LastOutput is not null && File.Exists(LastOutput))
            _platform.RevealInFileManager(LastOutput);
    }

    [RelayCommand]
    private void OpenOutput()
    {
        if (LastOutput is null || !File.Exists(LastOutput))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LastOutput) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Error(ex.Message);
        }
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    private void Info(string text)
    {
        MessageIsError = false;
        Message = text;
    }

    private void Error(string text)
    {
        MessageIsError = true;
        Message = text;
    }

    /// <summary>Before the window closes: settles unsaved changes; true when it may close.</summary>
    public Task<bool> CanCloseAsync() => ResolveUnsavedAsync();

    public void Dispose()
    {
        _life.Cancel();
        _previewCancel?.Cancel();
        foreach (var bitmap in _thumbnails.Values)
            bitmap.Dispose();
        _thumbnails.Clear();
        _life.Dispose();
    }
}
