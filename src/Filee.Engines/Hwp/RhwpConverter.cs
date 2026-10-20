// HWP / HWPX → PDF, HWP → HWPX and HWPX → HWP with the rhwp command line tool (MIT, https://github.com/edwardkim/rhwp).
// Bundled under engines/rhwp. Works without Hancom Office, so there is no automation approval dialog.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Engines.Ebooks;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Hwp;

/// <summary>Renders HWP and HWPX documents to PDF and converts between the two 한글 formats.</summary>
public sealed class RhwpConverter : IConverter
{
    // rhwp renders text slowly: about 1 s per KB of section XML (some 4 s per page of a novel) with rhwp 0.8.7 on an
    // i7-1185G7, so a 640-page book takes some 48 minutes; export-hwpx and convert take seconds for the same book. The
    // time limit grows with the document's text, and still stops a rhwp that hangs.
    private static readonly TimeSpan BaseTimeLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TimeLimitPerMegabyte = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxTimeLimit = TimeSpan.FromMinutes(60);

    /// <summary>Usual rendering time per MB of section XML (drives the progress estimate of long documents).</summary>
    private static readonly TimeSpan TypicalPerMegabyte = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan TypicalDuration = TimeSpan.FromSeconds(1.5);

    private string? _exe;

    public string Id => "rhwp";
    public string DisplayName => "rhwp";
    public int MaxParallelism => 2;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("hwp", "pdf"),
        new("hwpx", "pdf"),
        // Lossless within the 한글 document model; anything → HWP goes through the HWPX writer first.
        new("hwp", "hwpx"),
        new("hwpx", "hwp"),
    ];

    public EngineStatus GetStatus()
    {
        _exe = Locate();
        return _exe is null ? EngineStatus.Unavailable("engine.reason.not_installed") : EngineStatus.Available(_exe, EngineVersions.Component("rhwp"));
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var exe = _exe ?? Locate() ?? throw new InvalidOperationException("rhwp was not found.");
        var temp = Path.Combine(step.WorkDirectory, $"rhwp-{Guid.NewGuid():N}.{step.To}");
        try
        {
            var input = step.To == "pdf" ? await PrepareAsync(exe, step, cancellationToken) : step.InputPath;
            string[] arguments = step.To switch
            {
                "pdf" => ["export-pdf", input, "-o", temp, .. RhwpInput.PdfOptions()],
                "hwpx" => ["export-hwpx", step.InputPath, temp],
                "hwp" => ["convert", step.InputPath, temp],
                _ => throw new NotSupportedException(step.To),
            };

            var text = TextSize(input);
            progress?.Report(0.1);
            ProcessResult result;
            using (ProgressEstimate.Start(progress, step.To == "pdf" ? Typical(text) : TypicalDuration))
                result = await ProcessRunner.RunAsync(exe, arguments, TimeLimit(text), cancellationToken);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                throw new InvalidOperationException($"rhwp failed (exit {result.ExitCode}). {result.StandardError.Trim()}");
        }
        catch (TimeoutException ex)
        {
            // E-books reach rhwp through the HWPX writer; calibre makes their PDFs much faster when it is ranked above it.
            throw new ConversionRefusedException(
                CalibreConverter.Locate() is null ? "error.rhwp_timeout" : "error.rhwp_timeout_calibre", ex.Message);
        }

        var target = step.Output.Allocate(step.To);
        if (target is null)
            return [];
        File.Move(temp, target, overwrite: true);
        progress?.Report(1);
        return [target];
    }

    /// <summary>
    /// The document rhwp renders: a copy with the fixes of <see cref="RhwpInput"/> (fonts, shape sizes, equation
    /// scripts, tables floating in cells) when it needs any, else the original. An HWP is read through an HWPX copy for
    /// that, and used as it is when nothing needs fixing or the HWPX copy fails.
    /// </summary>
    private static async Task<string> PrepareAsync(string exe, ConversionStep step, CancellationToken cancellationToken)
    {
        var hwpx = step.InputPath;
        if (step.From == "hwp")
        {
            hwpx = Path.Combine(step.WorkDirectory, $"rhwp-{Guid.NewGuid():N}.hwpx");
            await ProcessRunner.RunAsync(exe, ["export-hwpx", step.InputPath, hwpx], TimeLimit(TextSize(step.InputPath)), cancellationToken);
            if (!File.Exists(hwpx) || new FileInfo(hwpx).Length == 0)
                return step.InputPath;
        }
        return RhwpInput.Prepare(hwpx, step.WorkDirectory) ?? step.InputPath;
    }

    /// <summary>How long one rhwp run on a document with <paramref name="textBytes"/> of text may take.</summary>
    internal static TimeSpan TimeLimit(long textBytes)
    {
        var limit = BaseTimeLimit + TimeLimitPerMegabyte * (textBytes / 1048576.0);
        return limit < MaxTimeLimit ? limit : MaxTimeLimit;
    }

    private static TimeSpan Typical(long textBytes)
    {
        var typical = TypicalPerMegabyte * (textBytes / 1048576.0);
        return typical > TypicalDuration ? typical : TypicalDuration;
    }

    /// <summary>
    /// The size of a document's text, which rendering time follows: the unpacked section XML of an HWPX (pictures left
    /// out), else three times the file (an HWP deflates its body text to about a third).
    /// </summary>
    internal static long TextSize(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var sections = zip.Entries
                .Where(e => e.FullName.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase)
                            && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .Sum(e => e.Length);
            if (sections > 0)
                return sections;
        }
        catch (InvalidDataException) { } // not a ZIP: an HWP
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new FileInfo(path).Length * 3;
    }

    /// <summary>Filee's own copy (engines/rhwp), never one found elsewhere on the system.</summary>
    private static string? Locate() =>
        EngineEnvironment.FindBundled("rhwp") is { } bundled
            ? EngineEnvironment.FirstExisting(Path.Combine(bundled, OperatingSystem.IsWindows() ? "rhwp.exe" : "rhwp"))
            : null;
}
