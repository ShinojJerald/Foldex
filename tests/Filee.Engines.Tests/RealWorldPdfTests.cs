// PDFs made the way many real ones are: an HTML page printed by Chromium (Microsoft Edge, headless), with subset
// TrueType fonts, a table, colours and italic serif text. Text editing, saving and data integrity are checked on them.
// Skipped where Edge isn't installed.

using System.Diagnostics;
using Filee.Engines.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class RealWorldPdfTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Html = """
        <!doctype html><html><head><meta charset="utf-8"><style>
        body{font-family:'Segoe UI',Arial,sans-serif;margin:48px;color:#222}
        h1{font-size:28px;color:#1a3d8f} h2{font-size:18px} p{font-size:13px;line-height:1.5}
        table{border-collapse:collapse} td{border:1px solid #999;padding:6px 10px;font-size:12px}
        .s{font-family:Georgia,serif;font-style:italic}
        </style></head><body>
        <h1>Quarterly Report</h1>
        <h2>Prepared for Northwind Traders</h2>
        <p>Revenue grew steadily this quarter across all regions.</p>
        <p>Contact: Jane Example, Finance Department</p>
        <table><tr><td>Region</td><td>Revenue</td></tr><tr><td>North</td><td>12,400</td></tr></table>
        <p class="s">Signed on behalf of the board.</p>
        </body></html>
        """;

    private static string? Edge() => new[]
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    }.FirstOrDefault(File.Exists);

    private string PrintedPdf(string? html = null)
    {
        var edge = OperatingSystem.IsWindows() ? Edge() : null;
        if (edge is null)
            Assert.Skip("Microsoft Edge is needed to print the test page");
        var dir = fx.NewFolder();
        var htmlText = html;
        html = Path.Combine(dir, "report.html");
        File.WriteAllText(html, htmlText ?? Html);
        var pdf = Path.Combine(dir, "report.pdf");
        var start = new ProcessStartInfo(edge) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--no-pdf-header-footer", $"--user-data-dir={Path.Combine(dir, "profile")}",
                     $"--print-to-pdf={pdf}", new Uri(html).AbsoluteUri })
            start.ArgumentList.Add(argument);
        using (var process = Process.Start(start)!)
            if (!process.WaitForExit(60_000))
            {
                process.Kill(true);
                Assert.Skip("Edge didn't finish printing in time");
            }
        // Edge may hand the work to another of its processes and return first: wait for the finished file.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        long size = -1;
        while (DateTime.UtcNow < deadline && !(File.Exists(pdf) && new FileInfo(pdf).Length > 0 && new FileInfo(pdf).Length == size))
        {
            size = File.Exists(pdf) ? new FileInfo(pdf).Length : -1;
            Thread.Sleep(500);
        }
        if (!File.Exists(pdf))
            Assert.Skip("Edge didn't print the page");
        return pdf;
    }

    private static List<string> Words(string path, int page = 1)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        return pig.GetPage(page).GetWords().Select(w => w.Text).ToList();
    }

    private static PdfTextLine Line(string path, string word)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        var box = pig.GetPage(1).GetWords().First(w => w.Text.StartsWith(word, StringComparison.Ordinal)).BoundingBox;
        return PdfContentEditing.FindLine(path, 0, box.Centroid.X, box.Centroid.Y) ?? throw new InvalidOperationException($"no line at {word}");
    }

    [Theory]
    [InlineData("Quarterly", "Annual Report", "Annual")]
    [InlineData("Prepared", "Prepared for Contoso Ltd", "Contoso")]
    [InlineData("Revenue", "Revenue fell slightly this quarter.", "fell")]
    [InlineData("Signed", "Approved by the board.", "Approved")]
    public void Edits_a_line_of_a_browser_printed_pdf(string lineStartsWith, string newText, string newWord)
    {
        var source = PrintedPdf();
        var before = Words(source);
        var line = Line(source, lineStartsWith);
        var output = Path.Combine(Path.GetDirectoryName(source)!, "edited.pdf");

        PdfEditor.Save(PdfEditor.AllPages(source), output,
            [new PdfMark(0, PdfMarkKind.ReplaceText, 0, 0, 0, 0, Text: newText, Line: line)]);

        var after = Words(output);
        Assert.Contains(newWord, after);
        foreach (var old in line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Except(newText.Split(' ')))
            Assert.Equal(before.Count(w => w == old) - 1, after.Count(w => w == old));
        // Every other line is still there.
        foreach (var other in new[] { "Contact:", "Region", "12,400", "Finance" }.Where(w => !line.Text.Contains(w)))
            Assert.Contains(other, after);
    }

    /// <summary>Chromium starts its pages with a scaling transform that is never restored; whatever Foldex draws on top
    /// (signatures, page numbers) must still land at the placed size and position.</summary>
    [Fact]
    public void Signatures_and_page_numbers_land_in_place_on_a_browser_printed_pdf()
    {
        var source = PrintedPdf();
        var dir = Path.GetDirectoryName(source)!;
        var ink = Path.Combine(dir, "ink.png");
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Black, 200, 60))
            image.Write(ink);
        var output = Path.Combine(dir, "signed.pdf");
        PdfEditor.Save(PdfEditor.AllPages(source), output,
            [new PdfMark(0, PdfMarkKind.Image, 300, 500, 150, 45, Text: ink)],
            new PdfStamps { PageNumberFormat = "Page {page}", PageNumberPosition = PdfStampPosition.BottomRight });

        using var page = PDFtoImage.Conversion.ToImage(File.ReadAllBytes(output), 0, null, new PDFtoImage.RenderOptions(Dpi: 72));
        bool Dark(int x, int y) => page.GetPixel(x, y) is var c && c.Red < 60 && c.Green < 60 && c.Blue < 60;
        Assert.True(Dark(310, 505) && Dark(440, 540) && Dark(375, 522), "signature not where it was placed");
        Assert.False(Dark(290, 495) || Dark(460, 550), "signature larger than placed");
        using var pig = UglyToad.PdfPig.PdfDocument.Open(output);
        var number = pig.GetPage(1).GetWords().Single(w => w.Text == "Page");
        Assert.True(number.BoundingBox.Bottom < 60 && number.BoundingBox.Left > pig.GetPage(1).Width / 2, $"page number at {number.BoundingBox}");
    }

    /// <summary>A study guide like the one in the bug report: headings and justified paragraphs inside shaded boxes, over two
    /// pages. Clicking any word of any line (at its shown position, as the editor passes it) must find that line.</summary>
    [Fact]
    public void Every_line_of_a_two_page_shaded_document_can_be_picked()
    {
        var box = "<div class='b'>I'm a Data Analyst with over two years of professional experience focused on data analytics, business intelligence, scraping, and data visualization. In my current role I work across the data lifecycle, from collection through SQL and Python transformation, analysis, visualization and workflow automation, using SQL Server, Power BI and Power Automate every day.</div>";
        var source = PrintedPdf($$"""
            <!doctype html><html><head><meta charset="utf-8"><style>
            body{font-family:Arial,sans-serif;margin:40px;color:#333;font-size:12px}
            h2{color:#2b4c7e;font-size:22px} h3{color:#2e8b57;font-size:15px}
            .b{background:#eef4fb;border-left:3px solid #9cc;padding:8px 12px;text-align:justify;line-height:1.5;margin-bottom:10px}
            </style></head><body>
            <h2>2. Exact Scripts to Memorize</h2><p>These are the answers you should know almost word-for-word.</p>
            {{string.Concat(Enumerable.Range(1, 14).Select(i => $"<h3>Question {i}: tell me about yourself</h3>{box}"))}}
            </body></html>
            """);
        using var pig = UglyToad.PdfPig.PdfDocument.Open(source);
        Assert.True(pig.NumberOfPages >= 2, "test document should span two pages");
        var missed = new List<string>();
        var checkedWords = 0;
        for (var p = 1; p <= pig.NumberOfPages; p++)
        {
            var page = pig.GetPage(p);
            // What the editor knows: the page as PDFium renders it, clicks from its top-left corner.
            var height = PDFtoImage.Conversion.GetPageSize(File.ReadAllBytes(source), p - 1).Height;
            foreach (var word in page.GetWords().Where(w => w.Text.Length > 2).Where((_, i) => i % 3 == 0))
            {
                checkedWords++;
                var c = word.BoundingBox.Centroid;
                var hit = PdfContentEditing.HitTest(source, p - 1, c.X - page.CropBox.Bounds.Left, height - (c.Y - page.CropBox.Bounds.Bottom));
                if (hit.Line is null || !hit.Line.Text.Contains(word.Text, StringComparison.Ordinal))
                    missed.Add($"p{p} '{word.Text}' at {c.X:0},{c.Y:0}: {hit.Miss} {hit.Line?.Text}");
            }
        }
        Assert.True(missed.Count == 0, $"{missed.Count} of {checkedWords} words not found:\n" + string.Join("\n", missed.Take(15)));
    }

    [Fact]
    public void Saving_keeps_the_source_and_a_failed_save_leaves_the_target_alone()
    {
        var source = PrintedPdf();
        var dir = Path.GetDirectoryName(source)!;
        var sourceBytes = File.ReadAllBytes(source);
        // Unicode and spaces in the name.
        var unicode = Path.Combine(dir, "Résumé 文件 (copy).pdf");
        PdfEditor.Save(PdfEditor.AllPages(source), unicode,
            [new PdfMark(0, PdfMarkKind.Note, 100, 100, 0, 0, Text: "Checked ✓")]);
        Assert.Contains("Quarterly", Words(unicode));
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));

        // A read-only target: the save fails with a message, the target keeps its bytes, no temporary file is left.
        var locked = Path.Combine(dir, "locked.pdf");
        File.Copy(source, locked);
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        try
        {
            var ex = Assert.Throws<PdfEditException>(() => PdfEditor.Save(PdfEditor.AllPages(source), locked,
                [new PdfMark(0, PdfMarkKind.Rectangle, 10, 10, 50, 50)]));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            Assert.Equal(sourceBytes, File.ReadAllBytes(locked));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            File.SetAttributes(locked, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_large_document_opens_edits_and_saves_in_reasonable_time()
    {
        var dir = fx.NewFolder();
        var builder = new PdfBuilder();
        for (var i = 1; i <= 400; i++)
        {
            builder.NewPage();
            builder.Line($"Page {i} of the large document", 14, builder.Font(), 72, 100);
        }
        var source = builder.Save(Path.Combine(dir, "large.pdf"));
        var output = Path.Combine(dir, "large-edited.pdf");
        var watch = Stopwatch.StartNew();

        var pages = PdfEditor.AllPages(source).Reverse().ToList();
        PdfEditor.Save(pages, output, [new PdfMark(0, PdfMarkKind.Highlight, 70, 85, 200, 20), new PdfMark(399, PdfMarkKind.Note, 50, 50, 0, 0, Text: "end")],
            new PdfStamps { PageNumberFormat = "{page} / {total}" });

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
        using var saved = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(400, saved.PageCount);
        Assert.Contains("400", Words(output, 1));
        Assert.Contains("1 / 400", string.Join(' ', Words(output, 1)).Replace(" / ", "/").Replace("/", " / "));
    }
}
