using Filee.Engines.Magick;
using Filee.Engines.Pdf;
using ImageMagick;
using ImageMagick.Drawing;
using SkiaSharp;

namespace Filee.Engines.Tests;

/// <summary>Signature pictures (transparent, trimmed, safe with bad input) and their placement in saved PDFs.</summary>
public class SignatureTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>An "L" of black ink on white: the top-right part has no ink.</summary>
    private static string InkOnWhite(string folder, int width = 600, int height = 200, string name = "scan.jpg")
    {
        var path = Path.Combine(folder, name);
        using var image = new MagickImage(MagickColors.White, (uint)width, (uint)height);
        new Drawables().FillColor(MagickColors.Black)
            .Rectangle(40, 20, 40 + width / 10.0, height - 20)
            .Rectangle(40, height - 60, width - 40, height - 20)
            .Draw(image);
        image.Write(path);
        return path;
    }

    [Fact]
    public void Makes_white_transparent_and_trims_to_the_ink()
    {
        var dir = fx.NewFolder();
        var output = Path.Combine(dir, "sig.png");

        var (w, h) = SignatureImage.Prepare(InkOnWhite(dir), output, removeWhite: true);

        using var result = new MagickImage(output);
        Assert.Equal(MagickFormat.Png, result.Format);
        Assert.Equal((uint)w, result.Width);
        Assert.True(w < 600 && h < 200, $"{w}×{h} not trimmed");
        Assert.True(result.HasAlpha);
        using var pixels = result.GetPixels();
        Assert.Equal(0, pixels.GetPixel(w - 5, 5)!.ToColor()!.A);                 // top right: transparent
        var ink = pixels.GetPixel(w / 20 + 4, h / 2)!.ToColor()!;                    // the upright bar
        Assert.True(ink.A > 200 && ink.R < 60, $"ink {ink}");
    }

    [Fact]
    public void Keeps_the_background_when_asked_and_scales_big_pictures_down()
    {
        var dir = fx.NewFolder();
        var output = Path.Combine(dir, "big.png");

        var (w, h) = SignatureImage.Prepare(InkOnWhite(dir, 6000, 1500, "big.png"), output, removeWhite: false);

        Assert.Equal(SignatureImage.MaxSide, Math.Max(w, h) - 2 * Math.Max(2, SignatureImage.MaxSide / 50));
        using var result = new MagickImage(output);
        using var pixels = result.GetPixels();
        // White kept (an opaque picture was asked for), margin transparent.
        Assert.Equal(255, pixels.GetPixel(w / 2, h / 2 - (int)(h * 0.3))!.ToColor()!.A);
        Assert.Equal(0, pixels.GetPixel(0, 0)!.ToColor()!.A);
    }

    [Fact]
    public void Refuses_broken_blank_and_missing_pictures()
    {
        var dir = fx.NewFolder();
        var fake = Path.Combine(dir, "fake.png");
        File.WriteAllText(fake, "not a picture");
        var blank = Path.Combine(dir, "blank.png");
        using (var white = new MagickImage(MagickColors.White, 300, 100))
            white.Write(blank);
        var output = Path.Combine(dir, "out.png");

        Assert.Throws<InvalidDataException>(() => SignatureImage.Prepare(fake, output, true));
        Assert.Throws<InvalidDataException>(() => SignatureImage.Prepare(blank, output, true));
        Assert.Throws<InvalidDataException>(() => SignatureImage.Prepare(Path.Combine(dir, "missing.png"), output, true));
        Assert.False(File.Exists(output));
    }

    private static SKBitmap Render(string path, int page) =>
        PDFtoImage.Conversion.ToImage(File.ReadAllBytes(path), page, null, new PDFtoImage.RenderOptions(Dpi: 72, WithAnnotations: true));

    private static bool Dark(SKColor c) => c.Red < 100 && c.Green < 100 && c.Blue < 100;
    private static bool White(SKColor c) => c.Red > 235 && c.Green > 235 && c.Blue > 235;

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public void A_signature_lands_where_it_was_placed_without_an_opaque_box(int rotation)
    {
        var dir = fx.NewFolder();
        var signature = Path.Combine(dir, "sig.png");
        var (w, h) = SignatureImage.Prepare(InkOnWhite(dir), signature, removeWhite: true);
        var builder = new PdfBuilder();
        builder.NewPage(595, 842);
        builder.Line("Signed here", 12, builder.Font(), 72, 100);
        builder.NewPage(842, 595);
        builder.Line("Second page", 12, builder.Font(), 72, 100);
        var source = builder.Save(Path.Combine(dir, "doc.pdf"));
        var output = Path.Combine(dir, "signed.pdf");

        // On the second page (shown turned by the rotation), at its aspect ratio.
        double x = 200, y = 300, width = 180, height = 180.0 * h / w;
        PdfEditor.Save([new(source, 0), new(source, 1, rotation)], output,
            [new PdfMark(1, PdfMarkKind.Image, x, y, width, height, Text: signature)]);

        foreach (var file in new[] { output, Resave(output, dir) })
        {
            using var page = Render(file, 1);
            // Upright bar (left), bottom bar, and the empty top-right stays the page's white.
            Assert.True(Dark(page.GetPixel((int)(x + width * 0.12), (int)(y + height / 2))), $"bar, {rotation}°");
            Assert.True(Dark(page.GetPixel((int)(x + width / 2), (int)(y + height * 0.78))), $"bottom, {rotation}°");
            Assert.True(White(page.GetPixel((int)(x + width * 0.8), (int)(y + height * 0.25))), $"no box, {rotation}°");
            using var first = Render(file, 0);
            Assert.True(White(first.GetPixel((int)(x + width * 0.12), (int)(y + height / 2))), "first page untouched");
        }
    }

    /// <summary>Reopened and saved again (as the editor does after a save): the signature stays.</summary>
    private static string Resave(string path, string dir)
    {
        var again = Path.Combine(dir, "again.pdf");
        PdfEditor.Save(PdfEditor.AllPages(path), again);
        return again;
    }
}
