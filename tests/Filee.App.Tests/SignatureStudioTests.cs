// Signature Studio: five distinct typed styles, drawing, uploading, colour and size, and the signature placed, resized
// and saved in a PDF by the editor.

using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Engines.Pdf;
using ImageMagick;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

public class SignatureStudioTests
{
    private static void Pump(int frames = 20)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static SignatureWindow Studio()
    {
        TestServices.EnsureInitialized();
        var window = new SignatureWindow();
        window.Show();
        Pump();
        return window;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [AvaloniaFact]
    public void Five_typed_styles_are_really_different_and_take_the_colour_and_size()
    {
        var studio = Studio();
        studio.NameBox.Text = "Ada Lovelace";
        Pump(); // TextChanged arrives on the next UI tick
        Assert.Equal(5, studio.StyleItems.Count);
        Assert.Equal(5, studio.StyleItems.Select(s => s.Family.Name).Distinct().Count());
        Assert.All(studio.StyleItems, s => Assert.Equal("Ada Lovelace", s.Sample));

        var shapes = new List<(string Hash, double Aspect)>();
        foreach (var style in studio.StyleItems.ToList())
        {
            studio.Styles.SelectedItem = style;
            var choice = studio.Insert();
            Assert.NotNull(choice);
            Assert.StartsWith(SignatureWindow.Folder, choice.Path);
            using var image = new MagickImage(choice.Path);
            Assert.True(image.HasAlpha);
            Assert.True(image.Width > 3 * image.Height / 2, $"{style.Key}: {image.Width}×{image.Height}");
            shapes.Add((Hash(choice.Path), choice.HeightPoints / choice.WidthPoints));
        }
        // Five different pictures, not one picture five times; their shapes differ too.
        Assert.Equal(5, shapes.Select(s => s.Hash).Distinct().Count());
        Assert.True(shapes.Max(s => s.Aspect) - shapes.Min(s => s.Aspect) > 0.02);
        Pump();
        using (var frame = studio.CaptureRenderedFrame())
            frame?.Save(System.IO.Path.Combine(TestServices.ScreenshotDirectory, "signature-studio-type.png"));

        // Navy ink and a smaller size.
        studio.Styles.SelectedIndex = 3;
        studio.Colors.SelectedItem = "#1B2A6B";
        studio.SizeSlider.Value = 120;
        var navy = studio.Insert()!;
        Assert.Equal(120, navy.WidthPoints);
        using (var image = new MagickImage(navy.Path))
        {
            var ink = image.Histogram().Where(p => p.Key.A > 250).OrderByDescending(p => p.Value).First().Key;
            Assert.True(ink.B > ink.R + 40, $"ink {ink}");
        }
        studio.Close();
    }

    /// <summary>Regression (found live): swashes outside the line box were cut off, so "Jerald" came out as "Jerala".
    /// No style may have ink on the edge of its raw render.</summary>
    [AvaloniaFact]
    public void Typed_signatures_are_not_clipped()
    {
        var studio = Studio();
        foreach (var style in studio.StyleItems)
            foreach (var name in new[] { "Shinoj Jerald", "fghjklpqy Tdb" })
            {
                using var image = new MagickImage(studio.RenderTyped(name, style));
                using var pixels = image.GetPixels();
                bool Ink(int x, int y) => pixels.GetPixel(x, y)!.ToColor()!.A > 0;
                for (var x = 0; x < image.Width; x++)
                    Assert.False(Ink(x, 0) || Ink(x, (int)image.Height - 1), $"{style.Key} \"{name}\" touches the top or bottom");
                for (var y = 0; y < image.Height; y++)
                    Assert.False(Ink(0, y) || Ink((int)image.Width - 1, y), $"{style.Key} \"{name}\" touches a side");
            }
        // The ascender of a script "d" reaches above the line box: "d" must come out much taller than "a" (it came out
        // the same, the swash cut away, before the fix).
        foreach (var style in studio.StyleItems)
        {
            using var withD = new MagickImage(studio.RenderTyped("d", style));
            using var withA = new MagickImage(studio.RenderTyped("a", style));
            withD.Trim();
            withA.Trim();
            Assert.True(withD.Height > withA.Height * 1.3, $"{style.Key}: d {withD.Height}px, a {withA.Height}px");
        }
        studio.Close();
    }

    [AvaloniaFact]
    public void Drawing_supports_thickness_undo_and_clear()
    {
        var studio = Studio();
        studio.Modes.SelectedItem = studio.DrawTab;
        Assert.Null(studio.Insert()); // nothing drawn yet

        Polyline Stroke(params double[] xy)
        {
            var line = new Polyline { Stroke = Brushes.Black, StrokeThickness = 2 };
            for (var i = 0; i < xy.Length; i += 2)
                line.Points.Add(new Point(xy[i], xy[i + 1]));
            studio.Strokes.Children.Add(line);
            return line;
        }
        Stroke(40, 120, 120, 40, 200, 140, 300, 60);
        Stroke(320, 130, 480, 130);
        studio.Thickness.Value = 6;
        Pump();
        var thick = studio.Insert()!;
        Assert.All(studio.Strokes.Children.OfType<Polyline>(), p => Assert.Equal(6, p.StrokeThickness));


        studio.Strokes.Children.RemoveAt(1); // what Undo stroke does
        var oneStroke = studio.Insert()!;
        Assert.NotEqual(Hash(thick.Path), Hash(oneStroke.Path));
        using (var a = new MagickImage(thick.Path))
        using (var b = new MagickImage(oneStroke.Path))
            Assert.True(b.Width < a.Width); // the second stroke went further right

        studio.Strokes.Children.Clear();
        Assert.Null(studio.Insert());
        studio.Close();
    }

    [AvaloniaFact]
    public void An_uploaded_signature_loses_its_white_background_and_bad_files_are_refused()
    {
        var studio = Studio();
        var dir = Directory.CreateTempSubdirectory("foldex-sig-").FullName;
        var scan = System.IO.Path.Combine(dir, "scan.jpg");
        using (var image = new MagickImage(MagickColors.White, 800, 300))
        {
            new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black).Rectangle(100, 120, 700, 160).Draw(image);
            image.Write(scan);
        }
        studio.SetUpload(scan);
        var choice = studio.Insert()!;
        using (var result = new MagickImage(choice.Path))
        using (var pixels = result.GetPixels())
            Assert.Equal(0, pixels.GetPixel(1, 1)!.ToColor()!.A);

        var broken = System.IO.Path.Combine(dir, "broken.png");
        File.WriteAllText(broken, "nope");
        studio.SetUpload(broken);
        Assert.Null(studio.Insert());
        Assert.True(studio.ErrorText.IsVisible);
        Assert.False(studio.InsertButton.IsEnabled);
        studio.Close();
    }

    [AvaloniaFact]
    public void The_editor_places_resizes_saves_and_keeps_a_signature()
    {
        TestServices.EnsureInitialized();
        _ = PdfEditor.NormalizeRotation(0);
        var dir = Directory.CreateTempSubdirectory("foldex-sig-").FullName;
        var source = System.IO.Path.Combine(dir, "contract.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            document.AddPage();
            document.AddPage().Rotate = 90;
            document.Save(source);
        }
        var studio = Studio();
        studio.NameBox.Text = "Ada";
        var signature = studio.Insert()!;
        studio.Close();

        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("pdf");
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 820 };
        window.Show();
        Pump();
        var editor = (PdfEditorPageViewModel)vm.CurrentPage;
        var output = System.IO.Path.Combine(dir, "signed.pdf");
        editor.PickSavePathAsync = _ => Task.FromResult<string?>(output);
        editor.CreateSignatureAsync = () => Task.FromResult<SignatureChoice?>(signature);
        var open = vm.OpenPdfAsync(source);
        while (!open.IsCompleted)
            Pump(2);

        // Cancelled studio: nothing placed.
        editor.CreateSignatureAsync = () => Task.FromResult<SignatureChoice?>(null);
        Run(editor.AddMarkAsync(PdfTool.Signature, 100, 600, 0, 0));
        Assert.Empty(editor.Pages[0].Marks);

        // On page 2 (turned), at the click, at the chosen size and aspect ratio.
        editor.CreateSignatureAsync = () => Task.FromResult<SignatureChoice?>(signature);
        editor.GoTo(1);
        Run(editor.AddMarkAsync(PdfTool.Signature, 300, 200, 0, 0));
        var mark = Assert.Single(editor.Pages[1].Marks);
        Assert.True(mark.IsImage);
        Assert.Equal((300.0, 200.0, signature.WidthPoints, signature.HeightPoints), (mark.X, mark.Y, mark.Width, mark.Height));
        Assert.True(mark.ShowsResizeHandle);

        // Resizing keeps the aspect ratio; zoom changes only the pixels, not the points.
        var aspect = mark.Height / mark.Width;
        editor.BeginMarkChange();
        editor.ResizeMark(mark, 240);
        Assert.Equal(240, mark.Width);
        Assert.Equal(240 * aspect, mark.Height, 6);
        editor.ZoomInCommand.Execute(null);
        Assert.Equal(240, mark.Width);
        Assert.Equal(mark.Width * editor.Zoom * 96 / 72, mark.PixelWidth, 3);

        Run(editor.SaveCommand.ExecuteAsync(null));
        Assert.False(editor.IsDirty, editor.Message);
        // Saved into the page content of page 2 only, and after the reload it's part of the document.
        using (var saved = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(2, saved.PageCount);
            Assert.Equal(90, saved.Pages[1].Rotate);
        }
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(output))
        {
            Assert.Empty(pig.GetPage(1).GetImages());
            Assert.Single(pig.GetPage(2).GetImages());
        }
        Assert.Empty(editor.Pages[1].Marks);
        window.Close();
    }

    private static void Run(Task task)
    {
        var start = Environment.TickCount64;
        while (!task.IsCompleted)
        {
            Pump(2);
            Assert.True(Environment.TickCount64 - start < 30000, "Timed out");
        }
        task.GetAwaiter().GetResult();
    }
}
