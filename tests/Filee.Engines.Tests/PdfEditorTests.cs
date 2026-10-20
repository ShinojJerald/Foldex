using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Filee.Engines.Tests;

/// <summary>The PDF workspace's page operations, marks, stamps and security, checked on the written files.</summary>
public class PdfEditorTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A PDF whose pages say "Page 1", "Page 2", … ; page sizes alternate between A4 portrait and a wide page.</summary>
    private static string MakePdf(string folder, string name, int pages, string prefix = "Page", string? title = null)
    {
        var builder = new PdfBuilder();
        for (var i = 1; i <= pages; i++)
        {
            var (w, h) = i % 2 == 1 ? (595.0, 842.0) : (842.0, 400.0);
            builder.NewPage(w, h);
            builder.Line($"{prefix} {i}", 24, builder.Font(), 72, 100);
        }
        var path = builder.Save(Path.Combine(folder, name));
        if (title is not null)
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            document.Info.Title = title;
            document.Info.Author = "Someone Private";
            document.Save(path);
        }
        return path;
    }

    private static List<string> PageTexts(string path, string? password = null)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path, new UglyToad.PdfPig.ParsingOptions { Password = password ?? "" });
        // Spaces dropped: PdfPig splits words on turned pages into letters.
        return pig.GetPages().Select(p => string.Concat(p.GetWords().Select(w => w.Text))).ToList();
    }

    private static SKBitmap Render(string path, int page, string? password = null) =>
        PDFtoImage.Conversion.ToImage(File.ReadAllBytes(path), page, password, new PDFtoImage.RenderOptions(Dpi: 72, WithAnnotations: true));

    private static bool IsDark(SKColor c) => c.Red < 120 && c.Green < 120 && c.Blue < 120;

    [Fact]
    public void Reorders_deletes_duplicates_rotates_and_inserts_pages()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "a.pdf", 3);
        var b = MakePdf(dir, "b.pdf", 2, "Other");
        var output = Path.Combine(dir, "out.pdf");

        // 3, 1 (turned), 1 again, other 2 inserted; page 2 of a deleted.
        PdfEditor.Save([new(a, 2), new(a, 0, 90), new(a, 0), new(b, 1)], output);

        Assert.Equal(["Page3", "Page1", "Page1", "Other2"], PageTexts(output));
        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal([0, 90, 0, 0], result.Pages.Cast<PdfPage>().Select(p => p.Rotate));
        // Sizes stay as they were.
        Assert.Equal(842, result.Pages[3].Width.Point, 0);
        Assert.Equal(400, result.Pages[3].Height.Point, 0);
        Assert.Equal(595, result.Pages[0].Width.Point, 0);
        // The sources are untouched.
        Assert.Equal(["Page1", "Page2", "Page3"], PageTexts(a));
    }

    [Fact]
    public void Merges_files_in_order()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "a.pdf", 2);
        var b = MakePdf(dir, "b.pdf", 1, "Other");
        var output = Path.Combine(dir, "merged.pdf");

        PdfEditor.Merge([b, a], output);

        Assert.Equal(["Other1", "Page1", "Page2"], PageTexts(output));
    }

    [Fact]
    public void Splits_by_ranges_and_by_size()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "doc.pdf", 5);
        var outDir = Directory.CreateDirectory(Path.Combine(dir, "parts")).FullName;

        var parts = PdfEditor.Split(a, "1-2, 4, 5-", outDir);

        Assert.Equal(["doc (1-2).pdf", "doc (4).pdf", "doc (5-).pdf"], parts.Select(Path.GetFileName));
        Assert.Equal(["Page1", "Page2"], PageTexts(parts[0]));
        Assert.Equal(["Page4"], PageTexts(parts[1]));
        Assert.Equal(["Page5"], PageTexts(parts[2]));

        var every = PdfEditor.Split(a, "every:2", outDir);
        Assert.Equal(3, every.Count);
        Assert.Equal(["Page5"], PageTexts(every[2]));
        // A second split never overwrites the first.
        var again = PdfEditor.Split(a, "4", outDir);
        Assert.Equal("doc (4) (2).pdf", Path.GetFileName(again.Single()));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("0-2")]
    [InlineData("abc")]
    [InlineData("")]
    public void Rejects_ranges_outside_the_document(string ranges)
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "doc.pdf", 3);
        Assert.Throws<PdfEditException>(() => PdfEditor.Split(a, ranges, dir));
    }

    [Fact]
    public void Protects_opens_and_removes_a_password()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "a.pdf", 2);
        var locked = Path.Combine(dir, "locked.pdf");

        PdfEditor.Save(PdfEditor.AllPages(a), locked, options: new PdfSaveOptions { UserPassword = "s3cret" });

        var error = Assert.Throws<PdfEditException>(() => PdfEditor.PageCount(locked));
        Assert.True(error.NeedsPassword);
        Assert.True(Assert.Throws<PdfEditException>(() => PdfEditor.PageCount(locked, "wrong")).NeedsPassword);
        Assert.Equal(2, PdfEditor.PageCount(locked, "s3cret"));
        Assert.Equal(["Page1", "Page2"], PageTexts(locked, "s3cret"));

        var unlocked = Path.Combine(dir, "unlocked.pdf");
        var passwords = new Dictionary<string, string> { [locked] = "s3cret" };
        PdfEditor.Save(PdfEditor.AllPages(locked, "s3cret"), unlocked, options: new PdfSaveOptions { SourcePasswords = passwords });
        Assert.Equal(2, PdfEditor.PageCount(unlocked));
    }

    [Fact]
    public void Keeps_or_removes_document_information()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "a.pdf", 1, title: "Quarterly report");
        var kept = Path.Combine(dir, "kept.pdf");
        var removed = Path.Combine(dir, "removed.pdf");

        PdfEditor.Save(PdfEditor.AllPages(a), kept);
        PdfEditor.Save(PdfEditor.AllPages(a), removed, options: new PdfSaveOptions { RemoveMetadata = true });

        Assert.Equal("Quarterly report", PdfEditor.ReadInfo(kept)["Title"]);
        var info = PdfEditor.ReadInfo(removed);
        Assert.False(info.ContainsKey("Title"));
        Assert.False(info.ContainsKey("Author"));
        // Verified on the bytes too: the author's name is nowhere in the file.
        Assert.DoesNotContain("Someone Private", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(removed)));
    }

    [Fact]
    public void Writes_annotations_that_other_viewers_show()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "a.pdf", 1);
        var output = Path.Combine(dir, "marked.pdf");
        PdfMark[] marks =
        [
            new(0, PdfMarkKind.Highlight, 60, 70, 200, 40, "#FFFF00"),
            new(0, PdfMarkKind.Rectangle, 300, 300, 100, 80, "#FF0000", LineWidth: 4),
            new(0, PdfMarkKind.Ellipse, 100, 500, 120, 60, "#0000FF"),
            new(0, PdfMarkKind.Line, 100, 700, 300, -40, "#00AA00"),
            new(0, PdfMarkKind.Note, 500, 50, 0, 0, "#FFD400", "Check this"),
            new(0, PdfMarkKind.FreeText, 100, 600, 200, 30, "#000000", "Approved (draft)", FontSize: 14),
            new(0, PdfMarkKind.Underline, 60, 70, 200, 40, "#FF00FF"),
            new(0, PdfMarkKind.StrikeOut, 60, 70, 200, 40, "#00FFFF"),
        ];

        PdfEditor.Save(PdfEditor.AllPages(a), output, marks);

        // Real annotations, reopened by PDFsharp.
        using (var result = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            var annots = result.Pages[0].Elements.GetArray("/Annots")!;
            var subtypes = annots.Elements.Select(e => ((PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)e).Value).Elements.GetName("/Subtype")).ToList();
            Assert.Equal(["/Highlight", "/Square", "/Circle", "/Line", "/Text", "/FreeText", "/Underline", "/StrikeOut"], subtypes);
            var note = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)annots.Elements[4]).Value;
            Assert.Equal("Check this", note.Elements.GetString("/Contents"));
            // The highlight follows the words "Page 1" (baseline 100 pt from the top, 24 pt) instead of the drawn box.
            var highlight = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)annots.Elements[0]).Value;
            var rect = highlight.Elements.GetRectangle("/Rect");
            Assert.InRange(rect.X1, 70, 80);
            Assert.InRange(842 - rect.Y2, 75, 90);
        }

        // PDFium (Chrome's PDF engine) draws them: red frame, blue ellipse, yellow highlight.
        using var bitmap = Render(output, 0);
        Assert.Equal(SKColors.Red, Pixel(bitmap, 300, 340));
        Assert.True(Pixel(bitmap, 160, 500).Blue > 200 && Pixel(bitmap, 160, 500).Red < 80);
        var highlighted = Enumerable.Range(72, 60).SelectMany(x => Enumerable.Range(78, 20).Select(y => Pixel(bitmap, x, y)));
        Assert.Contains(highlighted, c => c.Red > 200 && c.Green > 200 && c.Blue < 150);
        Assert.Contains(Enumerable.Range(100, 100).Select(x => Pixel(bitmap, x, 610)), IsDark); // free text
    }

    private static SKColor Pixel(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y);

    [Fact]
    public void Marks_land_where_they_are_shown_on_turned_pages()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "a.pdf", 1); // A4 portrait; turned it shows 842 × 595
        var output = Path.Combine(dir, "turned.pdf");

        PdfEditor.Save([new(a, 0, 90)], output, [new(0, PdfMarkKind.Rectangle, 600, 60, 120, 80, "#FF0000", LineWidth: 6)]);

        using var bitmap = Render(output, 0);
        Assert.Equal(842, bitmap.Width);
        Assert.Equal(595, bitmap.Height);
        Assert.Equal(SKColors.Red, Pixel(bitmap, 600, 100)); // left edge of the frame
        Assert.NotEqual(SKColors.Red, Pixel(bitmap, 660, 100)); // inside it
    }

    [Fact]
    public void Adds_page_numbers_header_footer_and_watermark()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "a.pdf", 3);
        var output = Path.Combine(dir, "stamped.pdf");

        PdfEditor.Save([new(a, 0), new(a, 1), new(a, 2, 90)], output, stamps: new PdfStamps
        {
            PageNumberFormat = "{page} / {total}",
            Header = "Confidential",
            Watermark = "DRAFT",
        });

        var texts = PageTexts(output);
        Assert.Contains("1/3", texts[0]);
        Assert.Contains("3/3", texts[2]);
        Assert.Contains("Confidential", texts[1]);
        Assert.True(texts[0].Contains("DRAFT") || texts[0].Contains("TFARD"), texts[0]); // diagonal: read either way

        // On the turned page the number is at the bottom of the page as shown.
        using var bitmap = Render(output, 2);
        var bottom = Enumerable.Range(bitmap.Width / 2 - 30, 60).SelectMany(x => Enumerable.Range(bitmap.Height - 30, 25).Select(y => Pixel(bitmap, x, y)));
        Assert.Contains(bottom, IsDark);
    }

    [Fact]
    public void Places_a_picture_on_a_page()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "a.pdf", 1);
        var picture = PdfBuilder.Photo(dir, "stamp.jpg", ImageMagick.MagickFormat.Jpeg);
        var output = Path.Combine(dir, "picture.pdf");

        PdfEditor.Save(PdfEditor.AllPages(a), output, [new(0, PdfMarkKind.Image, 300, 400, 200, 120, Text: picture)]);

        using var bitmap = Render(output, 0);
        Assert.NotEqual(SKColors.White, Pixel(bitmap, 400, 460));
        Assert.Equal(SKColors.White, Pixel(bitmap, 250, 460));
    }

    [Fact]
    public void Can_save_over_a_source_and_leaves_no_temporary_files()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "a.pdf", 3);

        PdfEditor.Save([new(a, 2), new(a, 1)], a);

        Assert.Equal(["Page3", "Page2"], PageTexts(a));
        Assert.Equal(["a.pdf"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void A_broken_file_fails_clearly_and_writes_nothing()
    {
        var dir = fx.NewFolder();
        var broken = Path.Combine(dir, "broken.pdf");
        File.WriteAllText(broken, "%PDF-1.7\nthis is not really a PDF");
        var output = Path.Combine(dir, "out.pdf");

        var error = Assert.Throws<PdfEditException>(() => PdfEditor.Save([new(broken, 0)], output));
        Assert.Contains("broken.pdf", error.Message);
        Assert.Equal(["broken.pdf"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Cancelling_writes_nothing()
    {
        var dir = fx.NewFolder();
        var a = MakePdf(dir, "a.pdf", 2);
        var output = Path.Combine(dir, "out.pdf");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PdfEditor.Save(PdfEditor.AllPages(a), output, cancellationToken: cancel.Token));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(360, 0)]
    public void Normalizes_rotation(int degrees, int expected) => Assert.Equal(expected, PdfEditor.NormalizeRotation(degrees));
}
