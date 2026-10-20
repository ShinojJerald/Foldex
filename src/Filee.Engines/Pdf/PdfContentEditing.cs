// Changing what is inside a PDF, not only adding to it: filling form fields, replacing a line of existing text, and
// signing with a certificate. All three run as part of PdfEditor.Save.
//
// Replacing text: the page's content stream is parsed (PDFsharp ContentReader), every text-showing operator that starts
// on the chosen line is removed, and the new text is drawn in the same place, size and colour with the closest installed
// font. Nothing is painted over: the old text is really gone. Save then reads the result back and refuses to keep it if
// the old words are still there or any other word on the page changed (text inside form XObjects, rotated or
// unusually encoded text), so a page is never silently damaged.

using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.Signatures;

namespace Filee.Engines.Pdf;

public enum PdfFieldKind { Text, CheckBox, Choice, Radio, Signature, Other }

/// <summary>A form field: its full name (parent.child), what to show, its value and, for lists and radio buttons, the choices.</summary>
public sealed record PdfFormField(string Name, string Label, PdfFieldKind Kind, string Value, IReadOnlyList<string> Options, bool ReadOnly);

/// <summary>
/// A line of existing text, in PDF user space: its bounds, the start of its baseline, and how it is set (font name as
/// stored, size, bold / italic, colour).
/// </summary>
public sealed record PdfTextLine(string Text, double Left, double Bottom, double Right, double Top, double BaselineX, double BaselineY,
    string FontName, double FontSize, bool Bold, bool Italic, string Color);

/// <summary>Why a click found no editable text.</summary>
public enum PdfTextMiss { None, TurnedPage, NoTextOnPage, NothingHere }

/// <summary>The result of <see cref="PdfContentEditing.HitTest"/>; Origin is the user-space top-left of the shown page.</summary>
public sealed record PdfTextHit(PdfTextLine? Line, PdfTextMiss Miss, double OriginX, double OriginY);

/// <summary>Sign the saved PDF with a certificate (PKCS#7, SHA-256), invisibly.</summary>
public sealed record PdfSigning(X509Certificate2 Certificate, string? Reason = null, string? Location = null);

public static class PdfContentEditing
{
    // ───────── Forms ─────────

    /// <summary>The fillable fields of a PDF (empty when it has no form).</summary>
    public static IReadOnlyList<PdfFormField> ReadFields(string path, string? password = null)
    {
        using var document = PdfEditor.OpenSource(path, password);
        var result = new List<PdfFormField>();
        if (document.Internals.Catalog.Elements.ContainsKey("/AcroForm") && document.AcroForm is { } form)
            foreach (var (name, field) in Leaves(form.Fields, ""))
                result.Add(Describe(name, field));
        return result;
    }

    /// <summary>Every field that holds a value, with its full name.</summary>
    internal static IEnumerable<(string Name, PdfAcroField Field)> Leaves(PdfAcroField.PdfAcroFieldCollection fields, string prefix)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var name = string.IsNullOrEmpty(field.Name) ? prefix : prefix.Length == 0 ? field.Name : $"{prefix}.{field.Name}";
            var isValueField = field is PdfTextField or PdfCheckBoxField or PdfChoiceField or PdfRadioButtonField or PdfSignatureField;
            if (!isValueField && field.HasKids)
                foreach (var leaf in Leaves(field.Fields, name))
                    yield return leaf;
            else if (name.Length > 0)
                yield return (name, field);
        }
    }

    private static PdfFormField Describe(string name, PdfAcroField field)
    {
        var label = field.Elements.GetString(PdfAcroField.Keys.TU);
        if (string.IsNullOrWhiteSpace(label))
            label = name;
        return field switch
        {
            PdfTextField text => new PdfFormField(name, label, PdfFieldKind.Text, text.Text ?? "", [], field.ReadOnly),
            PdfCheckBoxField box => new PdfFormField(name, label, PdfFieldKind.CheckBox, box.Checked ? "true" : "false", [], field.ReadOnly),
            PdfRadioButtonField radio => new PdfFormField(name, label, PdfFieldKind.Radio, RadioOptions(radio).ElementAtOrDefault(radio.SelectedIndex) ?? "",
                RadioOptions(radio), field.ReadOnly),
            PdfChoiceField choice => new PdfFormField(name, label, PdfFieldKind.Choice, ValueText(field), ChoiceOptions(choice), field.ReadOnly),
            PdfSignatureField => new PdfFormField(name, label, PdfFieldKind.Signature, "", [], true),
            _ => new PdfFormField(name, label, PdfFieldKind.Other, ValueText(field), [], true),
        };
    }

    private static string ValueText(PdfAcroField field) => field.Elements[PdfAcroField.Keys.V] switch
    {
        PdfString s => s.Value,
        PdfName n => n.Value.TrimStart('/'),
        _ => "",
    };

    private static List<string> ChoiceOptions(PdfChoiceField choice)
    {
        var options = new List<string>();
        if (choice.Elements.GetArray("/Opt") is { } opt)
            foreach (var item in opt.Elements)
            {
                var value = item switch
                {
                    PdfString s => s.Value,
                    PdfArray pair when pair.Elements.Count > 1 && pair.Elements[1] is PdfString shown => shown.Value,
                    PdfArray pair when pair.Elements.Count > 0 && pair.Elements[0] is PdfString export => export.Value,
                    _ => null,
                };
                if (value is not null)
                    options.Add(value);
            }
        return options;
    }

    /// <summary>The "on" state of each button of a radio group, in order.</summary>
    private static List<string> RadioOptions(PdfRadioButtonField radio)
    {
        var options = new List<string>();
        if (radio.Elements.GetArray(PdfAcroField.Keys.Kids) is not { } kids)
            return options;
        foreach (var kid in kids.Elements)
        {
            var widget = (kid as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary ?? kid as PdfDictionary;
            var normal = widget?.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
            options.Add(normal?.Elements.Keys.FirstOrDefault(k => k != "/Off")?.TrimStart('/') ?? $"{options.Count + 1}");
        }
        return options;
    }

    /// <summary>Fills fields by full name. Unknown names and read-only fields are refused with a message.</summary>
    internal static void FillFields(PdfDocument document, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
            return;
        var form = document.Internals.Catalog.Elements.ContainsKey("/AcroForm") ? document.AcroForm : null;
        if (form is null)
            throw new PdfEditException("This PDF has no form fields to fill.");
        var leaves = Leaves(form.Fields, "").ToDictionary(l => l.Name, l => l.Field, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (!leaves.TryGetValue(name, out var field))
                throw new PdfEditException($"The form has no field “{name}”.");
            if (field.ReadOnly)
                throw new PdfEditException($"The field “{name}” is read-only.");
            switch (field)
            {
                case PdfTextField text:
                    text.Text = value;
                    break;
                case PdfCheckBoxField box:
                    box.Checked = value is "true" or "True" or "1" or "on" or "yes";
                    break;
                case PdfRadioButtonField radio:
                    var index = RadioOptions(radio).IndexOf(value);
                    if (index < 0)
                        throw new PdfEditException($"“{value}” is not a choice of “{name}”.");
                    radio.SelectedIndex = index;
                    break;
                case PdfComboBoxField or PdfListBoxField:
                    var choice = (PdfChoiceField)field;
                    var option = ChoiceOptions(choice).IndexOf(value);
                    if (option < 0 && field is PdfListBoxField)
                        throw new PdfEditException($"“{value}” is not a choice of “{name}”.");
                    field.Elements[PdfAcroField.Keys.V] = new PdfString(value, PdfStringEncoding.Unicode);
                    if (option >= 0 && field is PdfComboBoxField combo)
                        combo.SelectedIndex = option;
                    else if (option >= 0 && field is PdfListBoxField list)
                        list.SelectedIndex = option;
                    break;
                default:
                    throw new PdfEditException($"The field “{name}” can't be filled here.");
            }
        }
        // Viewers redraw the fields from their values.
        form.Elements["/NeedAppearances"] = new PdfBoolean(true);
    }

    // ───────── Finding a line of text ─────────

    /// <summary>The line of text at a point (user space) on page <paramref name="pageIndex"/>, or null.</summary>
    public static PdfTextLine? FindLine(string path, int pageIndex, double x, double y, string? password = null)
    {
        using var pig = OpenPig(path, password);
        var page = pig.GetPage(pageIndex + 1);
        return page.Rotation.Value != 0 ? null : LineAt(Lines(page), x, y);
    }

    /// <summary>
    /// The line of text under a click, in the page's shown coordinates (points from the top-left of the page as it is
    /// rendered, i.e. its crop box), with the reason when there is none. <see cref="PdfTextHit.OriginX"/> /
    /// <see cref="PdfTextHit.OriginY"/> are the user-space coordinates of that top-left corner.
    /// </summary>
    public static PdfTextHit HitTest(string path, int pageIndex, double shownX, double shownY, string? password = null)
    {
        using var pig = OpenPig(path, password);
        var page = pig.GetPage(pageIndex + 1);
        // An empty crop box (files saved by earlier Foldex builds) means the media box.
        var crop = page.CropBox.Bounds is { Width: > 0, Height: > 0 } cropBounds ? cropBounds : page.MediaBox.Bounds;
        if (page.Rotation.Value != 0)
            return new PdfTextHit(null, PdfTextMiss.TurnedPage, crop.Left, crop.Top);
        var lines = Lines(page);
        if (lines.Count == 0)
            return new PdfTextHit(null, PdfTextMiss.NoTextOnPage, crop.Left, crop.Top);
        var line = LineAt(lines, crop.Left + shownX, crop.Top - shownY);
        return new PdfTextHit(line, line is null ? PdfTextMiss.NothingHere : PdfTextMiss.None, crop.Left, crop.Top);
    }

    private static UglyToad.PdfPig.PdfDocument OpenPig(string path, string? password) =>
        UglyToad.PdfPig.PdfDocument.Open(path, new UglyToad.PdfPig.ParsingOptions { Password = password ?? "", UseLenientParsing = true });

    /// <summary>
    /// The line under the point, else the nearest one within <see cref="ClickReach"/> points. Lines of small text are only
    /// a few points tall with wider gaps between them, so a click on a line easily lands just above, below or past it
    /// (the bug report: 39% of clicks on a page of 8 pt text found nothing with a 2-point margin).
    /// </summary>
    private static PdfTextLine? LineAt(List<PdfTextLine> lines, double x, double y)
    {
        static (double Dx, double Dy) Gap(PdfTextLine l, double x, double y) =>
            (Math.Max(0, Math.Max(l.Left - x, x - l.Right)), Math.Max(0, Math.Max(l.Bottom - y, y - l.Top)));
        return lines.Select(l => (Line: l, Gap: Gap(l, x, y)))
            .Where(t => t.Gap.Dx <= ClickReach && t.Gap.Dy <= ClickReach)
            .OrderBy(t => t.Gap.Dy).ThenBy(t => t.Gap.Dx)
            .Select(t => t.Line).FirstOrDefault();
    }

    /// <summary>How far (points) from a line's glyphs a click still picks it.</summary>
    internal const double ClickReach = 8;

    /// <summary>The page's text grouped into lines (words sharing a baseline and a size).</summary>
    internal static List<PdfTextLine> Lines(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().Where(w => w.Letters.Count > 0 && !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var groups = new List<List<UglyToad.PdfPig.Content.Word>>();
        foreach (var word in words.OrderByDescending(w => w.Letters[0].StartBaseLine.Y).ThenBy(w => w.BoundingBox.Left))
        {
            var size = word.Letters[0].PointSize;
            var baseline = word.Letters[0].StartBaseLine.Y;
            var group = groups.FirstOrDefault(g => Math.Abs(g[0].Letters[0].StartBaseLine.Y - baseline) < Math.Max(1, size * 0.3)
                                                   && Math.Abs(g[0].Letters[0].PointSize - size) < Math.Max(0.5, size * 0.15));
            if (group is null)
                groups.Add([word]);
            else
                group.Add(word);
        }
        return groups.Select(g =>
        {
            g.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            var first = g[0].Letters[0];
            var font = first.FontName ?? "";
            var rgb = first.Color?.ToRGBValues();
            var color = rgb is { } c ? $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}" : "#000000";
            return new PdfTextLine(string.Join(" ", g.Select(w => w.Text)),
                g.Min(w => w.BoundingBox.Left), g.Min(w => w.BoundingBox.Bottom), g.Max(w => w.BoundingBox.Right), g.Max(w => w.BoundingBox.Top),
                first.StartBaseLine.X, first.StartBaseLine.Y, font, first.PointSize,
                font.Contains("Bold", StringComparison.OrdinalIgnoreCase) || font.Contains("Black", StringComparison.OrdinalIgnoreCase) || font.Contains("Heavy", StringComparison.OrdinalIgnoreCase),
                font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase),
                color);
        }).ToList();
    }

    // ───────── Replacing a line ─────────

    /// <summary>Removes the text operators that start on <paramref name="line"/> from the page content. Returns how many.</summary>
    internal static int RemoveLine(PdfPage page, PdfTextLine line)
    {
        var content = ContentReader.ReadContent(page);
        var tolerance = Math.Max(1.5, line.FontSize * 0.35);
        var removed = 0;
        var states = new Stack<Matrix>();
        var ctm = Matrix.Identity;
        var tm = Matrix.Identity;
        var tlm = Matrix.Identity;
        double leading = 0;
        var output = new CSequence();

        foreach (var item in content)
        {
            if (item is not COperator op)
            {
                output.Add(item);
                continue;
            }
            var n = op.Operands;
            double N(int i) => i < n.Count ? n[i] switch { CInteger ci => ci.Value, CReal cr => cr.Value, _ => 0 } : 0;
            var drop = false;
            switch (op.Name)
            {
                case "q": states.Push(ctm); break;
                case "Q": ctm = states.Count > 0 ? states.Pop() : Matrix.Identity; break;
                case "cm": ctm = new Matrix(N(0), N(1), N(2), N(3), N(4), N(5)) * ctm; break;
                case "BT": tm = tlm = Matrix.Identity; break;
                case "Tm": tm = tlm = new Matrix(N(0), N(1), N(2), N(3), N(4), N(5)); break;
                case "Td": tm = tlm = Matrix.Translate(N(0), N(1)) * tlm; break;
                case "TD": leading = -N(1); tm = tlm = Matrix.Translate(N(0), N(1)) * tlm; break;
                case "TL": leading = N(0); break;
                case "T*": tm = tlm = Matrix.Translate(0, -leading) * tlm; break;
                case "Tj" or "TJ" or "'" or "\"":
                    if (op.Name is "'" or "\"")
                        tm = tlm = Matrix.Translate(0, -leading) * tlm;
                    var at = tm * ctm;
                    if (at.E >= line.Left - tolerance && at.E <= line.Right + tolerance && at.F >= line.Bottom - tolerance && at.F <= line.Top + tolerance)
                    {
                        drop = true;
                        removed++;
                        // ' and " also move to the next line: keep that move.
                        if (op.Name is "'" or "\"")
                            output.Add(OpCodes.OperatorFromName("T*"));
                    }
                    break;
            }
            if (!drop)
                output.Add(op);
        }
        if (removed == 0)
            return 0;
        // The page's content streams become this one stream.
        page.Contents.Elements.Clear();
        page.Contents.AppendContent().CreateStream(output.ToContent());
        return removed;
    }

    /// <summary>Draws the new text where the line was: same baseline start, size and colour, the closest installed font.</summary>
    internal static void DrawLine(PdfPage page, PdfTextLine line, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var font = MatchFont(line);
        var (r, g, b) = PdfEditor.ParseColor(line.Color);
        var brush = new XSolidBrush(XColor.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255)));
        PdfEditor.DrawUnrotated(page, (gfx, height) =>
            gfx.DrawString(text, font, brush, new XPoint(line.BaselineX, height - line.BaselineY), XStringFormats.BaseLineLeft));
    }

    /// <summary>The installed font closest to the line's: its own family when installed, else Arial / Times New Roman / Courier New.</summary>
    internal static XFont MatchFont(PdfTextLine line)
    {
        var style = (line.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (line.Italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
        var family = FamilyOf(line.FontName);
        foreach (var candidate in new[] { family, Fallback(line.FontName) }.Where(f => !string.IsNullOrEmpty(f)).Distinct())
        {
            try
            {
                var font = new XFont(candidate!, line.FontSize, style);
                _ = font.GetHeight(); // fails here when the family isn't installed
                return font;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // try the next one
            }
        }
        return new XFont("Arial", line.FontSize, style);
    }

    /// <summary>"ABCDEF+Calibri-BoldItalic" → "Calibri"; "TimesNewRomanPSMT" → "Times New Roman".</summary>
    internal static string FamilyOf(string fontName)
    {
        var name = fontName.Contains('+') ? fontName[(fontName.IndexOf('+') + 1)..] : fontName;
        name = name.Split('-', ',')[0];
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                name = name[..^suffix.Length];
        return name switch
        {
            "TimesNewRoman" or "Times" => "Times New Roman",
            "CourierNew" or "Courier" => "Courier New",
            "Helvetica" => "Arial",
            _ => System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " "),
        };
    }

    private static string Fallback(string fontName)
    {
        var name = fontName.ToLowerInvariant();
        if (name.Contains("times") || name.Contains("serif") && !name.Contains("sans") || name.Contains("georgia") || name.Contains("garamond") || name.Contains("cambria"))
            return "Times New Roman";
        if (name.Contains("courier") || name.Contains("mono") || name.Contains("consol"))
            return "Courier New";
        return "Arial";
    }

    /// <summary>
    /// After saving: the edited lines hold their new text and every other word of the page is as before. Returns the
    /// problem, or null.
    /// </summary>
    internal static string? Verify(string sourcePath, int sourcePage, string? password, string outputPath, int outputPage,
        IReadOnlyList<(PdfTextLine Line, string NewText)> edits, string? outputPassword)
    {
        using var before = UglyToad.PdfPig.PdfDocument.Open(sourcePath, new UglyToad.PdfPig.ParsingOptions { Password = password ?? "", UseLenientParsing = true });
        using var after = UglyToad.PdfPig.PdfDocument.Open(outputPath, new UglyToad.PdfPig.ParsingOptions { Password = outputPassword ?? "", UseLenientParsing = true });
        bool Inside(UglyToad.PdfPig.Content.Word w) => edits.Any(e =>
        {
            var c = w.BoundingBox.Centroid;
            return c.X >= e.Line.Left - 1 && c.X <= e.Line.Right + 1 && c.Y >= e.Line.Bottom - 1 && c.Y <= e.Line.Top + 1;
        });
        // New text may run past the old line's right edge: its band is the line's height from its left edge on.
        bool InBand(UglyToad.PdfPig.Content.Word w) => edits.Any(e =>
        {
            var c = w.BoundingBox.Centroid;
            return c.X >= e.Line.Left - 1 && c.Y >= e.Line.Bottom - 1 && c.Y <= e.Line.Top + 1;
        });
        static string Key(IEnumerable<string> words) => string.Concat(words.Order(StringComparer.Ordinal));
        var oldWords = before.GetPage(sourcePage + 1).GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var newWords = after.GetPage(outputPage + 1).GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        // Every other word is still there; stamps or a watermark may add words, so this is a subset check.
        var remaining = newWords.Select(w => w.Text).ToList();
        if (oldWords.Where(w => !Inside(w)).Any(w => !remaining.Remove(w.Text)))
            return "other text on the page would change";
        var band = newWords.Where(InBand).Select(w => w.Text).ToList();
        foreach (var word in oldWords.Where(w => !Inside(w) && InBand(w)))
            band.Remove(word.Text);
        var expected = Key(edits.SelectMany(e => e.NewText.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        if (Key(band) != expected)
            return "the new text doesn't read back in place of the old";
        return null;
    }

    // ───────── Signing ─────────

    /// <summary>Saves <paramref name="document"/> to <paramref name="path"/> signed with the certificate (PDFsharp's signer, SHA-256).</summary>
    internal static void SaveSigned(PdfDocument document, string path, PdfSigning signing)
    {
        if (!signing.Certificate.HasPrivateKey)
            throw new PdfEditException("The certificate has no private key, so it can't sign. Use the .pfx file that includes the key.");
        var options = new DigitalSignatureOptions
        {
            Reason = signing.Reason ?? "",
            Location = signing.Location ?? "",
            AppName = "Foldex",
            PageIndex = 0,
            Rectangle = new XRect(0, 0, 0, 0),
        };
        DigitalSignatureHandler.ForDocument(document, new PdfSharpDefaultSigner(signing.Certificate, PdfMessageDigestType.SHA256), options);
        document.Save(path);
    }

    /// <summary>Loads a .pfx / .p12 certificate with its private key.</summary>
    public static X509Certificate2 LoadCertificate(string path, string? password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new PdfEditException("The certificate could not be opened. Check its password.", ex);
        }
    }

    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static Matrix Identity => new(1, 0, 0, 1, 0, 0);
        public static Matrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

        public static Matrix operator *(Matrix m, Matrix n) => new(
            m.A * n.A + m.B * n.C, m.A * n.B + m.B * n.D,
            m.C * n.A + m.D * n.C, m.C * n.B + m.D * n.D,
            m.E * n.A + m.F * n.C + n.E, m.E * n.B + m.F * n.D + n.F);
    }
}
