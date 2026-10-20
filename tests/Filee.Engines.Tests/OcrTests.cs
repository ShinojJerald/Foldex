using Filee.Engines.Ocr;
using Filee.Engines.Pdf;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Drawing;

namespace Filee.Engines.Tests;

/// <summary>OCR with the engine built into Windows; skipped where Windows has no recognition language.</summary>
public class OcrTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static async Task RequireOcrAsync()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Windows OCR only");
        if ((await WindowsOcr.LanguagesAsync(TestContext.Current.CancellationToken)).Count == 0)
            Assert.Skip("Windows has no OCR language installed");
    }

    /// <summary>A "scan": black text drawn into a picture, nothing else.</summary>
    private static string TextPicture(string folder, string text)
    {
        using var image = new MagickImage(MagickColors.White, 1700, 400);
        new Drawables().Font("Arial").FontPointSize(90).FillColor(MagickColors.Black).Text(60, 220, text).Draw(image);
        var path = Path.Combine(folder, "scan.png");
        image.Write(path, MagickFormat.Png);
        return path;
    }

    [Fact]
    public async Task Reads_the_text_of_a_picture()
    {
        await RequireOcrAsync();
        var picture = TextPicture(fx.NewFolder(), "Invoice 2026 Total 42");

        var page = (await WindowsOcr.RecognizeAsync([picture], null, TestContext.Current.CancellationToken)).Single();

        Assert.Equal(1700, page.Width);
        Assert.Contains("Invoice", page.Text);
        Assert.Contains("2026", page.Text);
        var word = page.Lines.SelectMany(l => l.Words).First(w => w.Text == "Invoice");
        Assert.InRange(word.X, 40, 120);
    }

    [Fact]
    public async Task Makes_a_scanned_pdf_searchable_without_changing_how_it_looks()
    {
        await RequireOcrAsync();
        var dir = fx.NewFolder();
        var picture = TextPicture(dir, "Quarterly report draft for review");
        var scanned = Path.Combine(dir, "scanned.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using var gfx = XGraphics.FromPdfPage(page);
            using var image = XImage.FromFile(picture);
            gfx.DrawImage(image, 36, 100, 540, 127);
            document.Save(scanned);
        }
        string Text(string path)
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
            return string.Join(" ", pig.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        }
        Assert.Equal("", Text(scanned).Trim()); // only a picture

        var output = Path.Combine(dir, "searchable.pdf");
        var result = await PdfOcr.MakeSearchableAsync(scanned, output, null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.RecognisedPages);
        Assert.True(result.Words >= 3);
        var text = Text(output);
        Assert.Contains("Quarterly", text);
        Assert.Contains("report", text);
        // The words sit where the picture shows them: "Quarterly" starts near the left of the picture.
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(output))
        {
            var word = pig.GetPage(1).GetWords().First(w => w.Text.Contains("Quarterly"));
            Assert.InRange(word.BoundingBox.Left, 36, 90);
            Assert.InRange(792 - word.BoundingBox.Top, 100, 230);
        }
        // Invisible: the page looks the same as before.
        using var before = PDFtoImage.Conversion.ToImage(File.ReadAllBytes(scanned), 0, null, new PDFtoImage.RenderOptions(Dpi: 50));
        using var after = PDFtoImage.Conversion.ToImage(File.ReadAllBytes(output), 0, null, new PDFtoImage.RenderOptions(Dpi: 50));
        var differing = 0;
        for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
                if (before.GetPixel(x, y) != after.GetPixel(x, y))
                    differing++;
        Assert.True(differing < before.Width * before.Height / 200, $"{differing} pixels changed");

        // A PDF that already has text is copied without OCR.
        var again = await PdfOcr.MakeSearchableAsync(output, Path.Combine(dir, "again.pdf"), null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, again.RecognisedPages);
    }

    [Fact]
    public void Parses_one_picture_or_many() =>
        Assert.Equal(2, WindowsOcr.Parse("""[{"width":10,"height":5,"lines":[]},{"width":3,"height":4,"lines":{"text":"a","words":{"text":"a","x":0,"y":0,"w":1,"h":1}}}]""")
            .Count);
}
