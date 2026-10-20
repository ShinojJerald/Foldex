// File utilities: read-only folder scans (duplicates, largest files, wrong extensions), comparing two files, picture
// metadata (with a verified clean copy) and batch rename with a preview. Scans never delete or move anything.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.Core.Files;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Engines.Magick;
using Filee.Engines.Pdf;

namespace Filee.App.ViewModels.Pages;

/// <summary>One line of a scan result.</summary>
public sealed record ScanRow(string Title, string Detail, string? Path, bool IsHeader = false);

public sealed partial class UtilitiesPageViewModel : ObservableObject, IWorkspacePage, IDisposable
{
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private CancellationTokenSource? _scan;

    public UtilitiesPageViewModel(ILocalizer loc, IPlatformServices platform)
    {
        _loc = loc;
        _platform = platform;
    }

    public Func<Task<string?>>? PickFolderAsync { get; set; }
    public Func<bool, Task<IReadOnlyList<string>>>? PickFilesAsync { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] private string? _message;
    [ObservableProperty] private bool _messageIsError;
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    // ───────── Folder scans ─────────

    [ObservableProperty] private string? _folder;
    [ObservableProperty] private bool _recursive = true;
    public ObservableCollection<ScanRow> ScanResults { get; } = [];

    [RelayCommand]
    private async Task PickFolder() => Folder = (PickFolderAsync is null ? null : await PickFolderAsync()) ?? Folder;

    [RelayCommand]
    private Task FindDuplicates() => Scan(ct =>
    {
        var progress = new Progress<double>(p => Progress = p);
        var groups = FileScanner.FindDuplicates(Folder!, Recursive, progress, ct);
        var rows = new List<ScanRow>();
        foreach (var group in groups.Take(200))
        {
            rows.Add(new ScanRow(_loc.Format("utils.dup_group", group.Paths.Count, PdfToolsPageViewModel.Size(group.Size)),
                _loc.Format("utils.dup_wasted", PdfToolsPageViewModel.Size(group.Wasted)), null, IsHeader: true));
            rows.AddRange(group.Paths.Select(p => new ScanRow(Path.GetFileName(p), p, p)));
        }
        var wasted = groups.Sum(g => g.Wasted);
        return (rows, groups.Count == 0 ? _loc["utils.dup_none"] : _loc.Format("utils.dup_summary", groups.Count, PdfToolsPageViewModel.Size(wasted)));
    });

    [RelayCommand]
    private Task FindLargest() => Scan(_ =>
    {
        var files = FileScanner.Largest(Folder!, 50, Recursive);
        return (files.Select(f => new ScanRow(f.Name, $"{PdfToolsPageViewModel.Size(f.Length)} · {f.DirectoryName}", f.FullName)).ToList(),
            _loc.Format("utils.largest_summary", files.Count));
    });

    [RelayCommand]
    private Task FindMismatches() => Scan(ct =>
    {
        var found = FileScanner.FindMismatches(Folder!, Recursive, ct);
        return (found.Select(m => new ScanRow(Path.GetFileName(m.Path), _loc.Format("utils.mismatch_detail", m.DetectedType, m.SuggestedExtension), m.Path)).ToList(),
            found.Count == 0 ? _loc["utils.mismatch_none"] : _loc.Format("utils.mismatch_summary", found.Count));
    });

    [RelayCommand]
    private void CancelScan() => _scan?.Cancel();

    private async Task Scan(Func<CancellationToken, (List<ScanRow> Rows, string Summary)> work)
    {
        if (Folder is null || !Directory.Exists(Folder))
        {
            Error(_loc["utils.pick_folder_first"]);
            return;
        }
        _scan?.Cancel();
        var cancel = _scan = new CancellationTokenSource();
        try
        {
            IsBusy = true;
            Progress = 0;
            ScanResults.Clear();
            var (rows, summary) = await Task.Run(() => work(cancel.Token), cancel.Token);
            foreach (var row in rows)
                ScanResults.Add(row);
            Info(summary);
        }
        catch (OperationCanceledException)
        {
            Info(_loc["utils.cancelled"]);
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

    // ───────── Compare two files ─────────

    [ObservableProperty] private string? _compareA;
    [ObservableProperty] private string? _compareB;
    [ObservableProperty] private string? _compareResult;

    [RelayCommand] private async Task PickCompareA() => CompareA = (await PickOne()) ?? CompareA;
    [RelayCommand] private async Task PickCompareB() => CompareB = (await PickOne()) ?? CompareB;

    [RelayCommand]
    private async Task Compare()
    {
        if (CompareA is not { } a || CompareB is not { } b)
        {
            Error(_loc["utils.compare_need"]);
            return;
        }
        try
        {
            IsBusy = true;
            var same = await Task.Run(() => FileScanner.SameContent(a, b));
            CompareResult = same ? _loc["utils.compare_same"]
                : _loc.Format("utils.compare_different", PdfToolsPageViewModel.Size(new FileInfo(a).Length), PdfToolsPageViewModel.Size(new FileInfo(b).Length));
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

    // ───────── Picture metadata ─────────

    [ObservableProperty] private string? _metadataFile;
    [ObservableProperty] private string? _gpsText;
    public ObservableCollection<MetadataEntry> MetadataEntries { get; } = [];

    [RelayCommand]
    private async Task PickMetadataFile()
    {
        var file = await PickOne();
        if (file is null)
            return;
        MetadataFile = file;
        await LoadMetadata(file);
    }

    private async Task LoadMetadata(string file)
    {
        MetadataEntries.Clear();
        GpsText = null;
        try
        {
            var report = await Task.Run(() => ImageMetadata.Read(file));
            foreach (var entry in report.Entries)
                MetadataEntries.Add(entry);
            GpsText = report.HasGps
                ? _loc.Format("utils.gps", report.Latitude!.Value.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture),
                    report.Longitude!.Value.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture))
                : null;
            Info(report.IsEmpty ? _loc["utils.metadata_none"] : _loc.Format("utils.metadata_count", report.Entries.Count));
        }
        catch (Exception ex) when (ex is ImageMagick.MagickException or IOException or UnauthorizedAccessException)
        {
            Error(_loc.Format("utils.metadata_unreadable", ex.Message));
        }
    }

    [RelayCommand]
    private async Task RemoveMetadata()
    {
        if (MetadataFile is not { } file)
            return;
        var output = PdfEditor.UniquePath(Path.Combine(Path.GetDirectoryName(file)!,
            Path.GetFileNameWithoutExtension(file) + "-clean" + Path.GetExtension(file)));
        try
        {
            IsBusy = true;
            await Task.Run(() => ImageMetadata.WriteWithout(file, output));
            Info(_loc.Format("utils.metadata_removed", Path.GetFileName(output)));
            LastOutput = output;
        }
        catch (Exception ex) when (ex is ImageMagick.MagickException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [ObservableProperty] private string? _lastOutput;

    // ───────── Batch rename ─────────

    public ObservableCollection<string> RenameFiles { get; } = [];
    public ObservableCollection<RenamePlan> RenamePreview { get; } = [];
    [ObservableProperty] private string _renamePattern = "{name}";
    [ObservableProperty] private string _renameFind = "";
    [ObservableProperty] private string _renameReplace = "";
    [ObservableProperty] private decimal _renameStart = 1;

    partial void OnRenamePatternChanged(string value) => UpdateRenamePreview();
    partial void OnRenameFindChanged(string value) => UpdateRenamePreview();
    partial void OnRenameReplaceChanged(string value) => UpdateRenamePreview();
    partial void OnRenameStartChanged(decimal value) => UpdateRenamePreview();

    [RelayCommand]
    private async Task AddRenameFiles()
    {
        foreach (var file in PickFilesAsync is null ? [] : await PickFilesAsync(true))
            if (!RenameFiles.Contains(file))
                RenameFiles.Add(file);
        UpdateRenamePreview();
    }

    [RelayCommand]
    private void ClearRenameFiles()
    {
        RenameFiles.Clear();
        UpdateRenamePreview();
    }

    private void UpdateRenamePreview()
    {
        RenamePreview.Clear();
        foreach (var plan in BatchRename.Plan(RenameFiles.Where(File.Exists).ToList(), RenamePattern, RenameFind, RenameReplace, (int)RenameStart))
            RenamePreview.Add(plan);
    }

    [RelayCommand]
    private async Task ApplyRename()
    {
        var plans = RenamePreview.ToList();
        var changing = plans.Count(p => p.Changes);
        if (changing == 0)
        {
            Error(_loc["utils.rename_nothing"]);
            return;
        }
        if (ConfirmAsync is not null && !await ConfirmAsync(_loc["utils.rename"], _loc.Format("utils.rename_confirm", changing)))
            return;
        var renamed = BatchRename.Apply(plans);
        var map = plans.Where(p => p.Changes).ToDictionary(p => p.Path, p => Path.Combine(Path.GetDirectoryName(p.Path)!, p.NewName));
        var updated = RenameFiles.Select(f => map.TryGetValue(f, out var n) && File.Exists(n) ? n : f).ToList();
        RenameFiles.Clear();
        foreach (var file in updated)
            RenameFiles.Add(file);
        UpdateRenamePreview();
        Info(_loc.Format("utils.renamed", renamed, changing));
    }

    // ───────── Shared ─────────

    [RelayCommand]
    private void Reveal(string? path)
    {
        if (path is not null && (File.Exists(path) || Directory.Exists(path)))
            _platform.RevealInFileManager(path);
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    private async Task<string?> PickOne() => PickFilesAsync is null ? null : (await PickFilesAsync(false)).FirstOrDefault();

    private void Info(string text)
    {
        MessageIsError = false;
        Message = text;
    }

    private void Error(string text)
    {
        MessageIsError = true;
        Message = text;
    }

    public void Dispose() => _scan?.Cancel();
}
