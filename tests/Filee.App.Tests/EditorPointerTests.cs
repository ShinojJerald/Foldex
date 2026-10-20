// Real mouse input (headless pointer events) into the PDF editor's page overlay and the Signature Studio's drawing pad:
// what gets drawn must follow the pointer, end to end.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Engines.Pdf;
using ImageMagick;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

public class EditorPointerTests
{
    private static void Pump(int frames = 10)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Presses at the first point, moves through the others and releases at the last (points in the target's coordinates).</summary>
    private static void Drag(TopLevel window, Visual target, params Point[] points)
    {
        Point At(Point p) => target.TranslatePoint(p, window)!.Value;
        window.MouseMove(At(points[0]), RawInputModifiers.None);
        window.MouseDown(At(points[0]), MouseButton.Left, RawInputModifiers.None);
        foreach (var p in points.Skip(1))
        {
            window.MouseMove(At(p), RawInputModifiers.LeftMouseButton);
            Pump(2);
        }
        window.MouseUp(At(points[^1]), MouseButton.Left, RawInputModifiers.None);
        Pump();
    }

    // ───────── Bug A: drawing in Signature Studio ─────────

    [AvaloniaFact]
    public void A_signature_drawn_with_the_mouse_has_ink_and_can_be_inserted()
    {
        TestServices.EnsureInitialized();
        var studio = new SignatureWindow();
        studio.Show();
        Pump();
        studio.Modes.SelectedItem = studio.DrawTab;
        Pump();
        Assert.Null(studio.Insert()); // nothing drawn: still refused

        // A scribble and a second stroke, drawn with real pointer events.
        Drag(studio, studio.Surface, new Point(40, 120), new Point(90, 60), new Point(140, 130), new Point(200, 70), new Point(260, 120));
        Drag(studio, studio.Surface, new Point(300, 140), new Point(420, 140), new Point(500, 135));
        Pump();
        studio.Render();

        // The strokes on screen follow the pointer…
        var strokes = studio.Strokes.Children.OfType<Polyline>().ToList();
        Assert.Equal(2, strokes.Count);
        Assert.True(strokes[0].Points.Count >= 5, $"first stroke has {strokes[0].Points.Count} points");
        Assert.True(strokes[0].Bounds.Width > 150 && strokes[0].Bounds.Height > 50, $"first stroke drawn {strokes[0].Bounds}");
        // …the preview has ink and Insert is enabled…
        Assert.False(studio.ErrorText.IsVisible, studio.ErrorText.Text);
        Assert.True(studio.InsertButton.IsEnabled);
        var choice = studio.Insert();
        Assert.NotNull(choice);
        // …and the picture is the drawing: wide (both strokes), with opaque ink.
        using (var image = new MagickImage(choice.Path))
        {
            Assert.True(image.Width > 2.5 * image.Height, $"{image.Width}×{image.Height}");
            Assert.Contains(image.Histogram(), p => p.Key.A > 250 && p.Key.R < 80);
        }

        // Undo stroke removes the second stroke; Clear removes everything and Insert is refused again.
        studio.OnUndoStroke(null, new Avalonia.Interactivity.RoutedEventArgs());
        Assert.Single(studio.Strokes.Children);
        studio.OnClear(null, new Avalonia.Interactivity.RoutedEventArgs());
        Pump();
        studio.Render();
        Assert.Null(studio.Insert());
        Assert.False(studio.InsertButton.IsEnabled);
        studio.Close();
    }

    // ───────── Bug C: the line tool ─────────

    private static (MainWindow Window, PdfEditorPageViewModel Editor, Canvas Overlay) OpenEditor(string pdf)
    {
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("pdf");
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        Pump();
        var editor = (PdfEditorPageViewModel)vm.CurrentPage;
        var open = vm.OpenPdfAsync(pdf);
        var start = Environment.TickCount64;
        while (!open.IsCompleted || editor.PreviewBitmap is null)
        {
            Pump(2);
            Thread.Sleep(5);
            Assert.True(Environment.TickCount64 - start < 30000, "Timed out opening");
        }
        Pump();
        var overlay = window.GetVisualDescendants().OfType<Canvas>().Single(c => c.Name == "Overlay");
        return (window, editor, overlay);
    }

    private static string TwoPages(string dir)
    {
        _ = PdfEditor.NormalizeRotation(0);
        var path = System.IO.Path.Combine(dir, "lines.pdf");
        using var document = new PdfSharp.Pdf.PdfDocument();
        document.AddPage();
        document.AddPage().Rotate = 90;
        document.Save(path);
        return path;
    }

    [AvaloniaTheory]
    [InlineData(1.0, 0)]
    [InlineData(1.5, 0)]
    [InlineData(0.6, 0)]
    [InlineData(1.0, 1)]
    public void Lines_follow_the_drag_at_any_zoom_and_on_a_turned_page(double zoom, int page)
    {
        TestServices.EnsureInitialized();
        var dir = Directory.CreateTempSubdirectory("foldex-lines-").FullName;
        var (window, editor, overlay) = OpenEditor(TwoPages(dir));
        editor.GoTo(page);
        editor.Zoom = zoom;
        Pump();
        var scale = editor.Zoom * 96 / 72;

        void Draw(Point from, Point to)
        {
            editor.SelectedTool = editor.Tools.Single(t => t.Tool == PdfTool.Line);
            Pump();
            Drag(window, overlay, from, new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), to);
        }
        // Horizontal, vertical, diagonal up-right, diagonal down-left (overlay pixels).
        (Point From, Point To)[] drags =
        [
            (new Point(100, 150), new Point(300, 150)),
            (new Point(120, 200), new Point(120, 380)),
            (new Point(150, 400), new Point(330, 300)),
            (new Point(350, 420), new Point(180, 520)),
        ];
        foreach (var (from, to) in drags)
            Draw(from, to);

        var marks = editor.Pages[page].Marks.Where(m => m.IsLine).ToList();
        Assert.Equal(drags.Length, marks.Count);
        foreach (var ((from, to), mark) in drags.Zip(marks))
        {
            // Points of the shown page: start and end where the pointer was pressed and released.
            Assert.Equal(from.X / scale, mark.X, 1);
            Assert.Equal(from.Y / scale, mark.Y, 1);
            Assert.Equal(to.X / scale, mark.X + mark.Width, 1);
            Assert.Equal(to.Y / scale, mark.Y + mark.Height, 1);
            // And the line drawn on screen runs between the same two pixels.
            var drawn = overlay.GetVisualDescendants().OfType<Line>().Single(l => l.DataContext == mark);
            var a = drawn.TranslatePoint(drawn.StartPoint, overlay)!.Value;
            var b = drawn.TranslatePoint(drawn.EndPoint, overlay)!.Value;
            Assert.True(Near(a, from) && Near(b, to), $"drawn {a}→{b}, dragged {from}→{to}");
        }

        // Saved: the /L of each line annotation is the dragged line in PDF space (same direction, same length).
        var output = System.IO.Path.Combine(dir, "saved.pdf");
        editor.PickSavePathAsync = _ => Task.FromResult<string?>(output);
        var save = editor.SaveCommand.ExecuteAsync(null);
        while (!save.IsCompleted)
            Pump(2);
        Assert.False(editor.IsDirty, editor.Message);
        using var saved = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var annots = saved.Pages[page].Elements.GetArray("/Annots")!;
        var lines = annots.Elements.Select(e => ((PdfSharp.Pdf.Advanced.PdfReference)e).Value as PdfSharp.Pdf.PdfDictionary)
            .Where(d => d!.Elements.GetName("/Subtype") == "/Line").Select(d => d!.Elements.GetArray("/L")!).ToList();
        Assert.Equal(drags.Length, lines.Count);
        foreach (var ((from, to), l) in drags.Zip(lines))
        {
            var dx = l.Elements.GetReal(2) - l.Elements.GetReal(0);
            var dy = l.Elements.GetReal(3) - l.Elements.GetReal(1);
            var length = Math.Sqrt(dx * dx + dy * dy);
            var dragged = Math.Sqrt(Math.Pow((to.X - from.X) / scale, 2) + Math.Pow((to.Y - from.Y) / scale, 2));
            Assert.Equal(dragged, length, 0);
            if (page == 0 && from.Y == to.Y)
                Assert.Equal(0, dy, 1); // horizontal stays horizontal
            if (page == 0 && from.X == to.X)
                Assert.Equal(0, dx, 1); // vertical stays vertical
        }
        window.Close();
    }

    /// <summary>Regression: the Line tool previewed a rectangle while dragging, so a line drawn by following the box came
    /// out as its diagonal. The preview is now the line itself, under the pointer.</summary>
    [AvaloniaFact]
    public void The_line_tool_previews_a_line_not_a_box()
    {
        TestServices.EnsureInitialized();
        var dir = Directory.CreateTempSubdirectory("foldex-lines-").FullName;
        var (window, editor, overlay) = OpenEditor(TwoPages(dir));
        editor.SelectedTool = editor.Tools.Single(t => t.Tool == PdfTool.Line);
        Pump();
        Point At(Point p) => overlay.TranslatePoint(p, window)!.Value;
        window.MouseMove(At(new Point(100, 200)), RawInputModifiers.None);
        window.MouseDown(At(new Point(100, 200)), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(At(new Point(320, 200)), RawInputModifiers.LeftMouseButton);
        Pump();

        var preview = overlay.GetVisualDescendants().OfType<Line>().Single(l => l.Name == "RubberLine");
        var box = overlay.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RubberBand");
        Assert.True(preview.IsVisible, "no line preview while dragging");
        Assert.False(box.IsVisible, "a box preview is shown for the Line tool");
        Assert.True(Near(preview.TranslatePoint(preview.StartPoint, overlay)!.Value, new Point(100, 200))
                    && Near(preview.TranslatePoint(preview.EndPoint, overlay)!.Value, new Point(320, 200)));

        window.MouseUp(At(new Point(320, 200)), MouseButton.Left, RawInputModifiers.None);
        Pump();
        Assert.False(preview.IsVisible);
        var mark = Assert.Single(editor.Pages[0].Marks);
        Assert.Equal(0, mark.Height, 1); // horizontal drag, horizontal line
        // Other drawing tools still preview a box.
        editor.SelectedTool = editor.Tools.Single(t => t.Tool == PdfTool.Rectangle);
        Pump();
        window.MouseDown(At(new Point(100, 300)), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(At(new Point(200, 380)), RawInputModifiers.LeftMouseButton);
        Pump();
        Assert.True(box.IsVisible);
        Assert.False(preview.IsVisible);
        window.MouseUp(At(new Point(200, 380)), MouseButton.Left, RawInputModifiers.None);
        Pump();
        Assert.Contains(editor.Pages[0].Marks, m => m.IsBox);
        window.Close();
    }

    // ───────── Bug B: picking existing text ─────────

    /// <summary>Regression (bug report): on US Letter pages, clicking visible text said "There's no editable text there".
    /// Clicks go in through the real overlay with the Edit text tool, on page 2 of a Letter document.</summary>
    [AvaloniaFact]
    public void Clicking_text_on_a_letter_page_picks_that_line()
    {
        TestServices.EnsureInitialized();
        _ = PdfEditor.NormalizeRotation(0);
        var dir = Directory.CreateTempSubdirectory("foldex-text-").FullName;
        var path = System.IO.Path.Combine(dir, "guide.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            for (var p = 0; p < 3; p++)
            {
                var page = document.AddPage();
                page.Width = PdfSharp.Drawing.XUnit.FromPoint(612);
                page.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
                using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                gfx.DrawString($"Heading on page {p + 1}", new PdfSharp.Drawing.XFont("Arial", 16, PdfSharp.Drawing.XFontStyleEx.Bold), PdfSharp.Drawing.XBrushes.Black, 54, 80);
                for (var i = 0; i < 20; i++)
                    gfx.DrawString($"Body line {i + 1} of page {p + 1} with some words", new PdfSharp.Drawing.XFont("Arial", 9), PdfSharp.Drawing.XBrushes.Black, 54, 120 + i * 30);
            }
            document.Save(path);
        }
        var (window, editor, overlay) = OpenEditor(path);
        editor.GoTo(1);
        var start = Environment.TickCount64;
        while (editor.PreviewBitmap is null || Environment.TickCount64 - start < 500)
            Pump(2);
        var scale = editor.Zoom * 96 / 72;

        // Lines near the top and the bottom of the page: a wrong page height shows most at the bottom.
        foreach (var (line, baseline) in new[] { ("Heading on page 2", 80.0), ("Body line 1 of page 2 with some words", 120.0), ("Body line 20 of page 2 with some words", 690.0) })
        {
            editor.SelectedTool = editor.Tools.Single(t => t.Tool == PdfTool.EditText);
            Pump();
            var click = new Point(80 * scale, (baseline - 3) * scale); // on the glyphs, just above the baseline
            Drag(window, overlay, click, click);
            var start2 = Environment.TickCount64;
            while (!editor.Pages[1].Marks.Any(m => m.IsReplaceText && m.Text == line) && Environment.TickCount64 - start2 < 5000)
                Pump(2);
            Assert.True(editor.Pages[1].Marks.Any(m => m.IsReplaceText && m.Text == line), $"'{line}' not picked: {editor.Message}");
            var mark = editor.Pages[1].Marks.Single(m => m.Text == line);
            // The text change sits on the line in the preview (shown points, from the top).
            Assert.InRange(mark.Y, baseline - 20, baseline);
            Assert.InRange(mark.Y + mark.Height, baseline - 4, baseline + 6);
        }
        window.Close();
    }

    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) < 1.5 && Math.Abs(a.Y - b.Y) < 1.5;
}
