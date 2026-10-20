using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Views;

public partial class MainWindow : Window
{
    private bool _asking;
    private bool _releasePosted;
    private bool _releasing;

    public MainWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        // Picking a search result opens its page and clears the box.
        Search.SelectionChanged += (_, _) =>
        {
            if (Search.SelectedItem is SearchEntry entry && DataContext is MainWindowViewModel vm)
            {
                vm.Go(entry);
                Search.Text = "";
            }
        };
        Search.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && DataContext is MainWindowViewModel vm
                && vm.SearchEntries.FirstOrDefault(s => s.Title.Contains(Search.Text ?? "", StringComparison.CurrentCultureIgnoreCase)) is { } first
                && !string.IsNullOrWhiteSpace(Search.Text))
            {
                vm.Go(first);
                Search.Text = "";
                e.Handled = true;
            }
        };
    }

    private async void OnOpenGitHub(object? sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri(Filee.Core.AppIdentity.ProfileUrl));

    /// <summary>Shows the license files that ship next to Foldex.exe (MIT, with the notice of the code it is built on).</summary>
    private void OnLicenses(object? sender, RoutedEventArgs e)
    {
        var license = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        if (File.Exists(license))
            AppHost.Get<Filee.Core.Platform.IPlatformServices>().RevealInFileManager(license);
    }

    private async void OnSponsor(object? sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri(Filee.Core.AppIdentity.SupportUrl));

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
            vm.FocusSearchRequested += (_, _) => Search.Focus();
    }

    /// <summary>
    /// Closing the window keeps Filee running in the tray, quits, or asks which (<see cref="AppSettings.CloseAction"/>).
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.CloseReason is not WindowCloseReason.WindowClosing || _releasing)
            return;
        e.Cancel = true;
        // Hiding to the tray closes the window too (and with it the PDF workspace): settle unsaved edits first.
        if (DataContext is MainWindowViewModel { HasUnsavedWork: true } && !_unsavedResolved)
        {
            _ = ResolveUnsavedAsync(e.IsProgrammatic);
            return;
        }
        _unsavedResolved = false;
        // Close() from code goes to the tray, as before; the setting is for the title bar's close button and Alt+F4.
        if (e.IsProgrammatic)
            HideToTray();
        else
            CloseAsTheUserChose();
    }

    // Set for the one close that follows a settled Save / Don't save, then cleared, so later edits are asked about again.
    private bool _unsavedResolved;

    /// <summary>Save / Don't save / Cancel for each document with unsaved changes; Cancel (or a failed save) keeps the
    /// window open with every edit.</summary>
    private async Task ResolveUnsavedAsync(bool programmatic)
    {
        if (_asking || DataContext is not MainWindowViewModel vm)
            return;
        _asking = true;
        try
        {
            foreach (var editor in vm.UnsavedEditors)
                if (!await editor.ResolveUnsavedAsync())
                    return;
        }
        finally
        {
            _asking = false;
        }
        _unsavedResolved = true;
        if (programmatic)
            HideToTray();
        else
            CloseAsTheUserChose();
    }

    /// <summary>What the user's close does: hide, quit or ask (<see cref="AppSettings.CloseAction"/>).</summary>
    internal void CloseAsTheUserChose()
    {
        var store = AppHost.Get<UserDataStore>();
        switch (store.Settings.CloseAction)
        {
            case CloseAction.Tray:
                HideToTray();
                break;
            case CloseAction.Quit:
                Quit();
                break;
            default:
                _ = AskAsync(store);
                break;
        }
    }

    private async Task AskAsync(UserDataStore store)
    {
        if (_asking)
            return;
        _asking = true;
        try
        {
            var dialog = new CloseAskWindow();
            if (await dialog.ShowDialog<CloseAction?>(this) is not { } action)
                return;
            if (dialog.DontAskAgain)
            {
                store.Settings.CloseAction = action;
                store.SaveSettings();
            }
            if (action == CloseAction.Quit)
                Quit();
            else
                HideToTray();
        }
        finally
        {
            _asking = false;
        }
    }

    // Posted: shutting down closes this window again, which must not happen inside its own Closing handler.
    private static void Quit() => Dispatcher.UIThread.Post(() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());

    /// <summary>
    /// Leaves Filee in the tray: hides the window at once, then closes it for real, so its pages, visuals and images
    /// don't stay in memory until the next time (issue #119). <see cref="WindowService.ShowMain"/> opens a new one.
    /// </summary>
    internal void HideToTray()
    {
        Hide();
        if (_releasePosted)
            return;
        _releasePosted = true;
        // Posted: a window can't close inside its own Closing handler.
        Dispatcher.UIThread.Post(() =>
        {
            _releasePosted = false;
            if (IsVisible)
                return; // shown again meanwhile
            _releasing = true;
            Close();
        });
    }
}
