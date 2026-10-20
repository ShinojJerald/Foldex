using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>Yes / no questions and text (password) input for the PDF workspace and tools.</summary>
public partial class PromptWindow : Window
{
    private bool _ok;
    private bool _alt;

    public PromptWindow()
    {
        InitializeComponent();
        Motion.Track(this);
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _ok = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnAlt(object? sender, RoutedEventArgs e)
    {
        _alt = true;
        Close();
    }

    /// <summary>Save (Enter), Don't save (Alt+N) or Cancel (Esc, or closing the dialog).</summary>
    public static async Task<ViewModels.Pages.UnsavedChoice> AskUnsavedAsync(Window owner, string title, string message)
    {
        var window = CreateUnsaved(title, message);
        await window.ShowDialog(owner);
        return window.UnsavedResult;
    }

    /// <summary>The Save changes dialog, not shown yet.</summary>
    internal static PromptWindow CreateUnsaved(string title, string message)
    {
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        var window = new PromptWindow();
        window.Heading.Text = title;
        window.Message.Text = message;
        window.OkButton.Content = loc["pdf.unsaved_save"];
        window.AltButton.Content = loc["pdf.unsaved_dont"];
        window.AltButton.IsVisible = true;
        window.Width = 460;
        window.Opened += (_, _) => window.OkButton.Focus();
        return window;
    }

    /// <summary>What the Save changes dialog was answered with (Cancel until Save or Don't save is chosen).</summary>
    internal ViewModels.Pages.UnsavedChoice UnsavedResult =>
        _ok ? ViewModels.Pages.UnsavedChoice.Save : _alt ? ViewModels.Pages.UnsavedChoice.DontSave : ViewModels.Pages.UnsavedChoice.Cancel;

    /// <summary>True when the person chose OK (<paramref name="okText"/> labels the button).</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string? okText = null)
    {
        var window = new PromptWindow();
        window.Heading.Text = title;
        window.Message.Text = message;
        if (okText is not null)
            window.OkButton.Content = okText;
        await window.ShowDialog(owner);
        return window._ok;
    }

    /// <summary>The entered text, or null when cancelled.</summary>
    public static async Task<string?> AskAsync(Window owner, string title, string message, bool password)
    {
        var window = new PromptWindow();
        window.Heading.Text = title;
        window.Message.Text = message;
        window.Input.IsVisible = true;
        if (password)
            window.Input.PasswordChar = '•';
        window.Opened += (_, _) => window.Input.Focus();
        await window.ShowDialog(owner);
        return window._ok ? window.Input.Text ?? "" : null;
    }
}
