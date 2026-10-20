// Signature pictures for the PDF editor's Signature Studio: a rendered (typed / drawn) or uploaded signature becomes a
// PNG with a transparent background, trimmed to the ink, so it can be placed on a page at its own aspect ratio.

using ImageMagick;

namespace Filee.Engines.Magick;

public static class SignatureImage
{
    /// <summary>Uploaded pictures larger than this are refused before decoding.</summary>
    public const long MaxFileBytes = 25L * 1024 * 1024;
    /// <summary>Longest side of the saved signature; larger pictures are scaled down.</summary>
    public const int MaxSide = 2000;

    /// <summary>
    /// Reads a signature picture and writes it to <paramref name="outputPng"/>: turned upright, a white or near-white
    /// background made transparent when <paramref name="removeWhite"/>, trimmed to the ink with a small margin and scaled
    /// down to <see cref="MaxSide"/>. Returns the saved size in pixels. Unreadable, empty or oversized files throw
    /// <see cref="InvalidDataException"/> with a message for people.
    /// </summary>
    public static (int Width, int Height) Prepare(string inputPath, string outputPng, bool removeWhite)
    {
        var info = new FileInfo(inputPath);
        if (!info.Exists)
            throw new InvalidDataException($"“{info.Name}” was not found.");
        if (info.Length > MaxFileBytes)
            throw new InvalidDataException($"“{info.Name}” is larger than {MaxFileBytes / 1024 / 1024} MB. Use a smaller picture of the signature.");
        try
        {
            // Refuse decompression bombs before decoding the pixels.
            var ping = new MagickImageInfo(inputPath);
            if ((long)ping.Width * ping.Height > 100_000_000)
                throw new InvalidDataException($"“{info.Name}” is too large ({ping.Width} × {ping.Height} pixels).");
            using var image = new MagickImage(inputPath);
            image.AutoOrient();
            image.Strip();
            image.Alpha(AlphaOption.Set);
            if (removeWhite)
            {
                image.ColorFuzz = new Percentage(12);
                image.Transparent(MagickColors.White);
            }
            return Finish(image, outputPng, info.Name);
        }
        catch (MagickException ex)
        {
            throw new InvalidDataException($"“{info.Name}” couldn't be read as a picture ({ex.Message.Split('\n')[0]}).", ex);
        }
    }

    /// <summary>A rendered signature (already transparent) trimmed and saved like an uploaded one.</summary>
    public static (int Width, int Height) PrepareRendered(byte[] png, string outputPng)
    {
        using var image = new MagickImage(png);
        return Finish(image, outputPng, "The signature");
    }

    private static (int, int) Finish(MagickImage image, string outputPng, string name)
    {
        image.Trim();
        image.ResetPage();
        // Trimming a picture with no ink leaves a pixel or two.
        if (image.Width <= 2 || image.Height <= 2)
            throw new InvalidDataException($"{name} has no visible ink.");
        if (Math.Max(image.Width, image.Height) > MaxSide)
            image.Resize(new MagickGeometry(MaxSide, MaxSide) { Greater = true });
        var margin = (uint)Math.Max(2, Math.Max(image.Width, image.Height) / 50);
        image.BorderColor = MagickColors.Transparent;
        image.Border(margin);
        image.Format = MagickFormat.Png32;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPng))!);
        image.Write(outputPng);
        return ((int)image.Width, (int)image.Height);
    }
}
