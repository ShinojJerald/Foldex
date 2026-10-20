// Renders PDF pages for the PDF workspace's thumbnails and preview (PDFium through PDFtoImage), as raw BGRA pixels so the
// app needs no SkiaSharp code of its own.

using PDFtoImage;
using SkiaSharp;

#pragma warning disable CA1416 // PDFium ships for every desktop platform Filee targets

namespace Filee.Engines.Pdf;

/// <summary>
/// A rendered page: premultiplied BGRA rows of <see cref="Width"/> × 4 bytes, and the size of the page as shown (its
/// own rotation applied) in points, exact rather than rounded to pixels.
/// </summary>
public sealed record RenderedPage(byte[] Pixels, int Width, int Height, double WidthPoints, double HeightPoints);

public static class PdfPageRenderer
{
    /// <summary>Renders one page (0-based) at <paramref name="dpi"/>, annotations included.</summary>
    public static RenderedPage Render(byte[] pdf, int pageIndex, double dpi, string? password = null)
    {
        var resolution = (int)Math.Round(Math.Clamp(dpi, 4, 600));
        using var bitmap = Conversion.ToImage(pdf, pageIndex, password,
            new RenderOptions(Dpi: resolution, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White));
        var size = Conversion.GetPageSize(pdf, pageIndex, password);
        // PDFium reports the page box; the picture tells whether the page's own rotation turns it.
        var (w, h) = (bitmap.Width > bitmap.Height) == (size.Width > size.Height) ? (size.Width, size.Height) : (size.Height, size.Width);
        return ToPixels(bitmap, w, h);
    }

    private static RenderedPage ToPixels(SKBitmap bitmap, double widthPoints, double heightPoints)
    {
        using var bgra = bitmap.ColorType == SKColorType.Bgra8888 && bitmap.AlphaType == SKAlphaType.Premul
            ? null
            : new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (bgra is not null && !bitmap.CopyTo(bgra, SKColorType.Bgra8888))
            using (var canvas = new SKCanvas(bgra))
                canvas.DrawBitmap(bitmap, 0, 0);
        var source = bgra ?? bitmap;
        var pixels = new byte[source.Width * 4 * source.Height];
        // Rows may be padded: copy row by row.
        var span = source.GetPixelSpan();
        for (var y = 0; y < source.Height; y++)
            span.Slice(y * source.RowBytes, source.Width * 4).CopyTo(pixels.AsSpan(y * source.Width * 4));
        return new RenderedPage(pixels, source.Width, source.Height, widthPoints, heightPoints);
    }
}
