using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Filee.Engines.Magick;

namespace Filee.App.Views;

/// <summary>One of the five typed signature styles, previewed with the name typed so far.</summary>
public sealed partial class SignatureStyle(string key, string title, FontFamily family, FontWeight weight) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public FontFamily Family { get; } = family;
    public FontWeight Weight { get; } = weight;
    [ObservableProperty] private string _sample = "";
    [ObservableProperty] private IBrush _ink = Brushes.Black;
}

/// <summary>
/// Signature Studio: type (five styles), draw (mouse, pen or touch) or upload a signature, pick its colour and size, and
/// insert it. Saved signatures go to the user's data folder (signatures\); "Use last signature" reuses the newest.
/// </summary>
public partial class SignatureWindow : Window
{
    private const string FontBase = "avares://Filee/Assets/Fonts/Signatures#";
    private static readonly string[] InkColors = ["#111111", "#1B2A6B", "#1F4FD1", "#7A1F2B"];

    private readonly ILocalizer _loc = AppHost.Get<ILocalizer>();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly string _work = Directory.CreateTempSubdirectory("foldex-signature-").FullName;
    private Polyline? _stroke;
    private string? _uploadPath;
    private string? _previewPath;
    private (int W, int H) _previewPixels;
    private Color _ink = Color.Parse(InkColors[0]);
    private SignatureChoice? _result;
    private int _version;

    public ObservableCollection<SignatureStyle> StyleItems { get; } = [];

    public SignatureWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        foreach (var (key, family, weight) in new[]
                 {
                     ("classic", "Great Vibes", FontWeight.Normal),
                     ("modern", "Dancing Script", FontWeight.Medium),
                     ("formal", "Pinyon Script", FontWeight.Normal),
                     ("bold", "Kaushan Script", FontWeight.Normal),
                     ("minimal", "Sacramento", FontWeight.Normal),
                 })
            StyleItems.Add(new SignatureStyle(key, _loc[$"sig.style.{key}"], new FontFamily(FontBase + family), weight));
        Styles.ItemsSource = StyleItems;
        Styles.SelectedIndex = 0;

        Colors.ItemsSource = InkColors;
        Colors.SelectedIndex = 0;
        Colors.SelectionChanged += (_, _) =>
        {
            if (Colors.SelectedItem is string hex)
            {
                _ink = Color.Parse(hex);
                Changed();
            }
        };

        Surface.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed)
                return;
            _stroke = new Polyline { StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
            StyleStroke(_stroke);
            _stroke.Points = [e.GetPosition(Strokes)];
            Strokes.Children.Add(_stroke);
            e.Pointer.Capture(Surface);
        };
        Surface.PointerMoved += (_, e) =>
        {
            if (_stroke is null)
                return;
            var point = e.GetPosition(Strokes);
            // A new list each time: Polyline builds its shape when Points is set and doesn't see points added to the
            // same list, so the stroke stayed invisible (and the drawing "had no visible ink").
            _stroke.Points = [.. _stroke.Points, new Point(Math.Clamp(point.X, 0, Surface.Width), Math.Clamp(point.Y, 0, Surface.Height))];
        };
        Surface.PointerReleased += (_, e) =>
        {
            // A click without movement still leaves a dot.
            if (_stroke is { Points.Count: 1 } dot)
                dot.Points = [dot.Points[0], dot.Points[0] + new Point(0.5, 0.5)];
            _stroke = null;
            e.Pointer.Capture(null);
            Changed();
        };

        NameBox.TextChanged += (_, _) => Changed();
        Styles.SelectionChanged += (_, _) => Changed();
        Modes.SelectionChanged += (_, _) => Changed();
        Thickness.ValueChanged += (_, _) => Changed();
        SizeSlider.ValueChanged += (_, _) => UpdateSizeText();
        RemoveWhite.IsCheckedChanged += (_, _) => Changed();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Render();
        };
        LastButton.IsVisible = Last() is not null;
        Closed += (_, _) =>
        {
            try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
        Changed();
    }

    /// <summary>Saved signatures, in the app's data folder (%APPDATA%\Foldex by default).</summary>
    public static string Folder => System.IO.Path.Combine(AppHost.Get<UserDataStore>().Directory, "signatures");

    private static string? Last() => Directory.Exists(Folder)
        ? new DirectoryInfo(Folder).GetFiles("signature-*.png").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
        : null;

    private enum Mode { Type, Draw, Upload }

    private Mode Current => Modes.SelectedItem == DrawTab ? Mode.Draw : Modes.SelectedItem == UploadTab ? Mode.Upload : Mode.Type;

    private void StyleStroke(Polyline stroke)
    {
        stroke.Stroke = new SolidColorBrush(_ink);
        stroke.StrokeThickness = Thickness.Value;
    }

    /// <summary>Something changed: update the style samples now and the rendered preview shortly.</summary>
    private void Changed()
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? _loc["sig.sample_name"] : NameBox.Text.Trim();
        var ink = new SolidColorBrush(_ink);
        foreach (var style in StyleItems)
        {
            style.Sample = name;
            style.Ink = ink;
        }
        foreach (var stroke in Strokes.Children.OfType<Polyline>())
            StyleStroke(stroke);
        UndoStroke.IsEnabled = Strokes.Children.Count > 0;
        // Colour applies to typed and drawn signatures; an uploaded picture keeps its own.
        ColorPanel.IsEnabled = Current != Mode.Upload;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Renders the current signature to a trimmed transparent PNG and shows it.</summary>
    internal void Render()
    {
        var version = ++_version;
        ErrorText.IsVisible = false;
        string? path = null;
        try
        {
            var output = System.IO.Path.Combine(_work, $"preview-{version}.png");
            switch (Current)
            {
                case Mode.Type when !string.IsNullOrWhiteSpace(NameBox.Text) && Styles.SelectedItem is SignatureStyle style:
                    _previewPixels = SignatureImage.PrepareRendered(RenderTyped(NameBox.Text.Trim(), style), output);
                    path = output;
                    break;
                case Mode.Draw when Strokes.Children.Count > 0:
                    _previewPixels = SignatureImage.PrepareRendered(RenderControl(Surface, 3), output);
                    path = output;
                    break;
                case Mode.Upload when _uploadPath is not null:
                    _previewPixels = SignatureImage.Prepare(_uploadPath, output, RemoveWhite.IsChecked == true);
                    path = output;
                    break;
            }
        }
        catch (InvalidDataException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
        }
        _previewPath = path;
        Preview.Source = path is null ? null : new Bitmap(path);
        PreviewEmpty.IsVisible = path is null;
        InsertButton.IsEnabled = path is not null;
        UpdateSizeText();
    }

    private (double W, double H) PageSize()
    {
        var width = Math.Round(SizeSlider.Value);
        return _previewPixels.W <= 0 ? (width, 0) : (width, Math.Round(width * _previewPixels.H / _previewPixels.W));
    }

    private void UpdateSizeText()
    {
        var (w, h) = PageSize();
        PreviewSize.Text = _previewPath is null ? "" : _loc.Format("sig.size_value", w, h);
    }

    /// <summary>The name drawn in the style. Script swashes reach far outside the line box, and a TextBlock clips its
    /// glyphs to its own bounds when rendered to a bitmap: the room goes inside it, as its padding.</summary>
    internal byte[] RenderTyped(string text, SignatureStyle style)
    {
        const double size = 120;
        var block = new TextBlock
        {
            Text = text, FontFamily = style.Family, FontWeight = style.Weight, FontSize = size, Padding = new Thickness(size * 0.8),
            Foreground = new SolidColorBrush(_ink), HorizontalAlignment = HorizontalAlignment.Left,
        };
        block.Measure(Size.Infinity);
        block.Arrange(new Rect(block.DesiredSize));
        return RenderControl(block, 1);
    }

    private static byte[] RenderControl(Control control, double scale)
    {
        var size = control.Bounds.Size;
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    internal void OnUndoStroke(object? sender, RoutedEventArgs e)
    {
        if (Strokes.Children.Count > 0)
            Strokes.Children.RemoveAt(Strokes.Children.Count - 1);
        Changed();
    }

    internal void OnClear(object? sender, RoutedEventArgs e)
    {
        Strokes.Children.Clear();
        Changed();
    }

    private async void OnChoosePicture(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.heic"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;
        SetUpload(path);
    }

    internal void SetUpload(string path)
    {
        _uploadPath = path;
        UploadName.Text = System.IO.Path.GetFileName(path);
        Modes.SelectedItem = UploadTab;
        Changed();
    }

    private void OnLast(object? sender, RoutedEventArgs e)
    {
        if (Last() is not { } last)
            return;
        try
        {
            using var image = new Bitmap(last);
            var width = Math.Round(SizeSlider.Value);
            _result = new SignatureChoice(last, width, Math.Round(width * image.PixelSize.Height / image.PixelSize.Width));
            Close();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnInsert(object? sender, RoutedEventArgs e)
    {
        if (Insert() is { } choice)
        {
            _result = choice;
            Close();
        }
    }

    /// <summary>Keeps the current preview in the signatures folder and returns what to place; null without a signature.</summary>
    internal SignatureChoice? Insert()
    {
        _debounce.Stop();
        Render(); // the newest input, even if typed a moment ago
        if (_previewPath is null)
            return null;
        Directory.CreateDirectory(Folder);
        var path = System.IO.Path.Combine(Folder, $"signature-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        File.Copy(_previewPath, path);
        var (w, h) = PageSize();
        return new SignatureChoice(path, w, h);
    }

    /// <summary>The signature to place, or null when cancelled.</summary>
    public static async Task<SignatureChoice?> CreateAsync(Window owner)
    {
        var window = new SignatureWindow();
        window.Opened += (_, _) => window.NameBox.Focus();
        await window.ShowDialog(owner);
        return window._result;
    }
}
