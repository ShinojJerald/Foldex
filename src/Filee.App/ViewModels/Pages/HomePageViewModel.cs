// Home: drop zone (handled in the view) and recent conversions.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class HistoryItemViewModel : ObservableObject
{
    private readonly HistoryEntry _entry;
    private readonly IPlatformServices _platform;
    private readonly ConversionService? _conversions;

    public HistoryItemViewModel(HistoryEntry entry, ILocalizer loc, IPlatformServices platform, ConversionService? conversions = null)
    {
        _entry = entry;
        _platform = platform;
        _conversions = conversions;
        Title = entry.PresetName;
        Subtitle = entry.Sources.Count == 1 ? Path.GetFileName(entry.Sources[0]) : loc.Format("home.files", entry.Sources.Count);
        Time = entry.FinishedAt.Date == DateTime.Today ? entry.FinishedAt.ToString("t") : entry.FinishedAt.ToString("d");
        Failures = entry.Failures.Count > 0
            ? entry.Failures.Select(f => new FailureItem(Path.GetFileName(f.Source), FailureText.Reason(loc, f.ErrorKey, f.ErrorDetail))).ToList()
            : entry.Errors.Select(line => FailureText.FromLegacy(loc, line)).ToList();
        HasErrors = Failures.Count > 0 || entry.State == JobState.Failed;
        FailureSummary = loc.Format("home.failed_files", Failures.Count);
        HasOutputs = entry.Outputs.Any(ConversionService.Exists);
        // Only entries that remember their preset (Filee 1.8 and later) and still have a failed file to convert.
        CanRetry = conversions is not null && entry.Preset is not null && entry.Failures.Any(f => ConversionService.Exists(f.Source));
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Time { get; }
    public bool HasErrors { get; }
    public bool HasOutputs { get; }

    /// <summary>The files that failed and why.</summary>
    public IReadOnlyList<FailureItem> Failures { get; }

    public bool HasFailures => Failures.Count > 0;

    /// <summary>"2 files failed": the button that shows the list.</summary>
    public string FailureSummary { get; }

    public bool CanRetry { get; }

    [ObservableProperty] private bool _showFailures;

    [RelayCommand]
    private void ToggleFailures() => ShowFailures = !ShowFailures;

    /// <summary>Converts the failed files again the way the job ran them; progress shows in the toast.</summary>
    [RelayCommand]
    private void Retry()
    {
        if (_conversions is not null && _entry.Preset is not null)
            _conversions.Retry(_entry.Failures.Select(f => f.Source), _entry.Preset, _entry.WatchRuleId);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        // An output may be a folder ("Extract"): revealing it opens the extracted files.
        var output = _entry.Outputs.FirstOrDefault(ConversionService.Exists);
        if (output is not null)
            _platform.RevealInFileManager(output);
    }
}

/// <summary>A file on Home's recent list: opened in Foldex or written by a conversion.</summary>
public sealed record RecentFileItem(string Path, string Name, string Folder, string When, string Size, string Kind, string Badge, string BadgeColor)
{
    private static readonly Dictionary<string, (string Kind, string Color)> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = ("pdf", "#E11D48"),
        ["jpg"] = ("image", "#0EA5E9"), ["jpeg"] = ("image", "#0EA5E9"), ["png"] = ("image", "#0EA5E9"), ["webp"] = ("image", "#0EA5E9"),
        ["gif"] = ("image", "#0EA5E9"), ["heic"] = ("image", "#0EA5E9"), ["avif"] = ("image", "#0EA5E9"), ["tif"] = ("image", "#0EA5E9"),
        ["tiff"] = ("image", "#0EA5E9"), ["bmp"] = ("image", "#0EA5E9"), ["svg"] = ("image", "#0EA5E9"), ["ico"] = ("image", "#0EA5E9"),
        ["docx"] = ("document", "#2563EB"), ["doc"] = ("document", "#2563EB"), ["odt"] = ("document", "#2563EB"), ["rtf"] = ("document", "#2563EB"),
        ["txt"] = ("document", "#64748B"), ["md"] = ("document", "#64748B"), ["hwp"] = ("document", "#2563EB"), ["hwpx"] = ("document", "#2563EB"),
        ["xlsx"] = ("document", "#16A34A"), ["xls"] = ("document", "#16A34A"), ["csv"] = ("document", "#16A34A"), ["ods"] = ("document", "#16A34A"),
        ["pptx"] = ("document", "#EA580C"), ["ppt"] = ("document", "#EA580C"), ["odp"] = ("document", "#EA580C"), ["epub"] = ("document", "#7C3AED"),
        ["mp4"] = ("video", "#DB2777"), ["mkv"] = ("video", "#DB2777"), ["mov"] = ("video", "#DB2777"), ["webm"] = ("video", "#DB2777"), ["avi"] = ("video", "#DB2777"),
        ["mp3"] = ("audio", "#9333EA"), ["wav"] = ("audio", "#9333EA"), ["flac"] = ("audio", "#9333EA"), ["m4a"] = ("audio", "#9333EA"),
        ["ogg"] = ("audio", "#9333EA"), ["opus"] = ("audio", "#9333EA"), ["aac"] = ("audio", "#9333EA"),
    };

    public static RecentFileItem From(FileInfo file)
    {
        var extension = file.Extension.TrimStart('.');
        var (kind, color) = Kinds.GetValueOrDefault(extension, ("other", "#475569"));
        var when = file.LastWriteTime.Date == DateTime.Today ? file.LastWriteTime.ToString("t") : file.LastWriteTime.ToString("g");
        var badge = extension.Length is > 0 and <= 4 ? extension.ToUpperInvariant() : "FILE";
        return new RecentFileItem(file.FullName, file.Name, file.DirectoryName ?? "", when, PdfToolsPageViewModel.Size(file.Length), kind, badge, color);
    }
}

/// <summary>A filter chip above the recent files.</summary>
public sealed partial class RecentFilter(string kind, string title) : ObservableObject
{
    public string Kind { get; } = kind;
    public string Title { get; } = title;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class HomePageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly Services.Triggers.TriggerService? _triggers;
    private readonly ConversionService? _conversions;
    private readonly ElevationService? _elevation;

    public HomePageViewModel(UserDataStore store, ILocalizer loc, IPlatformServices platform, Services.Triggers.TriggerService? triggers = null,
        ConversionService? conversions = null, ElevationService? elevation = null, RecentFiles? recent = null)
    {
        _recent = recent;
        _store = store;
        Jobs = conversions?.Jobs ?? [];
        Filters = new[] { "all", "pdf", "image", "document", "video", "audio", "other" }
            .Select(k => new RecentFilter(k, loc[$"home.filter.{k}"])).ToList();
        Filters[0].IsSelected = true;
        _loc = loc;
        _platform = platform;
        _triggers = triggers;
        _conversions = conversions;
        _elevation = elevation;
        if (triggers is not null)
            triggers.StatusChanged += OnTriggerStatusChanged;
        if (elevation is not null)
            elevation.PropertyChanged += OnElevationChanged;
        var drag = store.Settings.Triggers.FirstOrDefault(t => t.Enabled && t.Kind == TriggerKind.Drag);
        DropHint = loc.Format(OperatingSystem.IsMacOS() ? "home.drop_hint_macos" : OperatingSystem.IsLinux() ? "home.drop_hint_linux" : "home.drop_hint", drag is null ? "—" : GestureText.Modifiers(drag.Modifiers));
        store.HistoryChanged += OnHistoryChanged;
        if (recent is not null)
            recent.Changed += OnHistoryChanged;
        Reload();
    }

    private readonly RecentFiles? _recent;

    /// <summary>Conversions running now and finished ones not yet dismissed (Task queue).</summary>
    public System.Collections.ObjectModel.ObservableCollection<JobViewModel> Jobs { get; }

    public IReadOnlyList<RecentFilter> Filters { get; }
    public ObservableCollection<RecentFileItem> RecentFiles { get; } = [];
    private List<RecentFileItem> _allRecent = [];
    [ObservableProperty] private bool _hasRecentFiles;

    [RelayCommand]
    private void Filter(RecentFilter filter)
    {
        foreach (var f in Filters)
            f.IsSelected = f == filter;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var kind = Filters.First(f => f.IsSelected).Kind;
        RecentFiles.Clear();
        foreach (var item in _allRecent.Where(i => kind == "all" || i.Kind == kind).Take(12))
            RecentFiles.Add(item);
        HasRecentFiles = _allRecent.Count > 0;
    }

    /// <summary>Files opened in Foldex and files conversions wrote, newest first, that still exist.</summary>
    private void LoadRecentFiles()
    {
        var paths = (_recent?.Items ?? [])
            .Concat(_store.History.Take(40).SelectMany(h => h.Outputs))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        _allRecent = paths.Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTime).Take(60).Select(RecentFileItem.From).ToList();
        ApplyFilter();
    }

    [RelayCommand]
    private void RevealFile(string path)
    {
        if (File.Exists(path))
            _platform.RevealInFileManager(path);
    }

    /// <summary>Removes finished jobs from the task queue.</summary>
    [RelayCommand]
    private void ClearFinishedJobs()
    {
        foreach (var job in Jobs.Where(j => !j.IsRunning).ToList())
            job.DismissCommand.Execute(null);
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Reload);

    public void Dispose()
    {
        _store.HistoryChanged -= OnHistoryChanged;
        if (_recent is not null)
            _recent.Changed -= OnHistoryChanged;
        if (_triggers is not null)
            _triggers.StatusChanged -= OnTriggerStatusChanged;
        if (_elevation is not null)
            _elevation.PropertyChanged -= OnElevationChanged;
    }

    private void OnTriggerStatusChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(NeedsAccessibility));

    private void OnElevationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(ElevationRestartFailed));

    /// <summary>
    /// Windows: Filee runs as administrator while Explorer doesn't, so drops, the Explorer menu and the shortcut can't
    /// reach it (the warning above the drop zone).
    /// </summary>
    public bool IsElevated => _elevation?.ShellCannotReachFilee == true;

    /// <summary>The restart button didn't work: the warning says what to do instead.</summary>
    public bool ElevationRestartFailed => _elevation?.RestartFailed == true;

    [RelayCommand]
    private void RestartWithoutAdministratorRights() => _elevation?.RestartWithoutAdministratorRights();

    /// <summary>macOS: gestures wait for the Accessibility permission (the reminder above the drop zone).</summary>
    public bool NeedsAccessibility => _triggers?.NeedsPermission == true;

    [RelayCommand]
    private void OpenAccessibility()
    {
        if (OperatingSystem.IsMacOS() && _platform is Filee.Platform.MacOS.MacOSPlatformServices mac)
            mac.OpenAccessibilitySettings();
    }

    public string DropHint { get; }



    public ObservableCollection<HistoryItemViewModel> History { get; } = [];

    [ObservableProperty] private bool _isEmpty;

    private void Reload()
    {
        History.Clear();
        foreach (var entry in _store.History.Take(40))
            History.Add(new HistoryItemViewModel(entry, _loc, _platform, _conversions));
        IsEmpty = History.Count == 0;
        LoadRecentFiles();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _store.ClearHistory();
        Reload();
    }
}
