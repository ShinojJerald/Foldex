// Picture metadata for the File utilities page: EXIF (camera, dates, GPS position), IPTC and XMP read with ImageMagick,
// and a copy written without them that is read back to prove nothing is left.

using System.Globalization;
using ImageMagick;

namespace Filee.Engines.Magick;

public sealed record MetadataEntry(string Group, string Name, string Value);

public sealed record ImageMetadataReport(IReadOnlyList<MetadataEntry> Entries, double? Latitude, double? Longitude)
{
    public bool HasGps => Latitude is not null && Longitude is not null;
    public bool IsEmpty => Entries.Count == 0;
}

public static class ImageMetadata
{
    /// <summary>The metadata of a picture (first frame).</summary>
    public static ImageMetadataReport Read(string path)
    {
        using var image = new MagickImage();
        image.Ping(path);
        var entries = new List<MetadataEntry>();
        double? latitude = null, longitude = null;

        if (image.GetExifProfile() is { } exif)
        {
            foreach (var value in exif.Values)
            {
                if (value.Tag == ExifTag.MakerNote || value.GetValue() is not { } raw)
                    continue;
                entries.Add(new MetadataEntry("EXIF", value.Tag.ToString(), Format(raw)));
            }
            latitude = Coordinate(exif, ExifTag.GPSLatitude, ExifTag.GPSLatitudeRef, "S");
            longitude = Coordinate(exif, ExifTag.GPSLongitude, ExifTag.GPSLongitudeRef, "W");
        }
        if (image.GetIptcProfile() is { } iptc)
            entries.AddRange(iptc.Values.Select(v => new MetadataEntry("IPTC", v.Tag.ToString(), v.Value)));
        if (image.GetXmpProfile() is { } xmp)
            entries.Add(new MetadataEntry("XMP", "XMP packet", $"{xmp.ToByteArray().Length} bytes"));
        foreach (var name in image.ProfileNames.Where(n => n is not ("exif" or "iptc" or "xmp" or "icc" or "icm" or "8bim")))
            entries.Add(new MetadataEntry("Profile", name, $"{image.GetProfile(name)?.ToByteArray().Length ?? 0} bytes"));
        foreach (var attribute in image.AttributeNames.Where(a => a.StartsWith("comment", StringComparison.OrdinalIgnoreCase)))
            entries.Add(new MetadataEntry("Comment", attribute, image.GetAttribute(attribute) ?? ""));
        return new ImageMetadataReport(entries, latitude, longitude);
    }

    /// <summary>
    /// Writes a copy of <paramref name="path"/> without EXIF, IPTC, XMP or comments (the colour profile stays, so colours
    /// don't change; the picture is turned upright first, as the orientation tag goes too). Returns the report of the
    /// copy as read back, which must be empty.
    /// </summary>
    public static ImageMetadataReport WriteWithout(string path, string outputPath)
    {
        using (var images = new MagickImageCollection(path))
        {
            foreach (var frame in images)
            {
                frame.AutoOrient();
                var icc = frame.GetColorProfile();
                frame.Strip();
                if (icc is not null)
                    frame.SetProfile(icc);
            }
            images.Write(outputPath);
        }
        var check = Read(outputPath);
        if (!check.IsEmpty)
            throw new InvalidOperationException($"Metadata is still in the copy ({string.Join(", ", check.Entries.Select(e => e.Name))}).");
        return check;
    }

    private static double? Coordinate(IExifProfile exif, ExifTag<Rational[]> tag, ExifTag<string> reference, string negative)
    {
        if (exif.GetValue(tag)?.Value is not { Length: 3 } parts)
            return null;
        var value = parts[0].ToDouble() + parts[1].ToDouble() / 60 + parts[2].ToDouble() / 3600;
        if (double.IsNaN(value))
            return null;
        return string.Equals(exif.GetValue(reference)?.Value, negative, StringComparison.OrdinalIgnoreCase) ? -value : value;
    }

    private static string Format(object raw) => raw switch
    {
        string s => s.TrimEnd('\0'),
        Rational r => r.ToDouble().ToString("0.####", CultureInfo.InvariantCulture),
        Rational[] rs => string.Join(" ", rs.Select(r => r.ToDouble().ToString("0.####", CultureInfo.InvariantCulture))),
        SignedRational r => r.ToDouble().ToString("0.####", CultureInfo.InvariantCulture),
        byte[] bytes => bytes.Length <= 16 ? Convert.ToHexString(bytes) : $"{bytes.Length} bytes",
        Array array => string.Join(" ", array.Cast<object>().Take(16)),
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "",
    };
}
