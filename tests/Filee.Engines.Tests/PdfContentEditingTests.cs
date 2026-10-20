using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

/// <summary>Form filling, replacing existing text and certificate signing, checked on the written files.</summary>
public class PdfContentEditingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A one-page PDF with a text field "name" and a check box "agree" (raw AcroForm dictionaries).</summary>
    private static string MakeForm(string folder)
    {
        var path = Path.Combine(folder, "form.pdf");
        using var document = new PdfDocument();
        var page = document.AddPage();
        var annots = new PdfArray(document);
        page.Elements["/Annots"] = annots;
        var fields = new PdfArray(document);

        PdfDictionary Widget(string type, string name, double y)
        {
            var field = new PdfDictionary(document);
            field.Elements["/Type"] = new PdfName("/Annot");
            field.Elements["/Subtype"] = new PdfName("/Widget");
            field.Elements["/FT"] = new PdfName(type);
            field.Elements["/T"] = new PdfString(name);
            field.Elements["/Rect"] = new PdfArray(document, new PdfReal(72), new PdfReal(y), new PdfReal(300), new PdfReal(y + 20));
            field.Elements["/P"] = page.Reference!;
            document.Internals.AddObject(field);
            annots.Elements.Add(field.Reference!);
            fields.Elements.Add(field.Reference!);
            return field;
        }

        var text = Widget("/Tx", "name", 700);
        text.Elements["/V"] = new PdfString("");
        text.Elements["/DA"] = new PdfString("/Helv 12 Tf 0 g");
        var box = Widget("/Btn", "agree", 650);
        PdfDictionary Appearance()
        {
            var stream = new PdfDictionary(document);
            stream.Elements["/Type"] = new PdfName("/XObject");
            stream.Elements["/Subtype"] = new PdfName("/Form");
            stream.Elements["/BBox"] = new PdfArray(document, new PdfReal(0), new PdfReal(0), new PdfReal(20), new PdfReal(20));
            stream.CreateStream(Encoding.ASCII.GetBytes("0 0 m 20 20 l S"));
            document.Internals.AddObject(stream);
            return stream;
        }
        var normal = new PdfDictionary(document);
        normal.Elements["/Yes"] = Appearance().Reference!;
        normal.Elements["/Off"] = Appearance().Reference!;
        var ap = new PdfDictionary(document);
        ap.Elements["/N"] = normal;
        box.Elements["/AP"] = ap;
        box.Elements["/V"] = new PdfName("/Off");
        box.Elements["/AS"] = new PdfName("/Off");

        var form = new PdfDictionary(document);
        form.Elements["/Fields"] = fields;
        document.Internals.AddObject(form);
        document.Internals.Catalog.Elements["/AcroForm"] = form.Reference!;
        document.Save(path);
        return path;
    }

    [Fact]
    public void Fills_a_form_and_keeps_it()
    {
        var dir = fx.NewFolder();
        var source = MakeForm(dir);
        var fields = PdfContentEditing.ReadFields(source);
        Assert.Equal(["name", "agree"], fields.Select(f => f.Name));
        Assert.Equal([PdfFieldKind.Text, PdfFieldKind.CheckBox], fields.Select(f => f.Kind));

        var output = Path.Combine(dir, "filled.pdf");
        PdfEditor.Save(PdfEditor.AllPages(source), output,
            options: new PdfSaveOptions { FormValues = new Dictionary<string, string> { ["name"] = "Ada Lovelace", ["agree"] = "true" } });

        var filled = PdfContentEditing.ReadFields(output);
        Assert.Equal("Ada Lovelace", filled.Single(f => f.Name == "name").Value);
        Assert.Equal("true", filled.Single(f => f.Name == "agree").Value);
        Assert.Equal(0, PdfContentEditing.ReadFields(source).Count(f => f.Value is "Ada Lovelace" or "true"));
    }

    [Fact]
    public void Refuses_form_values_when_pages_are_rearranged_or_unknown()
    {
        var dir = fx.NewFolder();
        var source = MakeForm(dir);
        var output = Path.Combine(dir, "out.pdf");
        var values = new Dictionary<string, string> { ["name"] = "x" };

        Assert.Throws<PdfEditException>(() =>
            PdfEditor.Save([new(source, 0), new(source, 0)], output, options: new PdfSaveOptions { FormValues = values }));
        Assert.Throws<PdfEditException>(() => PdfEditor.Save(PdfEditor.AllPages(source), output,
            options: new PdfSaveOptions { FormValues = new Dictionary<string, string> { ["missing"] = "x" } }));
        Assert.False(File.Exists(output));
    }

    private static string MakeText(string folder)
    {
        var builder = new PdfBuilder();
        builder.NewPage();
        builder.Line("Invoice for March", 18, builder.Font(), 72, 100);
        builder.Line("Keep this line", 12, builder.Font(), 72, 200);
        return builder.Save(Path.Combine(folder, "text.pdf"));
    }

    private static List<string> Words(string path)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        return pig.GetPage(1).GetWords().Select(w => w.Text).ToList();
    }

    private static PdfTextLine LineOf(string path, string word)
    {
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            var box = pig.GetPage(1).GetWords().First(w => w.Text == word).BoundingBox;
            return PdfContentEditing.FindLine(path, 0, box.Centroid.X, box.Centroid.Y)
                   ?? throw new InvalidOperationException("No line found.");
        }
    }

    [Fact]
    public void Replaces_a_line_of_existing_text()
    {
        var dir = fx.NewFolder();
        var source = MakeText(dir);
        var line = LineOf(source, "March");
        Assert.Equal("Invoice for March", line.Text);
        Assert.Equal(18, line.FontSize, 0);

        var output = Path.Combine(dir, "edited.pdf");
        PdfEditor.Save(PdfEditor.AllPages(source), output,
            [new PdfMark(0, PdfMarkKind.ReplaceText, 0, 0, 0, 0, Text: "Invoice for April", Line: line)]);

        var words = Words(output);
        Assert.Contains("April", words);
        Assert.DoesNotContain("March", words);
        Assert.Contains("Keep", words);
        Assert.Contains("line", words);
        // The new line sits where the old one was.
        var edited = LineOf(output, "April");
        Assert.Equal(line.BaselineY, edited.BaselineY, 1);
        Assert.Equal(line.Left, edited.Left, 1);
        Assert.Contains("March", Words(source));
    }

    /// <summary>Regression (bug report): small text (8.5 pt lines, 17 pt apart) has line boxes a few points tall; clicks
    /// just above, below or past a line said "There's no editable text there". Every click in a line's band must pick
    /// that line and never its neighbour; a page without text and a click far from text say why.</summary>
    [Fact]
    public void Clicks_near_small_text_pick_the_nearest_line_and_misses_say_why()
    {
        var dir = fx.NewFolder();
        var builder = new PdfBuilder();
        builder.NewPage(612, 792);
        for (var i = 0; i < 10; i++)
            builder.Line($"Body line {i} with some words in small type", 8.5, builder.Font(), 54, 120 + i * 17);
        builder.NewPage(612, 792); // no text at all
        var path = builder.Save(Path.Combine(dir, "small.pdf"));

        for (var i = 0; i < 10; i++)
        {
            var baseline = 120 + i * 17.0; // from the top
            // Clicks from 7 points above the baseline (in the gap above) to 7 below (the gap below), and just before the start.
            foreach (var dy in new[] { -7.0, -3.0, 0.0, 2.0, 7.0 })
                foreach (var x in new[] { 50.0, 150.0, 200.0 })
                {
                    var hit = PdfContentEditing.HitTest(path, 0, x, baseline - 3 + dy);
                    Assert.True(hit.Line?.Text.StartsWith($"Body line {i} ") == true,
                        $"click {dy:+0;-0} pt off line {i} at x {x}: {hit.Miss} {hit.Line?.Text}");
                }
        }
        Assert.Equal(PdfTextMiss.NothingHere, PdfContentEditing.HitTest(path, 0, 500, 600).Miss);
        Assert.Equal(PdfTextMiss.NoTextOnPage, PdfContentEditing.HitTest(path, 1, 100, 100).Miss);
    }

    /// <summary>Regression: drawing on a page added an empty /CropBox [0 0 0 0] to it (PDFsharp's CropBox getter creates
    /// one), so a text change saved over the file could not be picked again. Saved pages never get one, and files that
    /// already have one are repaired.</summary>
    [Fact]
    public void Saving_never_writes_an_empty_crop_box_and_repairs_one()
    {
        var dir = fx.NewFolder();
        var source = MakeText(dir);
        var path = Path.Combine(dir, "edited.pdf");
        File.Copy(source, path);
        // Three rounds of picking a line by clicking where it shows, changing it and saving over the file.
        foreach (var month in new[] { "April", "May", "June" })
        {
            var hit = PdfContentEditing.HitTest(path, 0, 100, 100 - 5);
            Assert.True(hit.Line is not null, $"{month}: {hit.Miss}");
            PdfEditor.Save(PdfEditor.AllPages(path), path,
                [new PdfMark(0, PdfMarkKind.ReplaceText, 0, 0, 0, 0, Text: $"Invoice for {month}", Line: hit.Line),
                 new PdfMark(0, PdfMarkKind.Image, 300, 500 + 30 * Array.IndexOf(["April", "May", "June"], month), 60, 20, Text: Ink(dir))]);
            using var saved = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Assert.False(saved.Pages[0].Elements.ContainsKey("/CropBox") && saved.Pages[0].CropBox.IsZero, $"{month}: empty crop box written");
            Assert.Contains(month, Words(path));
        }

        // A file saved by an earlier build, with an empty crop box: hit-testing uses the media box, and saving removes it.
        var broken = Path.Combine(dir, "broken.pdf");
        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            document.Pages[0].Elements["/CropBox"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
            document.Save(broken);
        }
        Assert.NotNull(PdfContentEditing.HitTest(broken, 0, 100, 95).Line);
        var repaired = Path.Combine(dir, "repaired.pdf");
        PdfEditor.Save(PdfEditor.AllPages(broken), repaired, [new PdfMark(0, PdfMarkKind.Rectangle, 10, 10, 40, 40)]);
        using (var saved = PdfReader.Open(repaired, PdfDocumentOpenMode.Import))
            Assert.False(saved.Pages[0].Elements.ContainsKey("/CropBox"));
    }

    private static string Ink(string dir)
    {
        var path = Path.Combine(dir, "ink.png");
        if (!File.Exists(path))
            using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Black, 60, 20))
                image.Write(path);
        return path;
    }

    [Fact]
    public void Signs_with_a_certificate()
    {
        var dir = fx.NewFolder();
        var source = MakeText(dir);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Foldex Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        var output = Path.Combine(dir, "signed.pdf");

        PdfEditor.Save(PdfEditor.AllPages(source), output, options: new PdfSaveOptions { Signing = new PdfSigning(certificate, "Approved") });

        var bytes = File.ReadAllBytes(output);
        var text = Encoding.Latin1.GetString(bytes);
        var range = Regex.Match(text, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        Assert.True(range.Success, "no /ByteRange");
        var r = Enumerable.Range(1, 4).Select(i => int.Parse(range.Groups[i].Value)).ToArray();
        var signedData = bytes[r[0]..(r[0] + r[1])].Concat(bytes[r[2]..(r[2] + r[3])]).ToArray();
        var contents = Convert.FromHexString(text[(r[0] + r[1] + 1)..(r[2] - 1)]);
        // The placeholder is zero-padded: keep the DER SEQUENCE only (30 82 hi lo).
        Assert.Equal(0x30, contents[0]);
        var der = contents[..(4 + (contents[2] << 8 | contents[3]))];
        var cms = new SignedCms(new ContentInfo(signedData), detached: true);
        cms.Decode(der);
        cms.CheckSignature(verifySignatureOnly: true);
        Assert.Equal("CN=Foldex Test", cms.SignerInfos[0].Certificate!.Subject);
        Assert.Throws<PdfEditException>(() => PdfEditor.Save(PdfEditor.AllPages(source), Path.Combine(dir, "both.pdf"),
            options: new PdfSaveOptions { Signing = new PdfSigning(certificate), UserPassword = "pw" }));
    }
}
