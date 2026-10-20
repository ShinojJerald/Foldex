// Text recognition with the OCR engine built into Windows (Windows.Media.Ocr), run through Windows PowerShell with the
// embedded windows-ocr.ps1. Local only: no download, no account, no upload; the languages are the ones Windows has.
// Paths go to the script in a list file and as separate arguments, never inside a command string.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Filee.Engines.Ocr;

public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);

public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

/// <summary>What was recognised in one picture; word boxes are in its pixels.</summary>
public sealed record OcrPage(int Width, int Height, IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
}

public sealed record OcrLanguage(string Tag, string Name)
{
    public override string ToString() => Name;
}

public sealed class OcrException(string message, Exception? inner = null) : Exception(message, inner);

public static class WindowsOcr
{
    /// <summary>Windows 10 and later have the engine (with at least one recognition language installed).</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);

    /// <summary>The recognition languages Windows has installed.</summary>
    public static async Task<IReadOnlyList<OcrLanguage>> LanguagesAsync(CancellationToken cancellationToken = default)
    {
        var json = await RunAsync(["-Languages"], cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(e => new OcrLanguage(e.GetProperty("tag").GetString() ?? "", e.GetProperty("name").GetString() ?? ""))
            .ToList();
    }

    /// <summary>Recognises the text of pictures (PNG, JPEG, BMP, TIFF, GIF), in order. Null language: the user's.</summary>
    public static async Task<IReadOnlyList<OcrPage>> RecognizeAsync(IReadOnlyList<string> images, string? language, CancellationToken cancellationToken = default)
    {
        if (images.Count == 0)
            return [];
        var list = Path.Combine(Path.GetTempPath(), $"filee-ocr-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(list, images.Select(Path.GetFullPath), new UTF8Encoding(false), cancellationToken);
            List<string> arguments = ["-List", list];
            if (!string.IsNullOrWhiteSpace(language))
                arguments.AddRange(["-Language", language]);
            var json = await RunAsync(arguments, cancellationToken);
            return Parse(json);
        }
        finally
        {
            try { File.Delete(list); } catch (IOException) { }
        }
    }

    internal static IReadOnlyList<OcrPage> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        // One picture comes back as an object rather than a one-item array from older PowerShell.
        var pages = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        return pages.Select(p => new OcrPage(
            p.GetProperty("width").GetInt32(), p.GetProperty("height").GetInt32(),
            Items(p, "lines").Select(l => new OcrLine(l.GetProperty("text").GetString() ?? "",
                Items(l, "words").Select(w => new OcrWord(w.GetProperty("text").GetString() ?? "",
                    w.GetProperty("x").GetDouble(), w.GetProperty("y").GetDouble(),
                    w.GetProperty("w").GetDouble(), w.GetProperty("h").GetDouble())).ToList())).ToList())).ToList();

        static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
            !element.TryGetProperty(name, out var value) ? []
            : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray()
            : value.ValueKind == JsonValueKind.Object ? [value] : [];
    }

    private static async Task<string> RunAsync(IReadOnlyList<string> scriptArguments, CancellationToken cancellationToken)
    {
        if (!IsSupported)
            throw new OcrException("Text recognition uses the OCR engine built into Windows 10 and 11.");
        // A new file each run (never one that might already be there), removed afterwards.
        var script = Path.Combine(Path.GetTempPath(), $"filee-ocr-{Guid.NewGuid():N}.ps1");
        await using (var file = new FileStream(script, FileMode.CreateNew, FileAccess.Write))
        await using (var writer = new StreamWriter(file, new UTF8Encoding(true)))
            await writer.WriteAsync(Script);
        try
        {
            return await RunScriptAsync(script, scriptArguments, cancellationToken);
        }
        finally
        {
            try { File.Delete(script); } catch (IOException) { }
        }
    }

    private static async Task<string> RunScriptAsync(string script, IReadOnlyList<string> scriptArguments, CancellationToken cancellationToken)
    {

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script])
            start.ArgumentList.Add(argument);
        foreach (var argument in scriptArguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new OcrException("Windows PowerShell could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            var message = (await error).Trim();
            // Windows PowerShell prints the script error first; the rest is its position in the script.
            var first = message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            throw new OcrException(string.IsNullOrEmpty(first) ? $"Text recognition failed (exit code {process.ExitCode})." : first);
        }
        return (await output).Trim();
    }

    private static readonly Lazy<string> ScriptText = new(() =>
    {
        using var stream = typeof(WindowsOcr).Assembly.GetManifestResourceStream("Filee.Engines.Ocr.windows-ocr.ps1")
                           ?? throw new OcrException("The OCR script is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static string Script => ScriptText.Value;
}
