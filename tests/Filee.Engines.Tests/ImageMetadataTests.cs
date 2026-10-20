using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class ImageMetadataTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A JPEG with a camera name, a comment and a GPS position (37.5° N, 127.25° E).</summary>
    private static string PhotoWithGps(string folder)
    {
        using var image = new MagickImage(MagickColors.SkyBlue, 64, 48);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "TestCam");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(37, 1), new Rational(30, 1), new Rational(0, 1)]);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(127, 1), new Rational(15, 1), new Rational(0, 1)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        image.SetProfile(exif);
        image.SetAttribute("comment", "my secret place");
        var path = Path.Combine(folder, "photo.jpg");
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    [Fact]
    public void Reads_camera_and_gps_metadata()
    {
        var report = ImageMetadata.Read(PhotoWithGps(fx.NewFolder()));

        Assert.Contains(report.Entries, e => e.Name == "Make" && e.Value == "TestCam");
        Assert.True(report.HasGps);
        Assert.Equal(37.5, report.Latitude!.Value, 3);
        Assert.Equal(127.25, report.Longitude!.Value, 3);
    }

    [Fact]
    public void Writes_a_copy_without_metadata_and_proves_it()
    {
        var dir = fx.NewFolder();
        var source = PhotoWithGps(dir);
        var output = Path.Combine(dir, "clean.jpg");

        var check = ImageMetadata.WriteWithout(source, output);

        Assert.True(check.IsEmpty);
        using var reread = new MagickImage(output);
        Assert.Null(reread.GetExifProfile());
        Assert.DoesNotContain("my secret place", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(output)));
        Assert.Equal(64u, reread.Width);
        // The original keeps its metadata.
        Assert.True(ImageMetadata.Read(source).HasGps);
    }
}
