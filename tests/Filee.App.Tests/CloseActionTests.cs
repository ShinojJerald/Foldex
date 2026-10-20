// Closing the main window: keep Filee in the tray, quit, or ask once with "Don't ask again" (Settings → General).

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class CloseActionTests
{
    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static MainWindow ShowMain()
    {
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("general");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        return window;
    }

    private static void Click(Window window, string resourceKey)
    {
        var text = (string)window.FindResource(resourceKey)!;
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, text));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    [AvaloniaFact]
    public void Keep_in_tray_hides_the_window_without_asking()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        store.Settings.CloseAction = CloseAction.Tray;
        var window = ShowMain();
        try
        {
            window.CloseAsTheUserChose();
            Pump();

            Assert.False(window.IsVisible);
            Assert.Empty(window.OwnedWindows);
        }
        finally
        {
            store.Settings.CloseAction = CloseAction.Ask;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Ask_with_dont_ask_again_saves_the_answer_and_the_general_page_follows()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        store.Settings.CloseAction = CloseAction.Ask;
        var window = ShowMain();
        try
        {
            var page = (GeneralPageViewModel)((MainWindowViewModel)window.DataContext!).CurrentPage;
            Assert.Equal(CloseAction.Ask, page.CloseChoice.Value);

            window.CloseAsTheUserChose();
            Pump();
            var dialog = Assert.IsType<CloseAskWindow>(Assert.Single(window.OwnedWindows));
            dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            Click(dialog, "close.tray");

            Assert.False(dialog.IsVisible);
            Assert.False(window.IsVisible);
            Assert.Equal(CloseAction.Tray, store.Settings.CloseAction);
            Assert.Equal(CloseAction.Tray, page.CloseChoice.Value);
        }
        finally
        {
            store.Settings.CloseAction = CloseAction.Ask;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Cancel_keeps_the_window_open_and_asks_again_next_time()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        store.Settings.CloseAction = CloseAction.Ask;
        var window = ShowMain();
        try
        {
            window.CloseAsTheUserChose();
            Pump();
            var dialog = Assert.IsType<CloseAskWindow>(Assert.Single(window.OwnedWindows));
            dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            Click(dialog, "common.cancel");

            Assert.True(window.IsVisible);
            Assert.Equal(CloseAction.Ask, store.Settings.CloseAction);

            // Answered without "Don't ask again": hidden this time, asked again the next.
            window.CloseAsTheUserChose();
            Pump();
            Click(Assert.IsType<CloseAskWindow>(Assert.Single(window.OwnedWindows)), "close.tray");
            Assert.False(window.IsVisible);
            Assert.Equal(CloseAction.Ask, store.Settings.CloseAction);
        }
        finally
        {
            store.Settings.CloseAction = CloseAction.Ask;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_general_page_choice_is_saved()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        using var page = new GeneralPageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<Filee.Core.Platform.IPlatformServices>());
        try
        {
            Assert.Equal(["Ask every time", "Keep Foldex running in the tray", "Quit Foldex"], page.CloseChoices.Select(c => c.Label));
            page.CloseChoice = page.CloseChoices.Single(c => c.Value == CloseAction.Quit);
            Assert.Equal(CloseAction.Quit, store.Settings.CloseAction);
        }
        finally
        {
            store.Settings.CloseAction = CloseAction.Ask;
        }
    }
}
