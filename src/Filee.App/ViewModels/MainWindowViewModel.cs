// Main (settings) window: left navigation + current page.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Filee.App.ViewModels;

/// <summary>Something the top bar's search finds: a page, or a tool on a page.</summary>
public sealed record SearchEntry(string Title, string Detail, Geometry? Icon, string Page)
{
    public override string ToString() => Title;
}

/// <summary>Navigation entry. <see cref="Title"/> follows the UI language.</summary>
public sealed partial class NavItem(string key, string titleKey, string iconKey, Type pageType, bool workspace = false) : ObservableObject
{
    public string Key { get; } = key;
    /// <summary>In the top group (tools to work with files) rather than the settings below it.</summary>
    public bool IsWorkspace { get; } = workspace;
    public string TitleKey { get; } = titleKey;
    public Geometry? Icon { get; } = Application.Current?.FindResource(iconKey) as Geometry;
    public Type PageType { get; } = pageType;

    [ObservableProperty] private string _title = "";

    public void Refresh(ILocalizer loc) => Title = loc[TitleKey];
}

/// <summary>
/// One per main window (<see cref="WindowService.ShowMain"/>); disposed with it, which drops its subscriptions on the
/// long-lived services and the current page.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILocalizer _loc;
    private bool _disposed;

    /// <summary>The repository, for the update check and About.</summary>
    public string RepositoryUrl => UpdateService.RepositoryUrl;

    /// <summary>The author's GitHub profile (sidebar).</summary>
    public string ProfileUrl => Filee.Core.AppIdentity.ProfileUrl;

    /// <summary>Buy Me a Coffee (sidebar).</summary>
    public string SupportUrl => Filee.Core.AppIdentity.SupportUrl;

    public MainWindowViewModel(IServiceProvider services, ILocalizer loc, UpdateService updates)
    {
        _services = services;
        _loc = loc;
        Updates = updates;
        updates.PropertyChanged += OnUpdatesChanged;
        RefreshVersionTexts();
        Items =
        [
            new("home", "nav.home", "Icon.Home", typeof(HomePageViewModel), true),
            new("pdf", "nav.pdf_editor", "Icon.File", typeof(PdfEditorPageViewModel), true),
            new("pdftools", "nav.pdf_tools", "Icon.Merge", typeof(PdfToolsPageViewModel), true),
            new("utilities", "nav.utilities", "Icon.Toolbox", typeof(UtilitiesPageViewModel), true),
            new("general", "nav.general", "Icon.Settings", typeof(GeneralPageViewModel)),
            new("triggers", "nav.triggers", "Icon.Keyboard", typeof(TriggersPageViewModel)),
            new("toolbar", "nav.toolbar", "Icon.Panel", typeof(ToolbarPageViewModel)),
            new("presets", "nav.presets", "Icon.Tune", typeof(PresetsPageViewModel)),
            new("watch", "nav.watch", "Icon.Folder", typeof(WatchFoldersPageViewModel)),
            new("theme", "nav.theme", "Icon.Palette", typeof(ThemePageViewModel)),
            new("engines", "nav.engines", "Icon.Engine", typeof(EnginesPageViewModel)),
        ];
        foreach (var item in Items)
            item.Refresh(loc);
        loc.LanguageChanged += OnLanguageChanged;
        WorkspaceItems = Items.Where(i => i.IsWorkspace).ToList();
        SearchEntries = BuildSearch();
        SettingsItems = Items.Where(i => !i.IsWorkspace).ToList();
        _selectedItem = Items[0];
        _selectedWorkspaceItem = Items[0];
        _currentPage = CreatePage(Items[0]);
    }

    public IReadOnlyList<NavItem> Items { get; }

    /// <summary>Pages and tools the search box offers.</summary>
    public IReadOnlyList<SearchEntry> SearchEntries { get; private set; }

    private IReadOnlyList<SearchEntry> BuildSearch()
    {
        Geometry? Icon(string key) => Application.Current?.FindResource(key) as Geometry;
        var entries = Items.Select(i => new SearchEntry(i.Title, _loc["shell.search_page"], i.Icon, i.Key)).ToList();
        (string Title, string Icon, string Page)[] tools =
        [
            ("tools.merge", "Icon.Merge", "pdftools"), ("tools.split", "Icon.Copy", "pdftools"), ("tools.compress", "Icon.Compress", "pdftools"),
            ("tools.security", "Icon.Lock", "pdftools"), ("tools.text", "Icon.Text", "pdftools"), ("tools.ocr", "Icon.Search", "pdftools"),
            ("utils.duplicates", "Icon.Copy", "utilities"), ("utils.largest", "Icon.Archive", "utilities"), ("utils.mismatches", "Icon.Alert", "utilities"),
            ("utils.metadata", "Icon.Fingerprint", "utilities"), ("utils.rename", "Icon.Pencil", "utilities"), ("utils.compare", "Icon.Copy", "utilities"),
            ("home.action.edit_pdf", "Icon.File", "pdf"),
        ];
        entries.AddRange(tools.Select(t => new SearchEntry(_loc[t.Title], Items.First(i => i.Key == t.Page).Title, Icon(t.Icon), t.Page)));
        return entries;
    }

    /// <summary>Opens what a search result points at.</summary>
    public void Go(SearchEntry entry) => Navigate(entry.Page);

    /// <summary>Ctrl+K: the view focuses the search box.</summary>
    public event EventHandler? FocusSearchRequested;

    [RelayCommand]
    private void FocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    // ───────── Light / dark (top bar) ─────────

    private bool ActualDark => Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
    public bool IsDark => ActualDark;
    public bool IsLight => !ActualDark;
    public Color GlowA => ActualDark ? Color.Parse("#4022D3EE") : Color.Parse("#507DD3FC");
    public Color GlowB => ActualDark ? Color.Parse("#408B5CF6") : Color.Parse("#40C4B5FD");

    [RelayCommand] private void UseLight() => SetMode(Filee.Core.Settings.ThemeMode.Light);
    [RelayCommand] private void UseDark() => SetMode(Filee.Core.Settings.ThemeMode.Dark);

    private void SetMode(Filee.Core.Settings.ThemeMode mode)
    {
        var store = _services.GetRequiredService<Filee.Core.Settings.UserDataStore>();
        store.Settings.Theme.Mode = mode;
        store.SaveSettings();
        _services.GetRequiredService<ThemeService>().Apply(store.Settings.Theme, store.Settings.Language);
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(GlowA));
        OnPropertyChanged(nameof(GlowB));
    }
    public IReadOnlyList<NavItem> WorkspaceItems { get; }
    public IReadOnlyList<NavItem> SettingsItems { get; }

    // One list per group; picking in one clears the other. A list clearing itself (null) changes nothing.
    [ObservableProperty] private NavItem? _selectedWorkspaceItem;
    [ObservableProperty] private NavItem? _selectedSettingsItem;

    partial void OnSelectedWorkspaceItemChanged(NavItem? value)
    {
        if (value is not null)
            SelectedItem = value;
    }

    partial void OnSelectedSettingsItemChanged(NavItem? value)
    {
        if (value is not null)
            SelectedItem = value;
    }

    /// <summary>Workspace pages kept while this window is open (<see cref="IWorkspacePage"/>), by type.</summary>
    private readonly Dictionary<Type, ObservableObject> _workspacePages = [];

    /// <summary>Workspace pages use the whole width; settings pages stay at a readable width.</summary>
    public double PageMaxWidth => CurrentPage is IWorkspacePage ? double.PositiveInfinity : 980;

    /// <summary>An open PDF has changes that closing the window would lose.</summary>
    public bool HasUnsavedWork => UnsavedEditors.Any();

    /// <summary>Open PDF documents with unsaved changes.</summary>
    public IEnumerable<PdfEditorPageViewModel> UnsavedEditors => _workspacePages.Values.OfType<PdfEditorPageViewModel>().Where(p => p.IsDirty).ToList();

    /// <summary>Opens a PDF in the workspace.</summary>
    public async Task OpenPdfAsync(string path)
    {
        Navigate("pdf");
        if (CurrentPage is PdfEditorPageViewModel editor)
            await editor.OpenAsync(path);
    }

    /// <summary>Update state for the bottom of the navigation (version, "update available" button).</summary>
    public UpdateService Updates { get; }

    [ObservableProperty] private NavItem _selectedItem;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageScroll), nameof(PageMaxWidth))]
    private ObservableObject _currentPage;

    /// <summary>
    /// Pages scroll as a whole, except pages that fit the window height and scroll their own parts
    /// (<see cref="IFitsWindowHeight"/>, e.g. the presets list and editor).
    /// </summary>
    public ScrollBarVisibility PageScroll => CurrentPage is IFitsWindowHeight ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private string _updateTip = "";

    /// <summary>Downloads and runs the new installer (or opens the download page in a portable copy).</summary>
    [RelayCommand]
    private Task DownloadUpdate() => Updates.UpdateAsync();

    private void OnUpdatesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (UpdateTexts.Affects(e.PropertyName))
            RefreshVersionTexts();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var item in Items)
            item.Refresh(_loc);
        SearchEntries = BuildSearch();
        OnPropertyChanged(nameof(SearchEntries));
        RefreshVersionTexts();
        CurrentPage = CreatePage(SelectedItem); // rebuild so code-generated labels update too
    }

    private void RefreshVersionTexts()
    {
        VersionText = _loc.Format("about.version", UpdateService.CurrentVersion);
        UpdateText = UpdateTexts.Short(Updates, _loc);
        var changes = UpdateTexts.Changes(Updates, _loc);
        UpdateTip = UpdateTexts.Status(Updates, _loc)
                    ?? $"{_loc[$"update.level.{Updates.Level}"]}{Environment.NewLine}{(changes.Length > 0 ? changes + Environment.NewLine : "")}{_loc[UpdateTexts.HowKey]}";
    }

    public void Navigate(string key)
    {
        var item = Items.FirstOrDefault(i => i.Key == key);
        if (item is not null)
            SelectedItem = item;
    }

    // Pages are recreated on every visit so they always reflect the latest settings. A navigation list that loses its
    // selection keeps the open page (Choices.Restore).
    partial void OnSelectedItemChanged(NavItem? oldValue, NavItem newValue)
    {
        if (Choices.Restore(oldValue, newValue, v => SelectedItem = v))
            return;
        SelectedWorkspaceItem = newValue.IsWorkspace ? newValue : null;
        SelectedSettingsItem = newValue.IsWorkspace ? null : newValue;
        CurrentPage = CreatePage(newValue);
    }

    // Old pages may hold event subscriptions on long-lived services; workspace pages live until the window closes.
    partial void OnCurrentPageChanged(ObservableObject? oldValue, ObservableObject newValue)
    {
        if (oldValue is not IWorkspacePage)
            (oldValue as IDisposable)?.Dispose();
    }

    private ObservableObject CreatePage(NavItem item)
    {
        if (_workspacePages.TryGetValue(item.PageType, out var kept))
            return kept;
        var page = (ObservableObject)ActivatorUtilities.CreateInstance(_services, item.PageType);
        if (page is IWorkspacePage)
            _workspacePages[item.PageType] = page;
        return page;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Updates.PropertyChanged -= OnUpdatesChanged;
        _loc.LanguageChanged -= OnLanguageChanged;
        if (CurrentPage is not IWorkspacePage)
            (CurrentPage as IDisposable)?.Dispose();
        foreach (var page in _workspacePages.Values)
            (page as IDisposable)?.Dispose();
        _workspacePages.Clear();
    }
}
