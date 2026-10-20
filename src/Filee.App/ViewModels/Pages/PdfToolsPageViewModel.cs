// PDF tools for whole files: merge, split, compress (Filee's PDF compression, with before / after sizes), protect /
// unlock and text extraction. Results are new files next to the sources (never over them).

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Engines.Ocr;
using Filee.Engines.Pdf;

namespace Filee.App.ViewModels.Pages;

/// <summary>A file written by a tool, with what to say about it.</summary>
public sealed record ToolResult(string Path, string Detail, bool IsWarning = false)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record CompressionLevel(PdfCompression Value, string Title, string Hint)
{
    public override string ToString() => Title;
}

public sealed partial class PdfToolsPageViewModel : ObservableObject, IWorkspacePage
{
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly ConversionService? _conversions;
    private readonly RecentFiles? _recent;

    public PdfToolsPageViewModel(ILocalizer loc, IPlatformServices platform, ConversionService? conversions = null, RecentFiles? recent = null)
    {
        _loc = loc;
        _platform = platform;
        _conversions = conversions;
        _recent = recent;
        Levels =
        [
            new(PdfCompression.Print, loc["tools.level.high"], loc["tools.level.high_hint"]),
            new(PdfCompression.Ebook, loc["tools.level.balanced"], loc["tools.level.balanced_hint"]),
            new(PdfCompression.Screen, loc["tools.level.small"], loc["tools.level.small_hint"]),
        ];
        _level = Levels[1];
    }

    /// <summary>Asks for PDFs: (several) → paths.</summary>
    public Func<bool, Task<IReadOnlyList<string>>>? PickPdfsAsync { get; set; }
    /// <summary>Asks where to save: (suggested name) → path.</summary>
    public Func<string, Task<string?>>? PickSavePathAsync { get; set; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] private string? _message;
    [ObservableProperty] private bool _messageIsError;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public ObservableCollection<ToolResult> Results { get; } = [];

    // ───────── Merge ─────────

    public ObservableCollection<string> MergeFiles { get; } = [];
    [ObservableProperty] private string? _selectedMergeFile;

    [RelayCommand]
    private async Task AddMergeFiles()
    {
        foreach (var file in await Pick(true))
            MergeFiles.Add(file);
    }

    [RelayCommand]
    private void RemoveMergeFile()
    {
        if (SelectedMergeFile is { } file)
            MergeFiles.Remove(file);
    }

    [RelayCommand] private void MergeUp() => MoveMergeFile(-1);
    [RelayCommand] private void MergeDown() => MoveMergeFile(1);

    private void MoveMergeFile(int step)
    {
        if (SelectedMergeFile is not { } file)
            return;
        var index = MergeFiles.IndexOf(file);
        var target = index + step;
        if (target < 0 || target >= MergeFiles.Count)
            return;
        MergeFiles.Move(index, target);
        SelectedMergeFile = file;
    }

    [RelayCommand]
    private async Task Merge()
    {
        if (MergeFiles.Count < 2)
        {
            Error(_loc["tools.merge_need_two"]);
            return;
        }
        var output = PickSavePathAsync is null ? null : await PickSavePathAsync(_loc["tools.merged_name"]);
        if (output is null)
            return;
        var inputs = MergeFiles.ToList();
        await Run(() =>
        {
            PdfEditor.Merge(inputs, output);
            return [new ToolResult(output, _loc.Format("tools.merged", inputs.Count, PdfEditor.PageCount(output)))];
        });
    }

    // ───────── Split ─────────

    [ObservableProperty] private string? _splitFile;
    [ObservableProperty] private bool _splitEvery = true;
    [ObservableProperty] private decimal _splitSize = 1;
    [ObservableProperty] private string _splitRanges = "1-3, 4-";

    [RelayCommand]
    private async Task PickSplitFile() => SplitFile = (await Pick(false)).FirstOrDefault() ?? SplitFile;

    [RelayCommand]
    private async Task Split()
    {
        if (SplitFile is not { } file)
        {
            Error(_loc["tools.pick_file_first"]);
            return;
        }
        var spec = SplitEvery ? $"every:{(int)SplitSize}" : SplitRanges;
        await Run(() => PdfEditor.Split(file, spec, Path.GetDirectoryName(file)!)
            .Select(p => new ToolResult(p, _loc.Format("tools.pages", PdfEditor.PageCount(p)))).ToList());
    }

    // ───────── Compress ─────────

    public IReadOnlyList<CompressionLevel> Levels { get; }
    [ObservableProperty] private CompressionLevel _level;
    [ObservableProperty] private bool _compressRemoveMetadata;
    public ObservableCollection<string> CompressFiles { get; } = [];
    public bool CanCompress => _conversions is not null;

    [RelayCommand]
    private async Task AddCompressFiles()
    {
        foreach (var file in await Pick(true))
            if (!CompressFiles.Contains(file))
                CompressFiles.Add(file);
    }

    [RelayCommand]
    private void ClearCompressFiles() => CompressFiles.Clear();

    [RelayCommand]
    private async Task Compress()
    {
        if (CompressFiles.Count == 0 || _conversions is null)
        {
            Error(_loc["tools.pick_file_first"]);
            return;
        }
        var files = CompressFiles.ToList();
        var preset = new Preset
        {
            Name = _loc["tools.compress"],
            TargetFormat = "pdf",
            Pdf = new PdfOptions { Compression = Level.Value },
            Image = new ImageOptions { KeepMetadata = !CompressRemoveMetadata },
            Output = new OutputRule { Location = OutputLocation.SameFolder, FileNamePattern = "{name}-compressed" },
        };
        await RunConversion(files, preset, (source, output) =>
        {
            var before = new FileInfo(source).Length;
            var after = new FileInfo(output).Length;
            var saved = before == 0 ? 0 : (before - after) * 100.0 / before;
            return saved < 1
                ? new ToolResult(output, _loc.Format("tools.not_smaller", Size(before)), IsWarning: true)
                : new ToolResult(output, _loc.Format("tools.compressed", Size(before), Size(after), Math.Round(saved)));
        });
    }

    // ───────── Protect / unlock ─────────

    [ObservableProperty] private string? _securityFile;
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _currentPassword = "";

    [RelayCommand]
    private async Task PickSecurityFile() => SecurityFile = (await Pick(false)).FirstOrDefault() ?? SecurityFile;

    [RelayCommand]
    private async Task Protect()
    {
        if (SecurityFile is not { } file || NewPassword.Length == 0)
        {
            Error(_loc["tools.protect_need"]);
            return;
        }
        var password = NewPassword;
        var current = CurrentPassword.Length > 0 ? CurrentPassword : null;
        var output = PdfEditor.UniquePath(Path.Combine(Path.GetDirectoryName(file)!, _loc.Format("tools.protected_name", Path.GetFileNameWithoutExtension(file))));
        await Run(() =>
        {
            PdfEditor.Save(PdfEditor.AllPages(file, current), output, options: new PdfSaveOptions
            {
                UserPassword = password,
                SourcePasswords = current is null ? null : new Dictionary<string, string> { [file] = current },
            });
            return [new ToolResult(output, _loc["tools.protected"])];
        });
        NewPassword = "";
    }

    [RelayCommand]
    private async Task Unlock()
    {
        if (SecurityFile is not { } file || CurrentPassword.Length == 0)
        {
            Error(_loc["tools.unlock_need"]);
            return;
        }
        var current = CurrentPassword;
        var output = PdfEditor.UniquePath(Path.Combine(Path.GetDirectoryName(file)!, _loc.Format("tools.unlocked_name", Path.GetFileNameWithoutExtension(file))));
        await Run(() =>
        {
            PdfEditor.Save(PdfEditor.AllPages(file, current), output,
                options: new PdfSaveOptions { SourcePasswords = new Dictionary<string, string> { [file] = current } });
            return [new ToolResult(output, _loc["tools.unlocked"])];
        });
        CurrentPassword = "";
    }

    // ───────── Text ─────────

    public ObservableCollection<string> TextFiles { get; } = [];

    [RelayCommand]
    private async Task AddTextFiles()
    {
        foreach (var file in await Pick(true))
            if (!TextFiles.Contains(file))
                TextFiles.Add(file);
    }

    [RelayCommand]
    private void ClearTextFiles() => TextFiles.Clear();

    [RelayCommand]
    private async Task ExtractText()
    {
        if (TextFiles.Count == 0 || _conversions is null)
        {
            Error(_loc["tools.pick_file_first"]);
            return;
        }
        var preset = new Preset
        {
            Name = _loc["tools.text"],
            TargetFormat = "txt",
            Output = new OutputRule { Location = OutputLocation.SameFolder },
        };
        await RunConversion(TextFiles.ToList(), preset, (_, output) =>
        {
            var text = File.ReadAllText(output);
            return string.IsNullOrWhiteSpace(text)
                ? new ToolResult(output, _loc["tools.no_text"], IsWarning: true)
                : new ToolResult(output, _loc.Format("tools.characters", text.Length));
        });
    }

    // ───────── OCR ─────────

    /// <summary>Asks for scanned PDFs and pictures.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickOcrFilesAsync { get; set; }

    public ObservableCollection<string> OcrFiles { get; } = [];
    public ObservableCollection<OcrLanguage> OcrLanguages { get; } = [];
    [ObservableProperty] private OcrLanguage? _ocrLanguage;
    [ObservableProperty] private string? _ocrStatus;
    [ObservableProperty] private double _ocrProgress;
    public bool OcrSupported => WindowsOcr.IsSupported;
    private CancellationTokenSource? _ocrCancel;
    private bool _languagesLoaded;

    /// <summary>Loads the recognition languages Windows has (once, when the page is shown).</summary>
    public async Task LoadOcrLanguagesAsync()
    {
        if (_languagesLoaded || !OcrSupported)
            return;
        _languagesLoaded = true;
        try
        {
            foreach (var language in await WindowsOcr.LanguagesAsync())
                OcrLanguages.Add(language);
            var ui = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            OcrLanguage = OcrLanguages.FirstOrDefault(l => l.Tag.StartsWith(ui, StringComparison.OrdinalIgnoreCase)) ?? OcrLanguages.FirstOrDefault();
            OcrStatus = OcrLanguages.Count == 0 ? _loc["tools.ocr_no_language"] : null;
        }
        catch (Exception ex) when (ex is OcrException or System.ComponentModel.Win32Exception or IOException or System.Text.Json.JsonException)
        {
            OcrStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddOcrFiles()
    {
        foreach (var file in PickOcrFilesAsync is null ? [] : await PickOcrFilesAsync())
            if (!OcrFiles.Contains(file))
                OcrFiles.Add(file);
    }

    [RelayCommand]
    private void ClearOcrFiles() => OcrFiles.Clear();

    [RelayCommand]
    private void CancelOcr() => _ocrCancel?.Cancel();

    /// <summary>Scanned PDFs become "name (searchable).pdf", pictures "name.txt"; a file that fails is listed with why.</summary>
    [RelayCommand]
    private async Task RunOcr()
    {
        if (OcrFiles.Count == 0)
        {
            Error(_loc["tools.pick_file_first"]);
            return;
        }
        var cancel = _ocrCancel = new CancellationTokenSource();
        var results = new List<ToolResult>();
        var files = OcrFiles.ToList();
        try
        {
            IsBusy = true;
            Message = null;
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var folder = Path.GetDirectoryName(file)!;
                var name = Path.GetFileNameWithoutExtension(file);
                OcrStatus = _loc.Format("tools.ocr_reading", Path.GetFileName(file), i + 1, files.Count);
                try
                {
                    if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        var output = PdfEditor.UniquePath(Path.Combine(folder, _loc.Format("tools.ocr_pdf_name", name)));
                        var index = i;
                        var progress = new Progress<double>(p => OcrProgress = (index + p) / files.Count);
                        var result = await PdfOcr.MakeSearchableAsync(file, output, OcrLanguage?.Tag, null, progress, cancel.Token);
                        results.Add(result.RecognisedPages == 0 && result.SkippedPages == result.Pages
                            ? new ToolResult(output, _loc["tools.ocr_had_text"], IsWarning: true)
                            : new ToolResult(output, _loc.Format("tools.ocr_pdf_done", result.RecognisedPages, result.Words)));
                    }
                    else
                    {
                        var page = (await WindowsOcr.RecognizeAsync([file], OcrLanguage?.Tag, cancel.Token)).Single();
                        var output = PdfEditor.UniquePath(Path.Combine(folder, name + ".txt"));
                        await File.WriteAllTextAsync(output, page.Text, cancel.Token);
                        results.Add(string.IsNullOrWhiteSpace(page.Text)
                            ? new ToolResult(output, _loc["tools.ocr_nothing"], IsWarning: true)
                            : new ToolResult(output, _loc.Format("tools.characters", page.Text.Length)));
                    }
                }
                catch (Exception ex) when (ex is OcrException or PdfEditException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    results.Add(new ToolResult(file, ex.Message, IsWarning: true));
                }
                OcrProgress = (i + 1.0) / files.Count;
            }
            OcrStatus = null;
            Show(results);
        }
        catch (OperationCanceledException)
        {
            OcrStatus = _loc["utils.cancelled"];
            Show(results);
        }
        finally
        {
            IsBusy = false;
            OcrProgress = 0;
        }
    }

    // ───────── Shared ─────────

    [RelayCommand]
    private void Reveal(string path)
    {
        if (File.Exists(path))
            _platform.RevealInFileManager(path);
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    private async Task<IReadOnlyList<string>> Pick(bool multiple)
    {
        var files = PickPdfsAsync is null ? [] : await PickPdfsAsync(multiple);
        foreach (var file in files)
            _recent?.Add(file);
        return files;
    }

    private async Task Run(Func<IReadOnlyList<ToolResult>> work)
    {
        try
        {
            IsBusy = true;
            Message = null;
            var results = await Task.Run(work);
            Show(results);
        }
        catch (PdfEditException ex)
        {
            Error(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Runs a conversion through the app's queue (history, toast) and reports each output.</summary>
    private async Task RunConversion(IReadOnlyList<string> files, Preset preset, Func<string, string, ToolResult> describe)
    {
        if (_conversions is null)
            return;
        try
        {
            IsBusy = true;
            Message = null;
            var job = await _conversions.RunAsync(files, preset);
            if (job is null)
                return;
            var results = new List<ToolResult>();
            foreach (var file in job.Files)
            {
                if (file.State == FileState.Failed)
                    results.Add(new ToolResult(file.SourcePath, Filee.App.ViewModels.FailureText.Reason(_loc, file.ErrorKey, file.ErrorDetail), IsWarning: true));
                else
                    results.AddRange(file.Outputs.Where(File.Exists).Select(o => describe(file.SourcePath, o)));
            }
            Show(results);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(IReadOnlyList<ToolResult> results)
    {
        Results.Clear();
        foreach (var result in results)
            Results.Add(result);
        MessageIsError = false;
        Message = _loc.Format("tools.done", results.Count(r => !r.IsWarning));
    }

    private void Error(string text)
    {
        MessageIsError = true;
        Message = text;
    }

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
