// Read-only folder scans for the File utilities page: exact duplicates (same size, then SHA-256), the largest files and
// files whose content doesn't match their extension. Nothing is ever moved or deleted here.

using System.Security.Cryptography;

namespace Filee.Core.Files;

/// <summary>Files with the same content.</summary>
public sealed record DuplicateGroup(string Hash, long Size, IReadOnlyList<string> Paths)
{
    /// <summary>Space the extra copies take.</summary>
    public long Wasted => Size * (Paths.Count - 1);
}

/// <summary>A file whose bytes say it is another type than its extension.</summary>
public sealed record ExtensionMismatch(string Path, string Extension, string DetectedType, string SuggestedExtension);

public static class FileScanner
{
    /// <summary>Files under <paramref name="folder"/>, skipping folders that can't be read.</summary>
    public static IEnumerable<FileInfo> Files(string folder, bool recursive = true) =>
        new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        });

    /// <summary>Groups of identical files, biggest waste first. Only files of equal size are hashed.</summary>
    public static IReadOnlyList<DuplicateGroup> FindDuplicates(string folder, bool recursive = true, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var bySize = Files(folder, recursive).Where(f => f.Length > 0).GroupBy(f => f.Length).Where(g => g.Count() > 1).ToList();
        var total = bySize.Sum(g => g.Count());
        var done = 0;
        var groups = new List<DuplicateGroup>();
        foreach (var sameSize in bySize)
        {
            var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var file in sameSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = TryHash(file.FullName, cancellationToken);
                done++;
                progress?.Report((double)done / total);
                if (hash is null)
                    continue;
                if (!byHash.TryGetValue(hash, out var paths))
                    byHash[hash] = paths = [];
                paths.Add(file.FullName);
            }
            groups.AddRange(byHash.Where(kv => kv.Value.Count > 1).Select(kv => new DuplicateGroup(kv.Key, sameSize.Key, kv.Value)));
        }
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    /// <summary>SHA-256 of a file in hex, or null when it can't be read (in use, no access).</summary>
    public static string? TryHash(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether two files have the same content (size, then SHA-256).</summary>
    public static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;
        var hashA = TryHash(a) ?? throw new IOException($"{a} can't be read.");
        var hashB = TryHash(b) ?? throw new IOException($"{b} can't be read.");
        return hashA == hashB;
    }

    /// <summary>The <paramref name="count"/> largest files.</summary>
    public static IReadOnlyList<FileInfo> Largest(string folder, int count = 50, bool recursive = true) =>
        Files(folder, recursive).OrderByDescending(f => f.Length).Take(count).ToList();

    /// <summary>Files whose content is a known type that their extension doesn't belong to.</summary>
    public static IReadOnlyList<ExtensionMismatch> FindMismatches(string folder, bool recursive = true, CancellationToken cancellationToken = default)
    {
        var result = new List<ExtensionMismatch>();
        foreach (var file in Files(folder, recursive))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FileSignature.Detect(file.FullName) is not { } type)
                continue;
            var extension = file.Extension.TrimStart('.').ToLowerInvariant();
            if (!type.Extensions.Contains(extension))
                result.Add(new ExtensionMismatch(file.FullName, extension, type.Name, type.Extensions[0]));
        }
        return result;
    }
}

/// <summary>A file type recognised from its first bytes.</summary>
public sealed record DetectedType(string Name, IReadOnlyList<string> Extensions);

/// <summary>Detects common file types from their content ("magic numbers").</summary>
public static class FileSignature
{
    private static readonly DetectedType Pdf = new("PDF document", ["pdf", "ai"]);
    private static readonly DetectedType Png = new("PNG image", ["png", "apng"]);
    private static readonly DetectedType Jpeg = new("JPEG image", ["jpg", "jpeg", "jpe", "jfif"]);
    private static readonly DetectedType Gif = new("GIF image", ["gif"]);
    private static readonly DetectedType Bmp = new("BMP image", ["bmp", "dib"]);
    private static readonly DetectedType Tiff = new("TIFF image", ["tif", "tiff", "dng", "nef", "cr2", "arw", "orf", "rw2", "pef", "srw", "erf", "3fr"]);
    private static readonly DetectedType Webp = new("WebP image", ["webp"]);
    private static readonly DetectedType Wav = new("WAV audio", ["wav"]);
    private static readonly DetectedType Avi = new("AVI video", ["avi"]);
    private static readonly DetectedType Zip = new("ZIP-based file", ["zip", "docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "jar", "apk", "hwpx", "3mf", "xpi", "vsix", "kmz", "cbz", "ipa", "aar", "whl", "nupkg", "msix", "appx", "sketch", "xd", "pages", "numbers", "key", "usdz"]);
    private static readonly DetectedType SevenZip = new("7-Zip archive", ["7z"]);
    private static readonly DetectedType Rar = new("RAR archive", ["rar", "cbr"]);
    private static readonly DetectedType Gzip = new("GZip archive", ["gz", "tgz"]);
    private static readonly DetectedType Ole = new("Office 97-2003 / OLE file", ["doc", "xls", "ppt", "msg", "hwp", "msi", "vsd", "pub", "dwg"]);
    private static readonly DetectedType Exe = new("Windows program", ["exe", "dll", "sys", "scr", "com", "ocx", "cpl", "efi", "mui"]);
    private static readonly DetectedType Mp3 = new("MP3 audio", ["mp3"]);
    private static readonly DetectedType Flac = new("FLAC audio", ["flac"]);
    private static readonly DetectedType Ogg = new("Ogg media", ["ogg", "oga", "ogv", "opus"]);
    private static readonly DetectedType Mp4 = new("MP4 / QuickTime media", ["mp4", "m4a", "m4v", "mov", "3gp", "3g2", "heic", "heif", "avif", "f4v", "m4b"]);
    private static readonly DetectedType Mkv = new("Matroska / WebM media", ["mkv", "webm", "mka", "mk3d"]);

    /// <summary>The type of a file from its content, or null for unknown or plain-text types.</summary>
    public static DetectedType? Detect(string path)
    {
        Span<byte> head = stackalloc byte[16];
        int read;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Detect(head[..read].ToArray());
    }

    public static DetectedType? Detect(byte[] h)
    {
        bool At(int offset, params byte[] bytes) => h.Length >= offset + bytes.Length && h.AsSpan(offset, bytes.Length).SequenceEqual(bytes);
        bool Ascii(int offset, string text) => At(offset, System.Text.Encoding.ASCII.GetBytes(text));

        if (Ascii(0, "%PDF-")) return Pdf;
        if (At(0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return Png;
        if (At(0, 0xFF, 0xD8, 0xFF)) return Jpeg;
        if (Ascii(0, "GIF87a") || Ascii(0, "GIF89a")) return Gif;
        if (Ascii(0, "BM") && h.Length >= 14 && At(6, 0, 0, 0, 0)) return Bmp;
        if (At(0, 0x49, 0x49, 0x2A, 0x00) || At(0, 0x4D, 0x4D, 0x00, 0x2A)) return Tiff;
        if (Ascii(0, "RIFF") && Ascii(8, "WEBP")) return Webp;
        if (Ascii(0, "RIFF") && Ascii(8, "WAVE")) return Wav;
        if (Ascii(0, "RIFF") && Ascii(8, "AVI ")) return Avi;
        if (At(0, 0x50, 0x4B, 0x03, 0x04) || At(0, 0x50, 0x4B, 0x05, 0x06)) return Zip;
        if (At(0, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return SevenZip;
        if (Ascii(0, "Rar!")) return Rar;
        if (At(0, 0x1F, 0x8B)) return Gzip;
        if (At(0, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1)) return Ole;
        if (Ascii(0, "MZ")) return Exe;
        if (Ascii(0, "ID3")) return Mp3;
        if (Ascii(0, "fLaC")) return Flac;
        if (Ascii(0, "OggS")) return Ogg;
        if (Ascii(4, "ftyp")) return Mp4;
        if (At(0, 0x1A, 0x45, 0xDF, 0xA3)) return Mkv;
        return null;
    }
}
