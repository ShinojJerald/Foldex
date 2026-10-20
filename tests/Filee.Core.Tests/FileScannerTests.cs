using Filee.Core.Files;

namespace Filee.Core.Tests;

public sealed class FileScannerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("filee-scan-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.GetFullPath(Path.Combine(_dir, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Finds_exact_duplicates_only()
    {
        var a = Write("a.bin", [1, 2, 3, 4]);
        var b = Write("sub/b.bin", [1, 2, 3, 4]);
        Write("c.bin", [1, 2, 3, 5]); // same size, other content
        Write("d.bin", [9]);

        var groups = FileScanner.FindDuplicates(_dir);

        var group = Assert.Single(groups);
        Assert.Equal([a, b], group.Paths.Order());
        Assert.Equal(4, group.Wasted);
        Assert.True(File.Exists(a) && File.Exists(b)); // a scan never deletes
        Assert.Empty(FileScanner.FindDuplicates(_dir, recursive: false));
    }

    [Fact]
    public void Compares_and_lists_the_largest_files()
    {
        var a = Write("a.bin", new byte[300]);
        var b = Write("b.bin", new byte[300]);
        var c = Write("c.bin", new byte[10]);

        Assert.True(FileScanner.SameContent(a, b));
        Assert.False(FileScanner.SameContent(a, c));
        Assert.Equal(c, FileScanner.Largest(_dir, 3).Last().FullName);
        Assert.Equal(2, FileScanner.Largest(_dir, 2).Count);
    }

    [Fact]
    public void Detects_content_that_does_not_match_the_extension()
    {
        Write("photo.png", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10]); // a JPEG named .png
        Write("real.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10]);
        Write("report.docx", [0x50, 0x4B, 0x03, 0x04, 0x14, 0]); // ZIP-based: fine
        Write("invoice.pdf.exe", "MZ\x90\0"u8.ToArray());
        Write("notes.pdf", "MZ\x90\0"u8.ToArray()); // a program pretending to be a PDF
        Write("plain.txt", "hello"u8.ToArray());

        var mismatches = FileScanner.FindMismatches(_dir).OrderBy(m => m.Path).ToList();

        Assert.Equal(2, mismatches.Count);
        Assert.Equal("notes.pdf", Path.GetFileName(mismatches[0].Path));
        Assert.Equal("exe", mismatches[0].SuggestedExtension);
        Assert.Equal("photo.png", Path.GetFileName(mismatches[1].Path));
        Assert.Equal("jpg", mismatches[1].SuggestedExtension);
    }

    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "pdf")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "png")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "gif")]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 }, "mp4")]
    public void Recognises_common_signatures(byte[] head, string extension) =>
        Assert.Contains(extension, FileSignature.Detect(head)!.Extensions);

    [Fact]
    public void Unknown_content_is_not_guessed() => Assert.Null(FileSignature.Detect("just text"u8.ToArray()));
}

public sealed class BatchRenameTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("filee-rename-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, name);
        return path;
    }

    [Fact]
    public void Previews_numbered_names_and_applies_them()
    {
        var files = new[] { Touch("IMG_001.jpg"), Touch("IMG_002.jpg"), Touch("notes.txt") };

        var plan = BatchRename.Plan(files, "Trip {n} {name}", find: "IMG_", replace: "");

        Assert.Equal(["Trip 1 001.jpg", "Trip 2 002.jpg", "Trip 3 notes.txt"], plan.Select(p => p.NewName));
        Assert.All(plan, p => Assert.Null(p.Problem));
        Assert.Equal(3, BatchRename.Apply(plan));
        Assert.Equal("IMG_001.jpg", File.ReadAllText(Path.Combine(_dir, "Trip 1 001.jpg")));
        Assert.False(File.Exists(files[0]));
    }

    [Fact]
    public void Never_renames_over_another_file()
    {
        var a = Touch("a.txt");
        Touch("b.txt");
        var other = Touch("keep.txt");

        var plan = BatchRename.Plan([a], "keep");

        Assert.Equal("exists", plan.Single().Problem);
        Assert.Equal(0, BatchRename.Apply(plan));
        Assert.Equal("keep.txt", File.ReadAllText(other));
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Swaps_names_within_the_selection()
    {
        var a = Touch("1.txt");
        var b = Touch("2.txt");

        // Reversed order: 2.txt becomes "1", 1.txt becomes "2".
        var plan = BatchRename.Plan([b, a], "{n}");

        Assert.Equal(2, BatchRename.Apply(plan));
        Assert.Equal("2.txt", File.ReadAllText(Path.Combine(_dir, "1.txt")));
        Assert.Equal("1.txt", File.ReadAllText(Path.Combine(_dir, "2.txt")));
    }

    [Fact]
    public void Flags_names_that_collide_or_are_invalid()
    {
        var files = new[] { Touch("a.txt"), Touch("b.txt") };
        Assert.Equal("duplicate", BatchRename.Plan(files, "same")[1].Problem);
        Assert.Equal("invalid", BatchRename.Plan(files, "bad:name")[0].Problem);
    }
}
