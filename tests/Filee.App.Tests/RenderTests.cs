// Renders real windows headlessly (Skia) to catch XAML / binding errors and to produce screenshots
// in bin/.../screenshots for visual review.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class RenderTests
{
    public static TheoryData<string, string> Pages => new()
    {
        { "home", "en" }, { "general", "en" }, { "triggers", "en" }, { "toolbar", "en" },
        { "presets", "en" }, { "theme", "en" }, { "engines", "en" },
        { "toolbar", "ko" }, { "home", "ko" }, { "presets", "zh-CN" }, { "toolbar", "zh-CN" }, { "home", "zh-CN" },
        { "toolbar", "pl" }, { "presets", "pl" }, { "home", "pl" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Pages))]
    public void Main_window_pages_render(string page, string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate(page);
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();

        Assert.NotNull(window.FindControl<ScrollViewer>("PageScroller"));
        Save(window, $"page-{page}-{language}.png");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Watch_folders_page_renders_a_rule(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var rule = new Filee.Core.Watching.WatchRule { Folder = @"C:\Scans\Inbox", PresetId = "to-pdf", Enabled = false };
        store.Settings.WatchFolders.Add(rule);
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("watch");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            Pump();

            var page = (Filee.App.ViewModels.Pages.WatchFoldersPageViewModel)vm.CurrentPage;
            var item = Assert.Single(page.Rules);
            Assert.Equal("to-pdf", item.Preset?.Value);
            Assert.False(string.IsNullOrWhiteSpace(item.Status));
            Assert.EndsWith(Path.Combine("converted", Path.ChangeExtension(AppHost.Get<Filee.Core.Localization.ILocalizer>()["watch.sample_file"], "pdf")),
                item.PreviewExample); // where a converted file goes and what it is called
            Assert.False(item.ShowOptions); // the optional settings stay folded until used
            Save(window, $"page-watch-{language}.png");
            window.Close();
        }
        finally
        {
            store.Settings.WatchFolders.Remove(rule);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Watch_folder_card_shows_recent_results_and_retries_in_the_folder(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var conversions = AppHost.Get<ConversionService>();
        var folder = Directory.CreateTempSubdirectory("filee-watch-").FullName;
        var broken = Path.Combine(folder, "scan-0412.heic");
        File.WriteAllBytes(broken, [0]);
        var rule = new Filee.Core.Watching.WatchRule { Folder = folder, PresetId = "to-pdf", Enabled = false };
        store.Settings.WatchFolders.Add(rule);
        store.AddHistory(new Filee.Core.History.HistoryEntry
        {
            WatchRuleId = rule.Id,
            FinishedAt = DateTime.Now,
            Sources = [Path.Combine(folder, "a.jpg"), Path.Combine(folder, "b.jpg"), broken],
            Failures = [new() { Source = broken, ErrorKey = "error.conversion_failed", ErrorDetail = "HEIC decoder: unexpected end of file" }],
            Preset = new Filee.Core.Presets.Preset { TargetFormat = "pdf" },
        });
        var retried = new List<(string Rule, IReadOnlyList<string> Files)>();
        var previousRetry = conversions.WatchRetry;
        conversions.WatchRetry = (id, files, _) =>
        {
            retried.Add((id, files));
            return Task.CompletedTask;
        };
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("watch");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 1000 };
            window.Show();
            Pump();

            var item = Assert.Single(((Filee.App.ViewModels.Pages.WatchFoldersPageViewModel)vm.CurrentPage).Rules);
            Assert.True(item.HasResults);
            Assert.Equal("scan-0412.heic", Assert.Single(item.ResultFailures).Name);
            Assert.True(item.CanRetryResults);
            item.ToggleResultFailuresCommand.Execute(null);
            Pump();
            Save(window, $"page-watch-results-{language}.png");

            // Retrying goes through the watch folder (which then moves originals when it does that).
            item.RetryResultsCommand.Execute(null);
            var (ruleId, files) = Assert.Single(retried);
            Assert.Equal(rule.Id, ruleId);
            Assert.Equal([broken], files);
            window.Close();
        }
        finally
        {
            conversions.WatchRetry = previousRetry;
            store.Settings.WatchFolders.Remove(rule);
            store.SaveSettings();
            store.ClearHistory();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Watch_folder_options_rename_and_filter_with_a_preview(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var folder = Directory.CreateTempSubdirectory("filee-watch-").FullName;
        File.WriteAllBytes(Path.Combine(folder, "notes.txt"), [0x41]);
        File.WriteAllBytes(Path.Combine(folder, "IMG_0412.jpg"), [0xFF, 0xD8]);
        var rule = new Filee.Core.Watching.WatchRule
        {
            Folder = folder,
            PresetId = "to-pdf",
            Enabled = false,
            Include = ["*.jpg"],
            FileNamePattern = "{name}_scan",
        };
        store.Settings.WatchFolders.Add(rule);
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("watch");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 1500 };
            window.Show();
            Pump();

            var item = Assert.Single(((Filee.App.ViewModels.Pages.WatchFoldersPageViewModel)vm.CurrentPage).Rules);
            Assert.True(item.ShowOptions); // options in use are shown

            // Typed patterns become chips: an extension turns into *.ext, a broken expression is not added.
            item.AddIncludeCommand.Execute("heic");
            Assert.Equal(["*.jpg", "*.heic"], rule.Include);
            item.AddIncludeCommand.Execute("/IMG_(/");
            Assert.NotNull(item.IncludeError);
            Assert.Equal("/IMG_(/", item.IncludeText);
            item.IncludeText = "";
            Assert.NotNull(item.IncludeMatches); // IMG_0412.jpg matches, notes.txt doesn't

            // The rename window works on copies: a ready-made replacement becomes a rule, the preview follows.
            var tool = item.CreateRenameTool();
            Assert.Equal("{name}_scan", tool.NamePattern); // the base name is edited here too
            tool.NamePattern = "";
            tool.InsertToken("date"); // an empty name starts from the original one
            Assert.Equal("{name}_{date}", tool.NamePattern);
            tool.NamePattern = "{name}_scan";
            tool.Recipes[0].AddCommand.Execute(null); // change IMG_ to Photo_
            Assert.Empty(rule.Renames); // nothing changes before saving
            Assert.Equal(new Filee.App.ViewModels.RenamePreview("IMG_0412.jpg", "Photo_0412_scan.pdf"), Assert.Single(tool.Previews));
            tool.CustomName = "img_0815.heic";
            Assert.Equal("Photo_0815_scan.pdf", tool.CustomResult);

            // Plain text is never "wrong"; only the advanced kind checks its expression.
            tool.AddStepCommand.Execute(null);
            var extra = tool.Steps[1];
            extra.Find = "(";
            Assert.Null(extra.Error);
            extra.Kind = tool.Kinds.First(k => k.Value == Filee.Core.Presets.RenameKind.Regex);
            Assert.NotNull(extra.Error);
            Assert.False(tool.IsValid);
            extra.Kind = tool.Kinds.First(k => k.Value == Filee.Core.Presets.RenameKind.Suffix);
            Assert.True(extra.ShowReplaceOnly);
            extra.Replace = "_v2";
            Assert.True(tool.IsValid);
            Assert.Equal("Photo_0412_scan_v2.pdf", tool.Previews[0].After);
            extra.RemoveCommand.Execute(null);
            var dialog = new Filee.App.Views.RenameToolWindow { DataContext = tool };
            dialog.Show();
            Pump();
            Save(dialog, $"window-rename-{language}.png");
            dialog.Close();

            item.SetNaming(tool.NamePattern, tool.Result); // saved
            Assert.Equal("IMG_", Assert.Single(rule.Renames).Find);
            // A file already in the folder that the patterns let through, renamed by the name rule and the rule.
            Assert.Equal($"IMG_0412.jpg → {Path.Combine("converted", "Photo_0412_scan.pdf")}", item.PreviewExample);
            Pump();
            Save(window, $"page-watch-options-{language}.png");
            window.Close();
        }
        finally
        {
            store.Settings.WatchFolders.Remove(rule);
            store.SaveSettings(); // editing the rule saved it: the next test's application would load it again
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Home_page_lists_failed_files_with_a_retry_button(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var folder = Directory.CreateTempSubdirectory("filee-home-").FullName;
        var scan = Path.Combine(folder, "scan-0412.heic");
        File.WriteAllBytes(scan, [0]);
        var entry = new Filee.Core.History.HistoryEntry
        {
            FinishedAt = DateTime.Now,
            PresetName = "PDF",
            State = Filee.Core.Conversion.JobState.CompletedWithErrors,
            Sources = [Path.Combine(folder, "report.docx"), scan, Path.Combine(folder, "notes.xyz")],
            Failures =
            [
                new() { Source = scan, ErrorKey = "error.conversion_failed", ErrorDetail = "HEIC decoder: unexpected end of file" },
                new() { Source = Path.Combine(folder, "notes.xyz"), ErrorKey = "error.unsupported_source", ErrorDetail = "xyz" },
            ],
            Preset = new Filee.Core.Presets.Preset { TargetFormat = "pdf" },
        };
        store.AddHistory(entry);
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            Pump();

            var item = ((Filee.App.ViewModels.Pages.HomePageViewModel)vm.CurrentPage).History[0];
            Assert.True(item.CanRetry);
            item.ToggleFailuresCommand.Execute(null);
            Pump();
            Save(window, $"page-home-failed-{language}.png");
            window.Close();
        }
        finally
        {
            store.ClearHistory(); // also on disk: the next test's application loads the history again
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void Engines_page_shows_versions_descriptions_and_download_speed()
    {
        TestServices.EnsureInitialized("ko");
        var downloads = AppHost.Get<EngineDownloadService>();
        var package = downloads.Packages[0];
        var before = (package.Status, package.Progress, package.ProgressDetail);
        package.Status = EnginePackageStatus.Downloading;
        package.Progress = 42;
        package.ProgressDetail = "176 MB / 422 MB · 8.4 MB/s · 30초 남음";
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("engines");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 1400 };
            window.Show();
            Pump();

            var engines = ((Filee.App.ViewModels.Pages.EnginesPageViewModel)vm.CurrentPage).Engines;
            Assert.All(engines.Where(e => e.IsAvailable), e => Assert.False(string.IsNullOrWhiteSpace(e.Version)));
            Assert.All(engines, e => Assert.False(string.IsNullOrWhiteSpace(e.Description)));
            Save(window, "page-engines-downloading-ko.png");
            window.Close();
        }
        finally
        {
            (package.Status, package.Progress, package.ProgressDetail) = before;
        }
    }

    [AvaloniaFact]
    public void Dragging_a_preset_shows_where_it_goes_in_the_quick_panel()
    {
        TestServices.EnsureInitialized("ko");
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("toolbar");
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 820 };
        window.Show();
        Pump();
        var panel = window.GetVisualDescendants().OfType<QuickPanel>().Single(d => d.IsEditMode);
        var count = panel.Items!.Count;

        // A chip over the third tile: the insertion bar goes before it.
        panel.SetDropPreview(2, "TIFF 300dpi");
        Pump();
        Assert.Equal(2, panel.SlotAt(panel.TileBounds(2).TopLeft + new Avalonia.Point(2, 20), count + 1));
        Assert.Equal(0, panel.SlotAt(panel.TileBounds(0).TopLeft + new Avalonia.Point(2, 20), count + 1));
        Save(window, "toolbar-drop-preview-ko.png");
        panel.SetDropPreview(-1);
        window.Close();
    }

    /// <summary>The built-in "Rotate" and "Flip" groups on the image panel: a heading each, with their presets as tiles.</summary>
    [AvaloniaTheory]
    [InlineData("ko")]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void The_rotate_and_flip_groups_show_as_sections(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var before = images.PresetIds.ToList();
        try
        {
            images.PresetIds.AddRange(["rotate", "flip"]);
            var vm = AppHost.Get<RadialViewModel>();
            vm.Load([@"C:\photos\a.jpg"], clickMode: true);
            var rotate = vm.Items.Count - 2;
            Assert.Equal(3, vm.Items[rotate].Children!.Count);
            Assert.All(vm.Items[rotate].Children!, item => Assert.True(item.IsEnabled, item.Label));
            var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
            Assert.Equal(loc["preset.rotate_left.short"], vm.Items[rotate].Children![0].Label);

            var window = new QuickPanelWindow { DataContext = vm };
            window.Show();
            Pump();
            var panel = window.Panel;
            var tile = panel.TileBounds(rotate, 1);
            Assert.True(tile.Width > 0);
            Assert.Equal(new PanelHit(PanelHitKind.SubSegment, rotate, 1), panel.HitTest(tile.Center));
            panel.SetExternalHit(panel.HitTest(tile.Center));
            vm.ShowHint(rotate, panel.ActiveSubIndex, centerHot: false);
            Pump();
            Save(window, $"panel-rotate-{language}.png");
            window.Close();
        }
        finally
        {
            images.PresetIds.Clear();
            images.PresetIds.AddRange(before);
        }
    }

    /// <summary>A group of three formats: its presets are tiles under its heading; the group converts nothing itself.</summary>
    [AvaloniaTheory]
    [InlineData("ko")]
    [InlineData("en")]
    public void A_group_lists_its_presets_as_tiles(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var before = images.PresetIds.ToList();
        var group = new Filee.Core.Presets.Preset
        {
            Id = "render-group",
            Name = language == "ko" ? "형식" : "Formats",
            Kind = Filee.Core.Presets.PresetKind.Group,
            SubPresetIds = ["to-png", "to-jpg", "to-webp"],
        };
        try
        {
            store.Presets.Add(group);
            images.PresetIds.Add(group.Id);
            var vm = AppHost.Get<RadialViewModel>();
            vm.Load([@"C:\photos\a.jpg"], clickMode: true);
            var index = vm.Items.Count - 1;
            Assert.Null(vm.PresetAt(index, -1));
            Assert.Equal("to-jpg", vm.PresetAt(index, 1)!.Id);

            var window = new QuickPanelWindow { DataContext = vm };
            window.Show();
            Pump();
            var panel = window.Panel;
            var jpg = panel.TileBounds(index, 1).Center;
            var hit = panel.HitTest(jpg);
            Assert.Equal(new PanelHit(PanelHitKind.SubSegment, index, 1), hit);
            Assert.True(panel.Converts(hit));
            Assert.Equal(0, panel.TileBounds(index).Width); // no tile for the group itself
            panel.SetExternalHit(hit);
            vm.ShowHint(index, panel.ActiveSubIndex, centerHot: false);
            Assert.Contains("JPG", vm.CenterSubtitle);
            Pump();
            Save(window, $"panel-group-{language}.png");
            window.Close();
        }
        finally
        {
            images.PresetIds.Clear();
            images.PresetIds.AddRange(before);
            store.Presets.Remove(group);
        }
    }

    [AvaloniaFact]
    public void Quick_panel_renders_for_images_and_runs_from_the_keyboard()
    {
        TestServices.EnsureInitialized("ko");
        var files = new[] { @"C:\photos\a.jpg", @"C:\photos\b.jpg", @"C:\photos\c.jpg" };
        var vm = AppHost.Get<RadialViewModel>();
        vm.Load(files, clickMode: true);
        var window = new QuickPanelWindow { DataContext = vm };
        window.Show();
        window.Panel.SetExternalHighlight(1);
        Pump();

        Assert.Equal(10, vm.Items.Count); // with "Trace to SVG" and "Line art to SVG"
        Assert.Equal("JPG 3개", vm.CenterTitle);
        Save(window, "panel-images-ko.png");

        // The close button cancels; arrows and Enter pick an action.
        var panel = window.Panel;
        Assert.Equal(PanelHitKind.Center, panel.HitTest(new Avalonia.Point(QuickPanel.PanelWidth - QuickPanel.Pad - 14, QuickPanel.Pad + 12)).Kind);
        var invoked = -1;
        panel.SegmentInvoked += (_, i) => invoked = i;
        panel.SetExternalHighlight(-1);
        Assert.True(panel.HandleKey(Avalonia.Input.Key.Right));
        Assert.True(panel.HandleKey(Avalonia.Input.Key.Right));
        Assert.True(panel.HandleKey(Avalonia.Input.Key.Enter));
        Assert.Equal(1, invoked);
        window.Close();
    }

    [AvaloniaFact]
    public void LibreOffice_options_window_renders()
    {
        TestServices.EnsureInitialized("ko");
        using var vm = new Filee.App.ViewModels.Pages.EnginesPageViewModel(AppHost.Get<Filee.Core.Settings.UserDataStore>(),
            AppHost.Get<Filee.Core.Conversion.ConverterCatalog>(), AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<EngineDownloadService>());
        var window = new Filee.App.Views.LibreOfficeOptionsWindow { DataContext = vm };
        window.Show();
        Pump();
        Save(window, "window-libreoffice-options-ko.png");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void First_run_engine_setup_renders(string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new EngineSetupViewModel(AppHost.Get<EngineDownloadService>(), AppHost.Get<Filee.Core.Localization.ILocalizer>());
        var window = new EngineSetupWindow { DataContext = vm };
        window.Show();
        Pump();

        // Everything not installed is offered, nothing is ticked: installing needs a choice first.
        Assert.Contains(vm.Packages, p => !p.IsInstalled && p.CanInstall);
        Assert.DoesNotContain(vm.Packages, p => p.Selected);
        Assert.False(vm.InstallCommand.CanExecute(null));
        vm.Packages.First(p => p.CanInstall && !p.IsInstalled).Selected = true;
        Assert.True(vm.InstallCommand.CanExecute(null));
        vm.Packages.First(p => p.Selected).Selected = false;
        Save(window, $"engine-setup-{language}.png");
        window.Close();
    }

    [AvaloniaFact]
    public void Donut_toolbar_waits_for_files_in_drag_mode()
    {
        TestServices.EnsureInitialized("en");
        var vm = AppHost.Get<RadialViewModel>();
        vm.ResetForDrag();
        var window = new QuickPanelWindow { DataContext = vm };
        window.Show();
        Pump();

        Assert.Empty(vm.Items);
        // Nothing shows until files are dragged in (a Ctrl + drag without files must not open anything).
        Assert.Equal(0, vm.DonutOpacity);
        Save(window, "panel-drag-waiting.png");
        vm.Load([@"C:\photos\a.jpg"], clickMode: false);
        Assert.Equal(1, vm.DonutOpacity);
        window.Close();
    }

    [AvaloniaFact]
    public void Dark_theme_with_custom_accent_renders()
    {
        TestServices.EnsureInitialized("en");
        var theme = new ThemeSettings { Mode = ThemeMode.Dark, Accent = "#1D9E75", CornerRadius = 22 };
        AppHost.Get<ThemeService>().Apply(theme, "en");
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("toolbar");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();

        Save(window, "page-toolbar-dark.png");
        window.Close();
        AppHost.Get<ThemeService>().Apply(new ThemeSettings(), "en");
    }

    [AvaloniaFact]
    public void Preset_popup_renders()
    {
        TestServices.EnsureInitialized("ko");
        var preset = AppHost.Get<UserDataStore>().FindPreset("jpg-70")!;
        var window = new PresetEditorWindow { DataContext = new PresetEditorViewModel(preset, AppHost.Get<Filee.Core.Localization.ILocalizer>()) };
        window.Show();
        Pump();

        Save(window, "preset-popup-ko.png");
        window.Close();
    }

    [AvaloniaFact]
    public void Preset_editor_shows_and_saves_rotation_and_flip()
    {
        TestServices.EnsureInitialized("ko");
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "rotate-left");
        var vm = new PresetEditorViewModel(preset, AppHost.Get<Filee.Core.Localization.ILocalizer>());
        Assert.Equal(Filee.Core.Presets.ImageRotation.Left90, vm.Rotation.Value); // not a hidden option any more
        Assert.Equal(Filee.Core.Presets.ImageFlip.None, vm.Flip.Value);
        var window = new PresetEditorWindow { DataContext = vm, Height = 900 };
        window.Show();
        Pump();
        Save(window, "preset-rotate-ko.png");
        window.Close();

        // Rotate and convert in one: turn left, mirror, and make it a JPG.
        vm.Flip = vm.Flips.Single(f => f.Value == Filee.Core.Presets.ImageFlip.Horizontal);
        vm.Target = vm.Targets.Single(t => t.Value == "jpg");
        Assert.True(vm.Apply());

        Assert.Equal(Filee.Core.Presets.ImageRotation.Left90, preset.Image.Rotation);
        Assert.Equal(Filee.Core.Presets.ImageFlip.Horizontal, preset.Image.Flip);
        Assert.Equal("jpg", preset.TargetFormat);
    }

    [AvaloniaFact]
    public void Preset_editor_keeps_rotation_visible_for_pdf_and_drops_it_for_targets_that_cannot_turn()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();

        // Photos → PDF are turned on the pages: the PDF card shows the rotation, and it is kept.
        var toPdf = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "rotate-left");
        var vm = new PresetEditorViewModel(toPdf, loc);
        vm.Target = vm.Targets.Single(t => t.Value == "pdf");
        Assert.False(vm.ShowImage);
        Assert.True(vm.ShowTurnForPdf);
        Assert.True(vm.Apply());
        Assert.Equal(Filee.Core.Presets.ImageRotation.Left90, toPdf.Image.Rotation);

        // A video can't be turned: a rotation left from the image target would fail every file with nothing on screen.
        var toVideo = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "rotate-left");
        vm = new PresetEditorViewModel(toVideo, loc);
        vm.Flip = vm.Flips.Single(f => f.Value == Filee.Core.Presets.ImageFlip.Vertical);
        vm.Target = vm.Targets.Single(t => t.Value == "mp4");
        Assert.False(vm.ShowTurnForPdf);
        Assert.True(vm.Apply());
        Assert.Equal(Filee.Core.Presets.ImageRotation.None, toVideo.Image.Rotation);
        Assert.Equal(Filee.Core.Presets.ImageFlip.None, toVideo.Image.Flip);
    }

    [AvaloniaFact]
    public void Preset_editor_saves_every_frame_only_for_png_and_jpg()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();

        var preset = new Filee.Core.Presets.Preset { TargetFormat = "jpg" };
        var vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.ShowSaveEveryFrame);
        vm.SaveEveryFrame = true;
        vm.FrameInterval = 5;
        Assert.Equal("One minute of 30 fps video gives 360 pictures, at most 100,000 per video.", vm.FrameEstimate);
        vm.FrameInterval = 5000; // out of range: clamped
        Assert.True(vm.Apply());
        Assert.True(preset.Media.SaveEveryFrame);
        Assert.Equal(1000, preset.Media.FrameInterval);

        // Ticked, then another target: nothing on screen says so any more, so it isn't saved.
        vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.SaveEveryFrame);
        vm.Target = vm.Targets.Single(t => t.Value == "webp");
        Assert.False(vm.ShowSaveEveryFrame);
        Assert.True(vm.Apply());
        Assert.False(preset.Media.SaveEveryFrame);
        Assert.Equal(1, preset.Media.FrameInterval);
    }

    [AvaloniaFact]
    public void Preset_editor_shows_and_saves_background_removal()
    {
        TestServices.EnsureInitialized("ko");
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "remove-background");
        var vm = new PresetEditorViewModel(preset, AppHost.Get<Filee.Core.Localization.ILocalizer>());
        Assert.True(vm.RemoveBackground);
        Assert.Equal(25, vm.BackgroundTolerance);
        var window = new PresetEditorWindow { DataContext = vm, Height = 900 };
        window.Show();
        Pump();
        Save(window, "preset-remove-background-ko.png");
        window.Close();

        vm.BackgroundTolerance = 140; // out of range: clamped
        vm.KeepEnclosedBackground = true;
        Assert.True(vm.Apply());

        Assert.True(preset.Image.RemoveBackground);
        Assert.Equal(100, preset.Image.BackgroundTolerance);
        Assert.True(preset.Image.KeepEnclosedBackground);
    }

    [AvaloniaFact]
    public void Preset_editor_shows_and_saves_pdf_compression()
    {
        TestServices.EnsureInitialized("ko");
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "compress-pdf");
        var vm = new PresetEditorViewModel(preset, AppHost.Get<Filee.Core.Localization.ILocalizer>());
        Assert.True(vm.ShowPdfCompression);
        Assert.True(vm.ShowPdfCompressionOptions);
        Assert.Equal(Filee.Core.Presets.PdfCompression.Ebook, vm.CompressionLevel.Value);
        Assert.Equal(["None", "Screen", "Ebook", "Print"], vm.CompressionLevels.Select(c => c.Value.ToString()));
        var window = new PresetEditorWindow { DataContext = vm, Height = 900 };
        window.Show();
        Pump();
        Save(window, "preset-compress-pdf-ko.png");
        window.Close();

        vm.CompressionLevel = vm.CompressionLevels.Single(c => c.Value == Filee.Core.Presets.PdfCompression.Screen);
        vm.CompressWithGhostscript = true;
        vm.KeepMetadata = false;
        Assert.True(vm.Apply());
        Assert.Equal(Filee.Core.Presets.PdfCompression.Screen, preset.Pdf.Compression);
        Assert.True(preset.Pdf.CompressWithGhostscript);
        Assert.False(preset.Image.KeepMetadata);

        // Off: Ghostscript and metadata have nothing to do. Other targets don't compress PDFs at all.
        vm.CompressionLevel = vm.CompressionLevels[0];
        Assert.False(vm.ShowPdfCompressionOptions);
        vm.Target = vm.Targets.Single(t => t.Value == "png");
        Assert.False(vm.ShowPdfCompression);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Sidebar_shows_the_version_and_a_new_release(string language)
    {
        TestServices.EnsureInitialized(language);
        var updates = AppHost.Get<UpdateService>();
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), updates);
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        var button = window.FindControl<Button>("UpdateButton")!;
        var version = window.FindControl<TextBlock>("VersionLabel")!;
        Assert.False(button.IsVisible);
        Assert.Contains(UpdateService.CurrentVersion, version.Text);

        try
        {
            updates.Notes = [ReleaseNotes.Parse("9.9.9", """
                <!-- update
                level: recommended
                ko: 엑셀에서 정한 페이지 나누기가 PDF에 반영돼요
                ko: 한글 문서의 보기 상자와 수식이 PDF에서 제대로 나와요
                en: Excel page breaks carry over to PDF
                en: Boxes and equations of 한글 documents come out right in PDFs
                zh: Excel 中设置的分页符会保留到 PDF 中
                zh: 韩文 Hangul 文档中的框和公式在 PDF 中正确显示
                -->
                """)];
            updates.LatestVersion = "9.9.9";
            Pump();
            Assert.True(button.IsVisible);
            Assert.Contains("9.9.9", vm.UpdateText);
            Save(window, $"sidebar-update-{language}.png");

            var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
            var notice = new UpdateNoticeWindow(updates, loc, loc.Format("update.notice_title", "9.9.9"));
            notice.Show();
            Pump();
            Save(notice, $"update-notice-{language}.png");

            // A required update: red pill on the card and in the sidebar; an optional one: plain button, grey pill.
            updates.Notes = [ReleaseNotes.Parse("9.9.9", """
                <!-- update
                level: required
                ko: 변환 결과가 잘못 나오던 문제를 고쳤어요
                en: Fixes wrong conversion results
                zh: 修复了转换结果错误的问题
                -->
                """)];
            Pump();
            Save(notice, $"update-notice-required-{language}.png");
            Save(window, $"sidebar-update-required-{language}.png");
            updates.Notes = [ReleaseNotes.Parse("9.9.9", "<!-- update\nlevel: optional\nko: 새 기능\nen: A new feature\n-->")];
            Pump();
            Save(window, $"sidebar-update-optional-{language}.png");

            // While the installer downloads, the card, the sidebar button and the tray show the progress.
            updates.Step = UpdateStep.Downloading;
            updates.DownloadPercent = 42;
            Pump();
            Assert.Contains("42", vm.UpdateText);
            Save(notice, $"update-notice-downloading-{language}.png");
            Save(window, $"sidebar-update-downloading-{language}.png");
            updates.Step = UpdateStep.Idle;
            notice.Close();

            var feedback = new FeedbackNoticeWindow();
            feedback.Show();
            Pump();
            Save(feedback, $"feedback-notice-{language}.png");
            feedback.Close();
        }
        finally
        {
            updates.Step = UpdateStep.Idle;
            updates.LatestVersion = null;
            updates.Notes = [];
            window.Close();
        }
    }

    /// <summary>
    /// The extension chips of a profile that shares extensions with "Images" (warning chips), has one Filee doesn't
    /// know (muted chip) and shows suggestions for what is being typed (dark: a refused duplicate instead).
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", false)]
    [InlineData("zh-CN", false)]
    [InlineData("en", true)]
    [InlineData("ko", true)]
    public void Toolbar_extension_editor_renders(string language, bool dark)
    {
        var photos = new ToolbarProfile
        {
            Id = "render-photos",
            Name = language == "ko" ? "사진" : "Photos",
            Extensions = ["jpg", "png", "heic", "dng", "webp", "xyz"],
            PresetIds = ["to-jpg", "to-webp", "half-size", "to-pdf"],
        };
        WithProfiles(language, dark, 1240, profiles => profiles.Insert(profiles.FindIndex(p => p.IsFallback), photos), (window, vm) =>
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile == photos);
            Pump();
            var editor = window.GetVisualDescendants().OfType<TagEditor>().Single();
            editor.FocusInput();
            Pump();
            if (dark)
            {
                window.KeyTextInput("PNG");
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                Assert.NotNull(vm.ExtensionTags!.InputError);
            }
            else
            {
                window.KeyTextInput("we");
                Pump();
                Assert.True(editor.IsSuggestionListOpen);
            }
            Pump();

            Assert.Equal(6, vm.ExtensionTags!.Tags.Count);
            Assert.Equal(2, vm.ExtensionTags.Notes.Count); // shared with "Images" + unknown
            Save(window, $"toolbar-extensions-{language}{(dark ? "-dark" : "")}.png");
        });
    }

    /// <summary>A second mixed-selection donut limited to images and PDF, two groups expanded.</summary>
    [AvaloniaTheory]
    [InlineData("en", false, "render-scans")]
    [InlineData("ko", false, "render-scans")]
    [InlineData("zh-CN", false, "render-scans")]
    [InlineData("ko", true, "render-scans")]
    [InlineData("en", false, "mixed")]
    [InlineData("en", true, "mixed")]
    public void Toolbar_mixed_selection_editor_renders(string language, bool dark, string profileId)
    {
        var scans = new ToolbarProfile
        {
            Id = "render-scans",
            Name = language == "ko" ? "스캔 모음" : "Scans",
            IsFallback = true,
            Extensions = ["jpg", "png", "tiff", "pdf"],
            PresetIds = ["to-pdf", "merge-pdf", "zip-all"],
        };
        WithProfiles(language, dark, 1600, profiles => profiles.Add(scans), (window, vm) =>
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == profileId);
            Pump();
            var mixed = vm.MixedExtensions!;
            if (profileId != "mixed")
                mixed.Groups[0].IsExpanded = true; // Images: partly checked, many chips
            mixed.Groups[1].IsExpanded = true; // PDF: all checked (Scans)
            Pump();

            Assert.True(vm.IsMixed);
            Assert.Null(vm.ExtensionTags);
            Assert.Equal(profileId == "mixed" ? false : null, mixed.Groups[0].State);
            Assert.True(vm.CanDelete); // two mixed profiles: either may go
            Save(window, $"toolbar-mixed-{profileId.Replace("render-", "")}-{language}{(dark ? "-dark" : "")}.png");
        });
    }

    /// <summary>The preset popup with the video/audio and archive sections, and the target list.</summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", false)]
    [InlineData("zh-CN", false)]
    [InlineData("ko", true)]
    public void Preset_editor_shows_media_and_archive_options(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        try
        {
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var suffix = $"{language}{(dark ? "-dark" : "")}";

            var media = new PresetEditorViewModel(store.FindPreset("mp4-720")!, loc);
            Assert.True(media.ShowMedia && media.ShowMaxHeight && media.ShowRemoveAudio);
            Assert.False(media.ShowPdf || media.ShowImage || media.ShowArchive);
            Assert.Equal(720, media.MaxHeight.Value);
            var window = new PresetEditorWindow { DataContext = media, Height = 900 };
            window.Show();
            Pump();
            Save(window, $"preset-media-{suffix}.png");

            // The target list: categories next to the names. (Headless popups live in the window's overlay layer,
            // so the open list is part of the window's frame.)
            var box = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "TargetBox");
            box.IsDropDownOpen = true;
            Pump();
            Assert.True(box.GetVisualDescendants().OfType<Popup>().Single().IsOpen);
            Save(window, $"preset-targets-{suffix}.png");
            box.IsDropDownOpen = false;
            window.Close();

            // HEVC preset: the codec choice next to the other video options.
            var hevc = new PresetEditorViewModel(store.FindPreset("mp4-hevc")!, loc);
            Assert.True(hevc.ShowVideoCodec && !hevc.ShowAudioCodec);
            Assert.Equal(Filee.Core.Presets.VideoCodec.Hevc, hevc.VideoCodecChoice.Value);
            window = new PresetEditorWindow { DataContext = hevc, Height = 900 };
            window.Show();
            Pump();
            Save(window, $"preset-codec-{suffix}.png");
            window.Close();

            var archive = new PresetEditorViewModel(store.FindPreset("zip-all")!, loc);
            Assert.True(archive.ShowArchive && archive.ShowArchiveOptions && archive.CombineIntoOne);
            Assert.False(archive.ShowMedia || archive.ShowPdf);
            window = new PresetEditorWindow { DataContext = archive, Height = 760 };
            window.Show();
            Pump();
            Save(window, $"preset-archive-{suffix}.png");
            window.Close();

            // Audio targets have no picture options; lossless ones only channels, WAV also sample rate and bit depth (#37);
            // AMR is fixed mono 8 kHz; "Extract" only explains.
            archive.Target = archive.Targets.Single(t => t.Value == "mp3");
            Assert.True(archive.ShowMedia && archive.ShowAudioBitrate && archive.ShowAudioChannels && !archive.ShowMaxHeight && !archive.ShowRemoveAudio);
            Assert.False(archive.ShowWav);
            archive.Target = archive.Targets.Single(t => t.Value == "flac");
            Assert.True(archive.ShowMedia && archive.ShowAudioChannels && !archive.ShowAudioBitrate && !archive.ShowWav);
            archive.Target = archive.Targets.Single(t => t.Value == "wav");
            Assert.True(archive.ShowMedia && archive.ShowAudioChannels && archive.ShowWav);
            window = new PresetEditorWindow { DataContext = archive, Height = 760 };
            window.Show();
            Pump();
            Save(window, $"preset-wav-{suffix}.png");
            window.Close();
            archive.Target = archive.Targets.Single(t => t.Value == "amr");
            Assert.False(archive.ShowAudioChannels || archive.ShowWav);
            archive.Target = archive.Targets.Single(t => t.Value == Filee.Core.Formats.FormatRegistry.Folder);
            Assert.True(archive.ShowArchive && archive.ShowExtractNote && !archive.ShowArchiveOptions);
        }
        finally
        {
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
    }

    /// <summary>The preset popup of "Trace to SVG": mode, colours and detail; colours only in colour mode.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void Preset_editor_shows_tracing_options(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var vm = new PresetEditorViewModel(store.FindPreset("trace-svg")!.Clone(), AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowTrace && vm.ShowTraceColors);
        Assert.False(vm.ShowImage || vm.ShowMedia || vm.ShowArchive);

        var window = new PresetEditorWindow { DataContext = vm, Height = 760 };
        window.Show();
        Pump();
        Assert.True(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TraceCard").IsVisible);
        Save(window, $"preset-trace-{language}.png");
        window.Close();

        vm.TraceMode = vm.TraceModes.Single(m => m.Value == Filee.Core.Presets.TracingMode.BlackAndWhite);
        Assert.False(vm.ShowTraceColors);
        vm.Target = vm.Targets.Single(t => t.Value == "dxf");
        Assert.True(vm.ShowTrace);
        vm.Target = vm.Targets.Single(t => t.Value == "png");
        Assert.False(vm.ShowTrace);
    }

    [AvaloniaFact]
    public void Preset_editor_saves_tracing_options()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "svg", Name = "Logo" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.Equal(["Colour", "Black and white (line art)"], vm.TraceModes.Select(m => m.Label));

        vm.TraceMode = vm.TraceModes.Single(m => m.Value == Filee.Core.Presets.TracingMode.BlackAndWhite);
        vm.TraceColors = 200;
        vm.TraceDetail = 80;
        Assert.True(vm.Apply());

        Assert.Equal((Filee.Core.Presets.TracingMode.BlackAndWhite, 64, 80), (preset.Trace.Mode, preset.Trace.Colors, preset.Trace.Detail));
    }

    [AvaloniaFact]
    public void Preset_editor_saves_media_and_archive_options()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "mp4", Name = "Clip" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.Equal("*", vm.Targets[0].Value); // "Same as source" stays first
        Assert.Equal([0, 32, 48, 64, 96, 128, 192, 256, 320], vm.AudioBitrates.Select(b => b.Value)); // 32-64 for speech (#37)
        Assert.All(vm.Targets.Skip(1), t => Assert.True(Filee.Core.Formats.FormatRegistry.Get(t.Value).Writable));
        Assert.Equal("Video", vm.Target.Detail);

        vm.MediaQuality = vm.MediaQualities.Single(q => q.Value == Filee.Core.Presets.MediaQuality.Small);
        vm.MaxHeight = vm.MaxHeights.Single(h => h.Value == 1080);
        vm.AudioBitrate = vm.AudioBitrates.Single(b => b.Value == 192);
        vm.RemoveAudio = true;
        vm.AudioChannels = vm.AudioChannelChoices.Single(c => c.Value == 1);
        vm.SampleRate = vm.SampleRates.Single(r => r.Value == 16000);
        vm.WavFormat = vm.WavFormats.Single(f => f.Value == Filee.Core.Presets.WavSampleFormat.Pcm24);
        Assert.Equal("16 kHz", vm.SampleRate.Label);
        vm.ArchiveLevel = vm.ArchiveLevels.Single(l => l.Value == Filee.Core.Presets.ArchiveLevel.Maximum);
        vm.CombineIntoOne = true;
        Assert.True(vm.Apply());

        Assert.Equal(Filee.Core.Presets.MediaQuality.Small, preset.Media.Quality);
        Assert.Equal(1080, preset.Media.MaxHeight);
        Assert.Equal(192, preset.Media.AudioBitrateKbps);
        Assert.True(preset.Media.RemoveAudio);
        Assert.Equal((1, 16000, Filee.Core.Presets.WavSampleFormat.Pcm24),
            (preset.Media.AudioChannels, preset.Media.AudioSampleRate, preset.Media.WavFormat));
        Assert.Equal(Filee.Core.Presets.ArchiveLevel.Maximum, preset.Archive.Level);
        Assert.True(preset.Archive.CombineIntoOne);

        // A value set outside the editor is offered instead of being lost.
        preset.Media.MaxHeight = 600;
        Assert.Equal("600p", new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>()).MaxHeight.Label);
    }

    [AvaloniaFact]
    public void Preset_editor_offers_hardware_encoding_for_h264_and_hevc()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "mp4", Name = "Fast" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowHardwareEncoding);
        Assert.False(vm.HardwareEncoding); // off unless asked for
        vm.HardwareEncoding = true;

        // AV1 and VP9 have no hardware path; HEVC does.
        vm.VideoCodecChoice = vm.VideoCodecs.Single(c => c.Value == Filee.Core.Presets.VideoCodec.Av1);
        Assert.False(vm.ShowHardwareEncoding);
        vm.VideoCodecChoice = vm.VideoCodecs.Single(c => c.Value == Filee.Core.Presets.VideoCodec.Hevc);
        Assert.True(vm.ShowHardwareEncoding);
        vm.Target = vm.Targets.Single(t => t.Value == "webm");
        Assert.False(vm.ShowHardwareEncoding);
        vm.Target = vm.Targets.Single(t => t.Value == "avi");
        Assert.False(vm.ShowHardwareEncoding);
        vm.Target = vm.Targets.Single(t => t.Value == "mp3");
        Assert.False(vm.ShowHardwareEncoding);
        vm.Target = vm.Targets.Single(t => t.Value == "ts");
        Assert.True(vm.ShowHardwareEncoding);
        vm.Target = vm.Targets.Single(t => t.Value == "mov");
        Assert.True(vm.Apply());
        Assert.True(preset.Media.HardwareEncoding);
        Assert.Equal(Filee.Core.Presets.VideoCodec.Hevc, preset.Media.VideoCodec);

        Assert.True(new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>()).HardwareEncoding);
    }

    [AvaloniaFact]
    public void Preset_editor_offers_the_codecs_each_target_can_hold()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "mp4", Name = "Codec" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.True(vm.ShowVideoCodec && !vm.ShowAudioCodec);
        Assert.Equal([Filee.Core.Presets.VideoCodec.Default, Filee.Core.Presets.VideoCodec.Hevc, Filee.Core.Presets.VideoCodec.Av1],
            vm.VideoCodecs.Select(c => c.Value));
        Assert.StartsWith("H.264", vm.VideoCodecChoice.Label);
        vm.VideoCodecChoice = vm.VideoCodecs.Single(c => c.Value == Filee.Core.Presets.VideoCodec.Hevc);

        // WebM has no HEVC: its list is VP9 / AV1 and the choice shows as VP9; MKV takes HEVC again.
        vm.Target = vm.Targets.Single(t => t.Value == "webm");
        Assert.Equal(["VP9", "AV1 (smallest, slow)"], vm.VideoCodecs.Select(c => c.Label));
        Assert.Equal(Filee.Core.Presets.VideoCodec.Default, vm.VideoCodecChoice.Value);
        vm.Target = vm.Targets.Single(t => t.Value == "mkv");
        Assert.Equal(Filee.Core.Presets.VideoCodec.Hevc, vm.VideoCodecChoice.Value);
        vm.Target = vm.Targets.Single(t => t.Value == "avi");
        Assert.False(vm.ShowVideoCodec);

        // M4A: AAC or ALAC; ALAC is lossless, so quality and bitrate go away.
        vm.Target = vm.Targets.Single(t => t.Value == "m4a");
        Assert.True(vm.ShowAudioCodec && vm.ShowAudioBitrate && vm.ShowMediaQuality && !vm.ShowVideoCodec);
        vm.AudioCodecChoice = vm.AudioCodecs.Single(c => c.Value == Filee.Core.Presets.AudioCodec.Alac);
        Assert.False(vm.ShowAudioBitrate || vm.ShowMediaQuality);
        Assert.True(vm.ShowMedia && vm.ShowAudioChannels);
        Assert.True(vm.Apply());
        Assert.Equal((Filee.Core.Presets.VideoCodec.Default, Filee.Core.Presets.AudioCodec.Alac), (preset.Media.VideoCodec, preset.Media.AudioCodec));

        // New lossless targets and W64 (WAV options); subtitles have no media or PDF options.
        vm.Target = vm.Targets.Single(t => t.Value == "wv");
        Assert.False(vm.ShowAudioBitrate || vm.ShowAudioCodec);
        vm.Target = vm.Targets.Single(t => t.Value == "w64");
        Assert.True(vm.ShowWav);
        vm.Target = vm.Targets.Single(t => t.Value == "smi");
        Assert.False(vm.ShowMedia || vm.ShowPdf || vm.ShowImage);
        Assert.Equal("Subtitle", vm.Target.Detail);
    }

    /// <summary>Shows the toolbar page (tall, so the profile card is visible) with temporarily changed profiles.</summary>
    private static void WithProfiles(string language, bool dark, double height, Action<List<ToolbarProfile>> change, Action<Window, ToolbarPageViewModel> act)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var theme = AppHost.Get<ThemeService>();
        var snapshot = store.Profiles.Select(p => p.Clone()).ToList();
        Window? window = null;
        try
        {
            change(store.Profiles);
            if (dark)
                theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("toolbar");
            window = new MainWindow { DataContext = vm, Width = 1080, Height = height };
            window.Show();
            Pump();
            act(window, (ToolbarPageViewModel)vm.CurrentPage);
        }
        finally
        {
            window?.Close();
            if (dark)
                theme.Apply(new ThemeSettings(), language);
            store.Profiles.Clear();
            store.Profiles.AddRange(snapshot);
            store.SaveLibrary();
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Excluded_apps_render_as_tags(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.ExcludedProcesses.ToList();
        try
        {
            store.Settings.ExcludedProcesses.Clear();
            store.Settings.ExcludedProcesses.AddRange(["Photoshop", "code"]);
            var vm = new TriggersPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>());
            var window = new Window { Content = new Filee.App.Views.Pages.TriggersPage { DataContext = vm }, Width = 820, Height = 1500 };
            window.Show();
            Pump();
            Assert.Equal(["Photoshop", "code"], vm.ExcludedApps.Tags.Select(t => t.Value));
            Save(window, $"triggers-excluded-{language}.png");
            window.Close();
        }
        finally
        {
            store.Settings.ExcludedProcesses.Clear();
            store.Settings.ExcludedProcesses.AddRange(before);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void Close_dialog_renders(string language)
    {
        TestServices.EnsureInitialized(language);
        var dialog = new CloseAskWindow();
        dialog.Show();
        Pump();
        Save(dialog, $"window-close-{language}.png");
        dialog.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void Format_request_notice_renders(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        var notice = new FormatRequestNoticeWindow([".ncm"], loc.Format("format_request.notice_title", ".ncm"));
        notice.Show();
        Pump();
        Save(notice, $"window-format-request-{language}.png");
        notice.Close();
    }

    private static void Pump()
    {
        // Let layout, bindings and a few animation frames run.
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Save(TopLevel window, string name)
    {
        // Disposed at the end, so the frame stays alive while Skia encodes it: a collection in between (the idle memory
        // trim runs one on another thread) would otherwise finalize its image under the encoder.
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, name));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
