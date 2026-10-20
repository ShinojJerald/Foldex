// Searchable PDFs from scanned ones: every page is rendered (PDFium), read by the Windows OCR engine, and the words are
// written over the page as invisible text in their places, so the PDF can be searched and its text selected and copied.
// The page itself (the scan) is copied unchanged; pages that already have text are left alone.

using Filee.Engines.Ocr;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

#pragma warning disable CA1416 // PDFium ships for every desktop platform Filee targets

namespace Filee.Engines.Pdf;

public sealed record OcrPdfResult(int Pages, int RecognisedPages, int SkippedPages, int Words);

public static class PdfOcr
{
    /// <summary>Rendering resolution for recognition: Windows OCR reads small print reliably at 300 dpi.</summary>
    private const int Dpi = 300;
    /// <summary>A page with at least this many letters already has a text layer.</summary>
    private const int TextLetters = 25;
    private const int PagesPerRun = 8;

    public static async Task<OcrPdfResult> MakeSearchableAsync(string inputPath, string outputPath, string? language, string? password = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        var pageCount = PdfEditor.PageCount(inputPath, password);
        var needsOcr = PagesWithoutText(inputPath, password, pageCount);
        var scratch = Directory.CreateTempSubdirectory("filee-ocr-").FullName;
        var recognised = new Dictionary<int, OcrPage>();
        try
        {
            // Rendered and read in small batches: one PowerShell run each, a few pictures on disk at a time.
            foreach (var batch in needsOcr.Chunk(PagesPerRun))
            {
                var images = new List<string>();
                foreach (var index in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var image = Path.Combine(scratch, $"page-{index + 1}.png");
                    await Task.Run(() => RenderPng(bytes, index, password, image), cancellationToken);
                    images.Add(image);
                }
                var pages = await WindowsOcr.RecognizeAsync(images, language, cancellationToken);
                for (var i = 0; i < batch.Length; i++)
                    recognised[batch[i]] = pages[i];
                foreach (var image in images)
                    File.Delete(image);
                progress?.Report((double)recognised.Count / Math.Max(1, needsOcr.Count) * 0.9);
            }

            var words = 0;
            await Task.Run(() => words = Write(inputPath, outputPath, password, recognised, cancellationToken), cancellationToken);
            progress?.Report(1);
            return new OcrPdfResult(pageCount, recognised.Count(r => r.Value.Lines.Count > 0), pageCount - needsOcr.Count, words);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Indexes of the pages without a text layer of their own.</summary>
    private static List<int> PagesWithoutText(string path, string? password, int pageCount)
    {
        try
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(path, new UglyToad.PdfPig.ParsingOptions { Password = password ?? "", UseLenientParsing = true });
            return Enumerable.Range(0, pageCount).Where(i => pig.GetPage(i + 1).Letters.Count(l => !string.IsNullOrWhiteSpace(l.Value)) < TextLetters).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Enumerable.Range(0, pageCount).ToList(); // can't tell: read every page
        }
    }

    private static void RenderPng(byte[] pdf, int page, string? password, string path)
    {
        using var bitmap = PDFtoImage.Conversion.ToImage(pdf, page, password,
            new PDFtoImage.RenderOptions(Dpi: Dpi, WithAnnotations: false, BackgroundColor: SKColors.White));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>
    /// Switches the text of the content stream just drawn (only the OCR words are in it) to render mode 3: not painted,
    /// but searchable and selectable, as OCR layers are written. PDFsharp's drawing API has no render mode, and it paints
    /// a fully transparent colour anyway.
    /// </summary>
    private static void MakeTextInvisible(PdfPage page)
    {
        if (page.Contents.Elements.Count == 0
            || page.Contents.Elements[^1] is not PdfSharp.Pdf.Advanced.PdfReference { Value: PdfDictionary { Stream: { } stream } })
            throw new PdfEditException("The text layer could not be written.");
        var content = System.Text.Encoding.Latin1.GetString(stream.Value);
        stream.Value = System.Text.Encoding.Latin1.GetBytes(System.Text.RegularExpressions.Regex.Replace(content, @"\bBT\b", "BT 3 Tr"));
    }

    /// <summary>Copies the pages and adds the recognised words as invisible text. Returns the number of words.</summary>
    private static int Write(string inputPath, string outputPath, string? password, IReadOnlyDictionary<int, OcrPage> recognised, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        var words = 0;
        try
        {
            using (var source = PdfEditor.OpenSource(inputPath, password))
            using (var output = new PdfDocument())
            {
                output.Info.Title = source.Info.Title;
                output.Info.Author = source.Info.Author;
                var brush = XBrushes.Black;
                for (var i = 0; i < source.PageCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = output.AddPage(source.Pages[i]);
                    if (!recognised.TryGetValue(i, out var ocr) || ocr.Lines.Count == 0)
                        continue;
                    PdfEditor.DrawShown(page, (gfx, box) =>
                    {
                        // Picture pixels → shown points (the rendering is the page as shown).
                        var scaleX = box.ShownWidth / ocr.Width;
                        var scaleY = box.ShownHeight / ocr.Height;
                        foreach (var word in ocr.Lines.SelectMany(l => l.Words))
                        {
                            if (string.IsNullOrWhiteSpace(word.Text) || word.Width <= 0 || word.Height <= 0)
                                continue;
                            var height = word.Height * scaleY;
                            var font = new XFont("Arial", Math.Max(1, height * 0.95));
                            var measured = gfx.MeasureString(word.Text, font).Width;
                            if (measured <= 0)
                                continue;
                            var state = gfx.Save();
                            gfx.TranslateTransform(word.X * scaleX, word.Y * scaleY);
                            gfx.ScaleTransform(word.Width * scaleX / measured, 1);
                            gfx.DrawString(word.Text, font, brush, 0, 0, XStringFormats.TopLeft);
                            gfx.Restore(state);
                            words++;
                        }
                    });
                    MakeTextInvisible(page);
                }
                output.Save(temp);
            }
            File.Move(temp, outputPath, overwrite: true);
            return words;
        }
        catch (Exception ex) when (ex is not PdfEditException and not OperationCanceledException)
        {
            throw new PdfEditException($"The searchable PDF could not be written ({ex.Message}).", ex);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }
}
