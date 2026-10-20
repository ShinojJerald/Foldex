// Page-level PDF editing for the PDF workspace (PDFsharp, MIT). An edited document is a list of page references (source
// file, page, extra rotation) plus marks (real PDF annotations) and stamps (page numbers, header / footer, watermark,
// pictures drawn into the page content). Save writes a new file from them (the sources are only ever read); a document
// that keeps all its pages in order is changed as a whole, so its form, bookmarks and links stay.
//
// Coordinates of marks are points on the page as it is shown (its rotation applied), measured from the top-left corner,
// the same space the workspace draws its preview in.

using System.Globalization;
using System.Text;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Pdf;

/// <summary>One page of an edited document: page <paramref name="PageIndex"/> (0-based) of a source PDF, turned by
/// <paramref name="Rotation"/> more degrees clockwise (a multiple of 90).</summary>
public sealed record PdfPageRef(string SourcePath, int PageIndex, int Rotation = 0);

/// <summary>ReplaceText changes a line of the page's own text (<see cref="PdfMark.Line"/>) to <see cref="PdfMark.Text"/>.</summary>
public enum PdfMarkKind { Note, Highlight, Underline, StrikeOut, Rectangle, Ellipse, Line, FreeText, Image, ReplaceText }

/// <summary>
/// An annotation (or, for <see cref="PdfMarkKind.Image"/>, a picture drawn into the page) on output page
/// <paramref name="Page"/> (0-based). <see cref="PdfMarkKind.Line"/> runs from (X, Y) to (X + Width, Y + Height), so its
/// size may be negative. <paramref name="Text"/> is the note / comment text, or the picture's path for Image.
/// </summary>
public sealed record PdfMark(
    int Page, PdfMarkKind Kind, double X, double Y, double Width, double Height,
    string Color = "#FFD400", string? Text = null, double LineWidth = 2, double FontSize = 12, PdfTextLine? Line = null);

public enum PdfStampPosition { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

/// <summary>Text drawn on every page. <see cref="PageNumberFormat"/> may use {page} and {total}.</summary>
public sealed record PdfStamps
{
    public string? PageNumberFormat { get; init; }
    public PdfStampPosition PageNumberPosition { get; init; } = PdfStampPosition.BottomCenter;
    public int FirstNumber { get; init; } = 1;
    public string? Header { get; init; }
    public string? Footer { get; init; }
    public string? Watermark { get; init; }
    public double WatermarkOpacity { get; init; } = 0.15;
    public double FontSize { get; init; } = 10;

    public bool IsEmpty => string.IsNullOrWhiteSpace(PageNumberFormat) && string.IsNullOrWhiteSpace(Header)
                           && string.IsNullOrWhiteSpace(Footer) && string.IsNullOrWhiteSpace(Watermark);
}

public sealed record PdfSaveOptions
{
    /// <summary>Password to open the result. Setting one encrypts it with AES-256.</summary>
    public string? UserPassword { get; init; }
    /// <summary>Password for full rights; the user password when not set.</summary>
    public string? OwnerPassword { get; init; }
    /// <summary>Leaves title, author, subject, keywords and creator out of the result.</summary>
    public bool RemoveMetadata { get; init; }
    /// <summary>Passwords of encrypted sources, by path.</summary>
    public IReadOnlyDictionary<string, string>? SourcePasswords { get; init; }
    /// <summary>Form field values by full field name (check boxes: "true" / "false").</summary>
    public IReadOnlyDictionary<string, string>? FormValues { get; init; }
    /// <summary>Sign the result with a certificate.</summary>
    public PdfSigning? Signing { get; init; }
}

/// <summary>A PDF the editor can't read or write, with a message for people.</summary>
public sealed class PdfEditException(string message, Exception? inner = null) : Exception(message, inner)
{
    public bool NeedsPassword { get; init; }
}

public static class PdfEditor
{
    private const double Margin = 28;

    static PdfEditor()
    {
        // PDFsharp's Core build finds no fonts unless told to use the system's; elsewhere only marks without text work.
        if (OperatingSystem.IsWindows() && GlobalFontSettings.FontResolver is null)
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    /// <summary>Opens a PDF for reading pages; wrong or missing passwords and broken files become <see cref="PdfEditException"/>.</summary>
    public static PdfDocument OpenSource(string path, string? password = null)
    {
        try
        {
            return password is null ? PdfReader.Open(path, PdfDocumentOpenMode.Import)
                : PdfReader.Open(path, password, PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new PdfEditException(password is null
                ? $"“{Path.GetFileName(path)}” is password protected. Enter its password to open it."
                : $"The password for “{Path.GetFileName(path)}” is not correct.", ex) { NeedsPassword = true };
        }
        catch (Exception ex) when (ex is not PdfEditException and not OperationCanceledException)
        {
            throw new PdfEditException($"“{Path.GetFileName(path)}” could not be read as a PDF ({ex.Message}).", ex);
        }
    }

    /// <summary>Number of pages of a PDF.</summary>
    public static int PageCount(string path, string? password = null)
    {
        using var document = OpenSource(path, password);
        return document.PageCount;
    }

    /// <summary>All pages of a file, in order, unturned.</summary>
    public static IReadOnlyList<PdfPageRef> AllPages(string path, string? password = null) =>
        Enumerable.Range(0, PageCount(path, password)).Select(i => new PdfPageRef(path, i)).ToList();

    /// <summary>
    /// Writes <paramref name="pages"/> as a new PDF at <paramref name="outputPath"/> (through a temporary file next to it,
    /// so a failure never leaves half a file, and the output may be one of the sources).
    /// </summary>
    public static void Save(
        IReadOnlyList<PdfPageRef> pages, string outputPath, IReadOnlyList<PdfMark>? marks = null, PdfStamps? stamps = null,
        PdfSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pages.Count == 0)
            throw new PdfEditException("A PDF needs at least one page.");
        options ??= new PdfSaveOptions();
        marks ??= [];
        var encrypt = !string.IsNullOrEmpty(options.UserPassword) || !string.IsNullOrEmpty(options.OwnerPassword);
        if (encrypt && options.Signing is not null)
            throw new PdfEditException("A PDF can be signed or protected with a password here, not both at once.");
        var formValues = options.FormValues ?? new Dictionary<string, string>();
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        var sources = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // All pages of one unprotected file in their order: change that file itself, so its form, bookmarks and
            // links stay. Anything else is built as a new PDF from the pages.
            var whole = OpenWhole(pages, options);
            using (var output = whole ?? new PdfDocument())
            {
                if (whole is not null)
                {
                    for (var i = 0; i < pages.Count; i++)
                        output.Pages[i].Rotate = NormalizeRotation(output.Pages[i].Rotate + pages[i].Rotation);
                    if (options.RemoveMetadata)
                    {
                        ClearInfo(output);
                        output.Internals.Catalog.Elements.Remove("/Metadata");
                    }
                }
                else
                {
                    if (formValues.Count > 0)
                        throw new PdfEditException("Form fields can be filled while the pages keep their original order. Undo the page changes, fill and save the form, then rearrange.");
                    foreach (var reference in pages)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var source = Source(sources, reference.SourcePath, options);
                        if (reference.PageIndex < 0 || reference.PageIndex >= source.PageCount)
                            throw new PdfEditException($"“{Path.GetFileName(reference.SourcePath)}” has no page {reference.PageIndex + 1}.");
                        var page = output.AddPage(source.Pages[reference.PageIndex]);
                        page.Rotate = NormalizeRotation(page.Rotate + reference.Rotation);
                    }
                    CopyInfo(sources[pages[0].SourcePath], output, options.RemoveMetadata);
                }

                foreach (var page in output.Pages)
                    RepairCropBox(page);

                // Existing text first, while the page content is still the source's.
                foreach (var group in marks.Where(m => m.Kind == PdfMarkKind.ReplaceText).GroupBy(m => m.Page))
                {
                    var page = PageOf(output, group.Key);
                    foreach (var edit in group)
                    {
                        if (edit.Line is null)
                            throw new PdfEditException("A text change has no line to replace.");
                        if (PdfContentEditing.RemoveLine(page, edit.Line) == 0)
                            throw new PdfEditException($"The text “{edit.Line.Text}” on page {group.Key + 1} can't be changed here. Nothing was saved. Put a text box over it instead.");
                    }
                    foreach (var edit in group)
                        PdfContentEditing.DrawLine(page, edit.Line!, edit.Text ?? "");
                }
                foreach (var mark in marks.Where(m => m.Kind != PdfMarkKind.ReplaceText))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = PageOf(output, mark.Page);
                    if (mark.Kind == PdfMarkKind.Image)
                        DrawImage(page, mark);
                    else
                        AddAnnotation(output, page, mark, pages[mark.Page], options);
                }
                if (stamps is { IsEmpty: false })
                    DrawStamps(output, stamps, cancellationToken);
                PdfContentEditing.FillFields(output, formValues);

                if (encrypt)
                {
                    output.SecuritySettings.UserPassword = options.UserPassword ?? "";
                    output.SecuritySettings.OwnerPassword = string.IsNullOrEmpty(options.OwnerPassword) ? options.UserPassword ?? "" : options.OwnerPassword;
                    output.SecurityHandler.SetEncryptionToV5();
                }
                if (options.Signing is { } signing)
                    PdfContentEditing.SaveSigned(output, temp, signing);
                else
                    output.Save(temp);
            }
            foreach (var source in sources.Values)
                source.Dispose();
            sources.Clear();
            VerifyTextEdits(pages, marks, options, temp);
            File.Move(temp, outputPath, overwrite: true);
        }
        catch (Exception ex) when (ex is not PdfEditException and not OperationCanceledException)
        {
            throw new PdfEditException($"The PDF could not be written ({ex.Message}).", ex);
        }
        finally
        {
            foreach (var source in sources.Values)
                source.Dispose();
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static PdfPage PageOf(PdfDocument document, int index) =>
        index >= 0 && index < document.PageCount ? document.Pages[index]
            : throw new PdfEditException($"A change is on page {index + 1}, which the document doesn't have.");

    /// <summary>The source opened for changing when <paramref name="pages"/> are all of one unprotected file in order; else null.</summary>
    private static PdfDocument? OpenWhole(IReadOnlyList<PdfPageRef> pages, PdfSaveOptions options)
    {
        var path = pages[0].SourcePath;
        if (options.SourcePasswords?.ContainsKey(path) == true
            || pages.Where((p, i) => p.PageIndex != i || !string.Equals(p.SourcePath, path, StringComparison.OrdinalIgnoreCase)).Any())
            return null;
        try
        {
            var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            if (document.PageCount == pages.Count)
                return document;
            document.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Restricted or unusual files: build a new PDF from the pages instead.
        }
        return null;
    }

    /// <summary>Reads the written file back: every replaced line holds its new text and the rest of its page is unchanged.</summary>
    private static void VerifyTextEdits(IReadOnlyList<PdfPageRef> pages, IReadOnlyList<PdfMark> marks, PdfSaveOptions options, string written)
    {
        foreach (var group in marks.Where(m => m.Kind == PdfMarkKind.ReplaceText && m.Line is not null).GroupBy(m => m.Page))
        {
            var reference = pages[group.Key];
            var problem = PdfContentEditing.Verify(reference.SourcePath, reference.PageIndex, options.SourcePasswords?.GetValueOrDefault(reference.SourcePath),
                written, group.Key, group.Select(m => (m.Line!, m.Text ?? "")).ToList(), options.UserPassword);
            if (problem is not null)
                throw new PdfEditException($"The text on page {group.Key + 1} can't be changed safely ({problem}). Nothing was saved. Put a text box over it instead.");
        }
    }

    private static void ClearInfo(PdfDocument document)
    {
        document.Info.Title = "";
        document.Info.Author = "";
        document.Info.Subject = "";
        document.Info.Keywords = "";
        document.Info.Creator = "";
    }

    /// <summary>Merges whole files in the given order.</summary>
    public static void Merge(IReadOnlyList<string> inputs, string outputPath, PdfSaveOptions? options = null, CancellationToken cancellationToken = default) =>
        Save(inputs.SelectMany(path => AllPages(path, options?.SourcePasswords?.GetValueOrDefault(path))).ToList(), outputPath, options: options,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Splits a PDF: every comma-separated part of <paramref name="ranges"/> ("1-3, 4-6, 7") becomes its own file
    /// "&lt;name&gt; (1-3).pdf" in <paramref name="outputFolder"/>. "every:N" splits into files of N pages.
    /// Returns the files written.
    /// </summary>
    public static IReadOnlyList<string> Split(string inputPath, string ranges, string outputFolder, string? password = null, CancellationToken cancellationToken = default)
    {
        var count = PageCount(inputPath, password);
        var parts = SplitParts(ranges, count);
        var options = password is null ? null : new PdfSaveOptions { SourcePasswords = new Dictionary<string, string> { [inputPath] = password } };
        var written = new List<string>();
        foreach (var (label, pageIndexes) in parts)
        {
            var output = UniquePath(Path.Combine(outputFolder, $"{Path.GetFileNameWithoutExtension(inputPath)} ({label}).pdf"));
            Save(pageIndexes.Select(i => new PdfPageRef(inputPath, i)).ToList(), output, options: options, cancellationToken: cancellationToken);
            written.Add(output);
        }
        return written;
    }

    /// <summary>The parts of a split, as (label, 0-based page indexes).</summary>
    internal static IReadOnlyList<(string Label, IReadOnlyList<int> Pages)> SplitParts(string ranges, int pageCount)
    {
        var spec = ranges.Trim();
        if (spec.StartsWith("every:", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(spec[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 1)
                throw new PdfEditException("Enter how many pages each part should have.");
            return Enumerable.Range(0, (pageCount + size - 1) / size).Select(part =>
            {
                var first = part * size;
                var last = Math.Min(pageCount, first + size) - 1;
                return (first == last ? $"{first + 1}" : $"{first + 1}-{last + 1}", (IReadOnlyList<int>)Enumerable.Range(first, last - first + 1).ToList());
            }).ToList();
        }
        var result = new List<(string, IReadOnlyList<int>)>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // PageRange.Parse reads invalid parts as "every page", so check the part first.
            var numbers = part.Split('-', StringSplitOptions.TrimEntries);
            var valid = numbers.Length <= 2 && numbers.Any(n => n.Length > 0)
                        && numbers.All(n => n.Length == 0 || int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p >= 1 && p <= pageCount);
            if (!valid)
                throw new PdfEditException($"“{part}” is not a page range of this document (it has {pageCount} pages).");
            var pages = Filee.Core.Conversion.PageRange.Parse(part, pageCount);
            result.Add((part.Replace(" ", "", StringComparison.Ordinal), pages));
        }
        if (result.Count == 0)
            throw new PdfEditException("Enter the page ranges to split into, for example 1-3, 4-6.");
        return result;
    }

    /// <summary>A path that doesn't exist yet: "name.pdf", "name (2).pdf", …</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>Document information (title, author, …) of a PDF, for the metadata view.</summary>
    public static IReadOnlyDictionary<string, string> ReadInfo(string path, string? password = null)
    {
        using var document = OpenSource(path, password);
        var info = document.Info;
        var result = new Dictionary<string, string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                result[key] = value;
        }
        Add("Title", info.Title);
        Add("Author", info.Author);
        Add("Subject", info.Subject);
        Add("Keywords", info.Keywords);
        Add("Creator", info.Creator);
        Add("Producer", info.Producer);
        if (info.CreationDate != default)
            Add("Created", info.CreationDate.ToString("u", CultureInfo.InvariantCulture));
        return result;
    }

    public static int NormalizeRotation(int degrees) => ((degrees % 360) + 360) % 360 / 90 * 90;

    private static PdfDocument Source(Dictionary<string, PdfDocument> sources, string path, PdfSaveOptions options)
    {
        if (!sources.TryGetValue(path, out var document))
            sources[path] = document = OpenSource(path, options.SourcePasswords?.GetValueOrDefault(path));
        return document;
    }

    private static void CopyInfo(PdfDocument source, PdfDocument output, bool remove)
    {
        if (remove)
        {
            ClearInfo(output);
            return;
        }
        output.Info.Title = source.Info.Title;
        output.Info.Author = source.Info.Author;
        output.Info.Subject = source.Info.Subject;
        output.Info.Keywords = source.Info.Keywords;
        output.Info.Creator = source.Info.Creator;
    }

    // ───────── Page geometry: shown page (rotated, top-left origin) ↔ PDF user space ─────────

    /// <summary>The page has a usable /CropBox (not missing, not empty).</summary>
    internal static bool HasCropBox(PdfPage page) => page.Elements.ContainsKey("/CropBox") && !page.CropBox.IsZero;

    /// <summary>Drops an empty /CropBox [0 0 0 0] (written by earlier Foldex builds), so the page shows its media box.</summary>
    private static void RepairCropBox(PdfPage page)
    {
        if (page.Elements.ContainsKey("/CropBox") && page.CropBox.IsZero)
            page.Elements.Remove("/CropBox");
    }

    internal readonly record struct PageBox(double X0, double Y0, double W, double H, int Rotate)
    {
        public double ShownWidth => Rotate is 90 or 270 ? H : W;
        public double ShownHeight => Rotate is 90 or 270 ? W : H;

        public static PageBox Of(PdfPage page)
        {
            // Only read /CropBox when the page has one: PDFsharp's CropBox getter adds an empty [0 0 0 0] box to the page,
            // which ended up in saved files (an empty crop box) and confused later edits.
            var box = HasCropBox(page) ? page.CropBox : page.MediaBox;
            return new PageBox(box.X1, box.Y1, box.Width, box.Height, NormalizeRotation(page.Rotate));
        }

        /// <summary>A shown point in PDF user space.</summary>
        public (double X, double Y) ToUser(double dx, double dy) => Rotate switch
        {
            90 => (X0 + dy, Y0 + dx),
            180 => (X0 + W - dx, Y0 + dy),
            270 => (X0 + W - dy, Y0 + H - dx),
            _ => (X0 + dx, Y0 + H - dy),
        };

        /// <summary>Maps shown coordinates to XGraphics' coordinates (unturned page, y down from the top).</summary>
        public XMatrix ShownToGraphics() => Rotate switch
        {
            90 => new XMatrix(0, -1, 1, 0, 0, H),
            180 => new XMatrix(-1, 0, 0, -1, W, H),
            270 => new XMatrix(0, 1, -1, 0, W, 0),
            _ => XMatrix.Identity,
        };
    }

    // ───────── Annotations ─────────

    private static void AddAnnotation(PdfDocument document, PdfPage page, PdfMark mark, PdfPageRef reference, PdfSaveOptions options)
    {
        var box = PageBox.Of(page);
        var (r, g, b) = ParseColor(mark.Color);
        var lineWidth = Math.Clamp(mark.LineWidth, 0.25, 50);
        var annotation = new PdfDictionary(document);
        annotation.Elements["/Type"] = new PdfName("/Annot");
        annotation.Elements["/F"] = new PdfInteger(4); // print
        annotation.Elements["/C"] = Numbers(document, r, g, b);
        annotation.Elements["/M"] = new PdfString(PdfDate(DateTime.Now));
        annotation.Elements["/NM"] = new PdfString(Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrEmpty(mark.Text))
            annotation.Elements["/Contents"] = new PdfString(mark.Text, PdfStringEncoding.Unicode);

        var subtype = mark.Kind switch
        {
            PdfMarkKind.Note => "/Text",
            PdfMarkKind.Highlight => "/Highlight",
            PdfMarkKind.Underline => "/Underline",
            PdfMarkKind.StrikeOut => "/StrikeOut",
            PdfMarkKind.Rectangle => "/Square",
            PdfMarkKind.Ellipse => "/Circle",
            PdfMarkKind.Line => "/Line",
            _ => "/FreeText",
        };
        annotation.Elements["/Subtype"] = new PdfName(subtype);

        if (mark.Kind is PdfMarkKind.Highlight or PdfMarkKind.Underline or PdfMarkKind.StrikeOut
            && TextLines(reference, mark, box, options) is { Count: > 0 } lines)
        {
            AddTextMarkup(document, annotation, mark.Kind, lines, (r, g, b));
        }
        else
        {
            // Shown rectangle (normalised, padded for the stroke) and an appearance drawn upright in shown space; the
            // form's matrix turns it with the page so it reads the same as on screen.
            var pad = mark.Kind is PdfMarkKind.Rectangle or PdfMarkKind.Ellipse or PdfMarkKind.Line ? lineWidth : 0;
            var width = mark.Kind == PdfMarkKind.Note ? 20 : Math.Abs(mark.Width);
            var height = mark.Kind == PdfMarkKind.Note ? 20 : Math.Abs(mark.Height);
            if (mark.Kind != PdfMarkKind.Line)
            {
                width = Math.Max(width, 2);
                height = Math.Max(height, 2);
            }
            var left = (mark.Kind == PdfMarkKind.Note ? mark.X : Math.Min(mark.X, mark.X + mark.Width)) - pad;
            var top = (mark.Kind == PdfMarkKind.Note ? mark.Y : Math.Min(mark.Y, mark.Y + mark.Height)) - pad;
            var formWidth = width + 2 * pad;
            var formHeight = height + 2 * pad;
            var rect = UserRect(box, left, top, formWidth, formHeight);
            annotation.Elements["/Rect"] = Numbers(document, rect.X1, rect.Y1, rect.X2, rect.Y2);

            var ops = new StringBuilder();
            PdfDictionary? resources = null;
            string Color(string op) => Invariant($"{r} {g} {b} {op}");
            switch (mark.Kind)
            {
                case PdfMarkKind.Highlight:
                    resources = TransparencyResources(document);
                    ops.Append(Invariant($"/GS0 gs {Color("rg")} 0 0 {formWidth} {formHeight} re f"));
                    annotation.Elements["/QuadPoints"] = Numbers(document, rect.X1, rect.Y2, rect.X2, rect.Y2, rect.X1, rect.Y1, rect.X2, rect.Y1);
                    break;
                case PdfMarkKind.Underline:
                    ops.Append(Invariant($"{Color("RG")} {lineWidth} w 0 {lineWidth / 2} m {formWidth} {lineWidth / 2} l S"));
                    annotation.Elements["/QuadPoints"] = Numbers(document, rect.X1, rect.Y2, rect.X2, rect.Y2, rect.X1, rect.Y1, rect.X2, rect.Y1);
                    break;
                case PdfMarkKind.StrikeOut:
                    ops.Append(Invariant($"{Color("RG")} {lineWidth} w 0 {formHeight / 2} m {formWidth} {formHeight / 2} l S"));
                    annotation.Elements["/QuadPoints"] = Numbers(document, rect.X1, rect.Y2, rect.X2, rect.Y2, rect.X1, rect.Y1, rect.X2, rect.Y1);
                    break;
                case PdfMarkKind.Rectangle:
                    ops.Append(Invariant($"{Color("RG")} {lineWidth} w {pad} {pad} {width} {height} re S"));
                    annotation.Elements["/BS"] = Border(document, lineWidth);
                    break;
                case PdfMarkKind.Ellipse:
                    ops.Append(Color("RG")).Append(Invariant($" {lineWidth} w ")).Append(EllipsePath(pad, pad, width, height)).Append(" S");
                    annotation.Elements["/BS"] = Border(document, lineWidth);
                    break;
                case PdfMarkKind.Line:
                {
                    // Start and end inside the form (y up).
                    var x1 = mark.X - left; var y1 = formHeight - (mark.Y - top);
                    var x2 = mark.X + mark.Width - left; var y2 = formHeight - (mark.Y + mark.Height - top);
                    ops.Append(Invariant($"{Color("RG")} {lineWidth} w 1 J {x1} {y1} m {x2} {y2} l S"));
                    var start = box.ToUser(mark.X, mark.Y);
                    var end = box.ToUser(mark.X + mark.Width, mark.Y + mark.Height);
                    annotation.Elements["/L"] = Numbers(document, start.X, start.Y, end.X, end.Y);
                    annotation.Elements["/BS"] = Border(document, lineWidth);
                    break;
                }
                case PdfMarkKind.Note:
                    ops.Append(Invariant($"{Color("rg")} 0.5 0.5 19 19 re f 0 0 0 RG 0.6 w 0.5 0.5 19 19 re S 4 14 m 16 14 l 4 10 m 16 10 l 4 6 m 12 6 l S"));
                    annotation.Elements["/Name"] = new PdfName("/Comment");
                    annotation.Elements["/Open"] = new PdfBoolean(false);
                    break;
                case PdfMarkKind.FreeText:
                {
                    resources = FontResources(document);
                    var size = Math.Clamp(mark.FontSize, 4, 144);
                    annotation.Elements["/DA"] = new PdfString(Invariant($"/Helv {size} Tf {r} {g} {b} rg"));
                    annotation.Elements["/BS"] = Border(document, 0);
                    ops.Append(Invariant($"BT /Helv {size} Tf {Color("rg")} {size * 1.2} TL 2 {formHeight - size} Td"));
                    foreach (var line in (mark.Text ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n'))
                        ops.Append(" (").Append(PdfText(line)).Append(") Tj T*");
                    ops.Append(" ET");
                    break;
                }
            }
            var matrix = box.Rotate == 0 ? null : RotationMatrix(document, box.Rotate);
            annotation.Elements["/AP"] = Appearance(document, formWidth, formHeight, ops.ToString(), resources, matrix, 0, 0);
        }

        document.Internals.AddObject(annotation);
        annotation.Elements["/P"] = page.Reference;
        var annots = page.Elements.GetArray("/Annots");
        if (annots is null)
        {
            annots = new PdfArray(document);
            page.Elements["/Annots"] = annots;
        }
        annots.Elements.Add(annotation.Reference!);
    }

    /// <summary>
    /// Lines of text under a text-markup mark, in user space (left, bottom, right, top), so the highlight follows the
    /// text. Only for sources without their own rotation or offset box, where PdfPig's coordinates are plain user space;
    /// elsewhere the marked rectangle is used.
    /// </summary>
    private static List<(double L, double B, double R, double T)> TextLines(PdfPageRef reference, PdfMark mark, PageBox box, PdfSaveOptions options)
    {
        var lines = new List<(double L, double B, double R, double T)>();
        try
        {
            var password = options.SourcePasswords?.GetValueOrDefault(reference.SourcePath);
            using var pig = UglyToad.PdfPig.PdfDocument.Open(reference.SourcePath,
                new UglyToad.PdfPig.ParsingOptions { Password = password ?? "", UseLenientParsing = true });
            var page = pig.GetPage(reference.PageIndex + 1);
            if (page.Rotation.Value != 0 || box.X0 != 0 || box.Y0 != 0)
                return lines;
            var a = box.ToUser(mark.X, mark.Y);
            var c = box.ToUser(mark.X + mark.Width, mark.Y + mark.Height);
            var (left, right) = (Math.Min(a.X, c.X), Math.Max(a.X, c.X));
            var (bottom, top) = (Math.Min(a.Y, c.Y), Math.Max(a.Y, c.Y));
            var words = page.GetWords()
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .Select(w => w.BoundingBox)
                .Where(w => w.Right > left && w.Left < right && w.Top > bottom && w.Bottom < top)
                .OrderByDescending(w => w.Bottom).ThenBy(w => w.Left);
            foreach (var word in words)
            {
                var index = lines.FindIndex(l => Math.Abs(l.B - word.Bottom) < Math.Max(1, (word.Top - word.Bottom) / 2));
                if (index < 0)
                    lines.Add((word.Left, word.Bottom, word.Right, word.Top));
                else
                {
                    var l = lines[index];
                    lines[index] = (Math.Min(l.L, word.Left), Math.Min(l.B, word.Bottom), Math.Max(l.R, word.Right), Math.Max(l.T, word.Top));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lines.Clear(); // unreadable text layer: mark the rectangle instead
        }
        return lines;
    }

    private static void AddTextMarkup(PdfDocument document, PdfDictionary annotation, PdfMarkKind kind, List<(double L, double B, double R, double T)> lines, (double R, double G, double B) color)
    {
        var (x1, y1) = (lines.Min(l => l.L), lines.Min(l => l.B));
        var (x2, y2) = (lines.Max(l => l.R), lines.Max(l => l.T));
        annotation.Elements["/Rect"] = Numbers(document, x1, y1, x2, y2);
        var quads = lines.SelectMany(l => new[] { l.L, l.T, l.R, l.T, l.L, l.B, l.R, l.B }).ToArray();
        annotation.Elements["/QuadPoints"] = Numbers(document, quads);

        var ops = new StringBuilder();
        PdfDictionary? resources = null;
        var c = Invariant($"{color.R} {color.G} {color.B}");
        if (kind == PdfMarkKind.Highlight)
        {
            resources = TransparencyResources(document);
            ops.Append("/GS0 gs ").Append(c).Append(" rg");
            foreach (var l in lines)
                ops.Append(Invariant($" {l.L} {l.B} {l.R - l.L} {l.T - l.B} re"));
            ops.Append(" f");
        }
        else
        {
            ops.Append(c).Append(" RG");
            foreach (var l in lines)
            {
                var height = l.T - l.B;
                var y = kind == PdfMarkKind.Underline ? l.B + height * 0.08 : l.B + height * 0.4;
                ops.Append(Invariant($" {Math.Max(0.5, height / 14)} w {l.L} {y} m {l.R} {y} l S"));
            }
        }
        // The form is in user space: its bounding box is the annotation rectangle itself.
        annotation.Elements["/AP"] = Appearance(document, x2 - x1, y2 - y1, ops.ToString(), resources, null, x1, y1);
    }

    private static PdfDictionary Appearance(PdfDocument document, double width, double height, string ops, PdfDictionary? resources, PdfArray? matrix, double originX, double originY)
    {
        var form = new PdfDictionary(document);
        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        form.Elements["/BBox"] = Numbers(document, originX, originY, originX + width, originY + height);
        if (matrix is not null)
            form.Elements["/Matrix"] = matrix;
        form.Elements["/Resources"] = resources ?? new PdfDictionary(document);
        form.CreateStream(Encoding.Latin1.GetBytes(ops));
        document.Internals.AddObject(form);
        var ap = new PdfDictionary(document);
        ap.Elements["/N"] = form.Reference!;
        return ap;
    }

    private static PdfArray RotationMatrix(PdfDocument document, int rotate)
    {
        // Counter-clockwise by the page rotation: the viewer turns the page clockwise, so the mark reads upright.
        var (cos, sin) = rotate switch { 90 => (0, 1), 180 => (-1, 0), _ => (0, -1) };
        return Numbers(document, cos, sin, -sin, cos, 0, 0);
    }

    private static PdfDictionary TransparencyResources(PdfDocument document)
    {
        var state = new PdfDictionary(document);
        state.Elements["/Type"] = new PdfName("/ExtGState");
        state.Elements["/CA"] = new PdfReal(0.4);
        state.Elements["/ca"] = new PdfReal(0.4);
        state.Elements["/BM"] = new PdfName("/Multiply");
        var states = new PdfDictionary(document);
        states.Elements["/GS0"] = state;
        var resources = new PdfDictionary(document);
        resources.Elements["/ExtGState"] = states;
        return resources;
    }

    private static PdfDictionary FontResources(PdfDocument document)
    {
        var font = new PdfDictionary(document);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/Helvetica");
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        var fonts = new PdfDictionary(document);
        fonts.Elements["/Helv"] = font;
        var resources = new PdfDictionary(document);
        resources.Elements["/Font"] = fonts;
        return resources;
    }

    private static PdfDictionary Border(PdfDocument document, double width)
    {
        var border = new PdfDictionary(document);
        border.Elements["/W"] = new PdfReal(width);
        border.Elements["/S"] = new PdfName("/S");
        return border;
    }

    private static PdfRectangle UserRect(PageBox box, double left, double top, double width, double height)
    {
        var a = box.ToUser(left, top);
        var c = box.ToUser(left + width, top + height);
        return new PdfRectangle(new XPoint(Math.Min(a.X, c.X), Math.Min(a.Y, c.Y)), new XPoint(Math.Max(a.X, c.X), Math.Max(a.Y, c.Y)));
    }

    private static string EllipsePath(double x, double y, double w, double h)
    {
        const double k = 0.5522847498;
        var (cx, cy, rx, ry) = (x + w / 2, y + h / 2, w / 2, h / 2);
        return Invariant($"{cx + rx} {cy} m {cx + rx} {cy + ry * k} {cx + rx * k} {cy + ry} {cx} {cy + ry} c ") +
               Invariant($"{cx - rx * k} {cy + ry} {cx - rx} {cy + ry * k} {cx - rx} {cy} c ") +
               Invariant($"{cx - rx} {cy - ry * k} {cx - rx * k} {cy - ry} {cx} {cy - ry} c ") +
               Invariant($"{cx + rx * k} {cy - ry} {cx + rx} {cy - ry * k} {cx + rx} {cy} c h");
    }

    /// <summary>Text for a WinAnsi literal string: escaped, and characters Helvetica lacks as "?".</summary>
    private static string PdfText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '(' or ')' or '\\')
                builder.Append('\\').Append(ch);
            else
                builder.Append(ch is >= ' ' and <= 'ÿ' ? ch : '?');
        }
        return builder.ToString();
    }

    private static PdfArray Numbers(PdfDocument document, params double[] values) =>
        new(document, values.Select(v => (PdfItem)new PdfReal(Math.Round(v, 4))).ToArray());

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string PdfDate(DateTime time) => "D:" + time.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    internal static (double R, double G, double B) ParseColor(string color)
    {
        var hex = color.TrimStart('#');
        if (hex.Length == 8)
            hex = hex[2..];
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return (1, 0.83, 0);
        return (Math.Round(((value >> 16) & 0xFF) / 255.0, 3), Math.Round(((value >> 8) & 0xFF) / 255.0, 3), Math.Round((value & 0xFF) / 255.0, 3));
    }

    // ───────── Page content: pictures and stamps ─────────

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfPage, object> Isolated = new();

    /// <summary>
    /// Wraps the page's own content in q … Q once, so what is drawn after it starts from the page's coordinates. Chromium's
    /// "Save as PDF" starts each page with a scaling transform it never restores; once a text edit had rewritten such a
    /// page's content, the replacement text was drawn under that transform (tiny, in the wrong place) and every edit failed
    /// its read-back check.
    /// </summary>
    internal static void Isolate(PdfPage page)
    {
        if (Isolated.TryGetValue(page, out _))
            return;
        Isolated.Add(page, page);
        if (page.Contents.Elements.Count == 0)
            return;
        page.Contents.PrependContent().CreateStream("q\n"u8.ToArray());
        page.Contents.AppendContent().CreateStream("\nQ\n"u8.ToArray());
    }

    /// <summary>XGraphics on a page in its own unturned coordinates (y down from the top); passes the page box top in user space.</summary>
    internal static void DrawUnrotated(PdfPage page, Action<XGraphics, double> draw)
    {
        var box = PageBox.Of(page);
        var rotate = page.Rotate;
        page.Rotate = 0;
        try
        {
            Isolate(page);
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            draw(gfx, box.Y0 + box.H);
        }
        finally
        {
            page.Rotate = rotate;
        }
    }

    /// <summary>XGraphics on a page in shown coordinates (the page's rotation is undone while drawing).</summary>
    internal static void DrawShown(PdfPage page, Action<XGraphics, PageBox> draw)
    {
        var box = PageBox.Of(page);
        var rotate = page.Rotate;
        page.Rotate = 0;
        try
        {
            Isolate(page);
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            gfx.MultiplyTransform(box.ShownToGraphics());
            draw(gfx, box);
        }
        finally
        {
            page.Rotate = rotate;
        }
    }

    private static void DrawImage(PdfPage page, PdfMark mark)
    {
        if (string.IsNullOrEmpty(mark.Text) || !File.Exists(mark.Text))
            throw new PdfEditException("The picture to place on the page was not found.");
        // Any picture ImageMagick reads, re-encoded as PNG: PDFsharp reads few formats, and EXIF data stays behind.
        using var stream = new MemoryStream();
        using (var picture = new MagickImage(mark.Text))
        {
            picture.AutoOrient();
            picture.Strip();
            picture.Write(stream, MagickFormat.Png32);
        }
        stream.Position = 0;
        using var image = XImage.FromStream(stream);
        DrawShown(page, (gfx, _) => gfx.DrawImage(image, Math.Min(mark.X, mark.X + mark.Width), Math.Min(mark.Y, mark.Y + mark.Height),
            Math.Max(1, Math.Abs(mark.Width)), Math.Max(1, Math.Abs(mark.Height))));
    }

    private static void DrawStamps(PdfDocument document, PdfStamps stamps, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(stamps.FontSize, 4, 72);
        var font = new XFont("Arial", size);
        var brush = new XSolidBrush(XColor.FromArgb(255, 60, 60, 60));
        var total = document.PageCount;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = stamps.FirstNumber + i;
            DrawShown(document.Pages[i], (gfx, box) =>
            {
                var w = box.ShownWidth;
                var h = box.ShownHeight;
                if (!string.IsNullOrWhiteSpace(stamps.Watermark))
                {
                    var markSize = Math.Min(w, h) / Math.Max(4, stamps.Watermark.Length) * 1.6;
                    var markFont = new XFont("Arial", markSize, XFontStyleEx.Bold);
                    var alpha = (int)Math.Round(Math.Clamp(stamps.WatermarkOpacity, 0.02, 1) * 255);
                    var state = gfx.Save();
                    gfx.RotateAtTransform(-Math.Atan2(h, w) * 180 / Math.PI, new XPoint(w / 2, h / 2));
                    gfx.DrawString(stamps.Watermark, markFont, new XSolidBrush(XColor.FromArgb(alpha, 128, 128, 128)),
                        new XRect(0, 0, w, h), XStringFormats.Center);
                    gfx.Restore(state);
                }
                if (!string.IsNullOrWhiteSpace(stamps.Header))
                    gfx.DrawString(Expand(stamps.Header, number, total), font, brush, new XRect(Margin, Margin / 2, w - 2 * Margin, size * 1.4), XStringFormats.TopCenter);
                if (!string.IsNullOrWhiteSpace(stamps.Footer))
                    gfx.DrawString(Expand(stamps.Footer, number, total), font, brush, new XRect(Margin, h - Margin / 2 - size * 1.4, w - 2 * Margin, size * 1.4), XStringFormats.BottomCenter);
                if (!string.IsNullOrWhiteSpace(stamps.PageNumberFormat))
                {
                    var top = stamps.PageNumberPosition is PdfStampPosition.TopLeft or PdfStampPosition.TopCenter or PdfStampPosition.TopRight;
                    var y = top ? Margin / 2 : h - Margin / 2 - size * 1.4;
                    var rect = new XRect(Margin, y, w - 2 * Margin, size * 1.4);
                    var format = stamps.PageNumberPosition switch
                    {
                        PdfStampPosition.TopLeft => XStringFormats.TopLeft,
                        PdfStampPosition.TopCenter => XStringFormats.TopCenter,
                        PdfStampPosition.TopRight => XStringFormats.TopRight,
                        PdfStampPosition.BottomLeft => XStringFormats.BottomLeft,
                        PdfStampPosition.BottomRight => XStringFormats.BottomRight,
                        _ => XStringFormats.BottomCenter,
                    };
                    gfx.DrawString(Expand(stamps.PageNumberFormat, number, total), font, brush, rect, format);
                }
            });
        }
    }

    private static string Expand(string text, int page, int total) =>
        text.Replace("{page}", page.ToString(CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{total}", total.ToString(CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase);
}
