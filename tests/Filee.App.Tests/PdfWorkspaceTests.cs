// The PDF workspace, PDF tools and file utilities pages: they render (light and dark, screenshots for review), and the
// editor opens a real PDF, edits it with undo / redo and saves a file that has the edits.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Engines.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

public class PdfWorkspaceTests
{
    /// <summary>Pages of different sizes with a coloured block each (no text: no fonts needed).</summary>
    private static string MakePdf(int pages)
    {
        var dir = Directory.CreateTempSubdirectory("filee-workspace-").FullName;
        var path = Path.Combine(dir, "sample.pdf");
        using var document = new PdfDocument();
        XColor[] colors = [XColors.SteelBlue, XColors.IndianRed, XColors.SeaGreen, XColors.Goldenrod];
        for (var i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(i % 2 == 0 ? 595 : 842);
            page.Height = XUnit.FromPoint(i % 2 == 0 ? 842 : 595);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(new XSolidBrush(colors[i % colors.Length]), 60, 60, page.Width.Point - 120, 180);
            gfx.DrawRectangle(new XPen(XColors.Gray, 1), 60, 300, page.Width.Point - 120, page.Height.Point - 360);
        }
        document.Save(path);
        return path;
    }

    private static void Pump(int frames = 40)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Runs the UI loop until <paramref name="task"/> is done (the editor renders and saves in the background).</summary>
    private static void Wait(Task task, int timeoutMs = 30000)
    {
        var start = Environment.TickCount64;
        while (!task.IsCompleted)
        {
            Pump(2);
            Thread.Sleep(5);
            Assert.True(Environment.TickCount64 - start < timeoutMs, "Timed out");
        }
        task.GetAwaiter().GetResult();
    }

    internal static void WaitUntil(Func<bool> condition, int timeoutMs = 30000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            Pump(2);
            Thread.Sleep(5);
            Assert.True(Environment.TickCount64 - start < timeoutMs, "Timed out");
        }
    }

    private static void Save(TopLevel window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, name));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static (MainWindow Window, MainWindowViewModel Vm) Open(string page)
    {
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate(page);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 820 };
        window.Show();
        Pump();
        return (window, vm);
    }

    [AvaloniaTheory]
    [InlineData("pdf", "en", false)]
    [InlineData("pdftools", "en", false)]
    [InlineData("utilities", "en", false)]
    [InlineData("home", "en", true)]
    [InlineData("pdftools", "ko", true)]
    [InlineData("utilities", "zh-CN", false)]
    public void New_pages_render(string page, string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var app = Avalonia.Application.Current!;
        var theme = app.RequestedThemeVariant;
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            var (window, vm) = Open(page);
            Assert.Equal(page, vm.SelectedItem.Key);
            Save(window, $"page-{page}-{language}{(dark ? "-dark" : "")}.png");
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = theme;
        }
    }

    [AvaloniaFact]
    public void Workspace_pages_keep_their_state_while_the_window_is_open()
    {
        TestServices.EnsureInitialized();
        var (window, vm) = Open("pdf");
        var editor = Assert.IsType<PdfEditorPageViewModel>(vm.CurrentPage);
        vm.Navigate("general");
        vm.Navigate("pdf");
        Assert.Same(editor, vm.CurrentPage);
        Assert.Equal(double.PositiveInfinity, vm.PageMaxWidth);
        vm.Navigate("general");
        Assert.Equal(980, vm.PageMaxWidth);
        // Each group's list shows only its own selection.
        Assert.Null(vm.SelectedWorkspaceItem);
        Assert.Equal("general", vm.SelectedSettingsItem?.Key);
        window.Close();
    }

    [AvaloniaFact]
    public void Editor_opens_edits_undoes_and_saves_a_pdf()
    {
        TestServices.EnsureInitialized();
        var source = MakePdf(3);
        var sourceBytes = File.ReadAllBytes(source);
        var output = Path.Combine(Path.GetDirectoryName(source)!, "edited.pdf");
        var (window, vm) = Open("pdf");
        var editor = (PdfEditorPageViewModel)vm.CurrentPage;
        editor.PickSavePathAsync = _ => Task.FromResult<string?>(output);

        Wait(vm.OpenPdfAsync(source));
        Assert.Equal(3, editor.Pages.Count);
        Assert.False(editor.IsDirty);
        WaitUntil(() => editor.Pages.All(p => p.Thumbnail is not null) && editor.PreviewBitmap is not null);
        // The thumbnails know the real page sizes.
        Assert.Equal(842, editor.Pages[1].BaseSize.Width, 0);

        // Turn page 1, move page 3 to the front, duplicate it, delete the copy again, undo and redo.
        editor.GoTo(0);
        editor.RotateRightCommand.Execute(null);
        Assert.Equal(90, editor.Pages[0].Rotation);
        editor.MovePages([editor.Pages[2]], 0);
        Assert.Equal(2, editor.Pages[0].Reference.PageIndex);
        editor.GoTo(0);
        editor.DuplicatePagesCommand.Execute(null);
        Assert.Equal(4, editor.Pages.Count);
        editor.UndoCommand.Execute(null);
        Assert.Equal(3, editor.Pages.Count);
        editor.RedoCommand.Execute(null);
        Assert.Equal(4, editor.Pages.Count);
        editor.GoTo(1);
        editor.DeletePagesCommand.Execute(null);
        Assert.Equal([2, 0, 1], editor.Pages.Select(p => p.Reference.PageIndex));
        Assert.True(editor.IsDirty);
        Assert.True(vm.HasUnsavedWork);

        // A rectangle on the first page and page numbers when saving.
        editor.GoTo(0);
        Wait(editor.AddMarkAsync(PdfTool.Rectangle, 100, 400, 200, 120));
        Assert.Single(editor.Pages[0].Marks);
        editor.AddPageNumbers = true;
        WaitUntil(() => editor.PreviewBitmap is { } b && Math.Abs(b.Size.Width - editor.PreviewWidth) < editor.PreviewWidth * 0.5);
        Pump();
        Save(window, "pdf-editor-document.png");

        Wait(editor.SaveCommand.ExecuteAsync(null));

        Assert.False(editor.IsDirty, editor.Message);
        Assert.Equal(output, editor.SavePath);
        using (var saved = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(3, saved.PageCount);
            Assert.Equal([0, 90, 0], saved.Pages.Cast<PdfPage>().Select(p => p.Rotate)); // page 1 (turned) is second now
            Assert.Equal(595, saved.Pages[0].Width.Point, 0);
            var annots = saved.Pages[0].Elements.GetArray("/Annots");
            Assert.NotNull(annots);
        }
        Assert.Equal(sourceBytes, File.ReadAllBytes(source)); // the original is untouched

        // Saving again writes to the same file; Save As is only asked the first time.
        editor.PickSavePathAsync = _ => throw new InvalidOperationException("Save As should not be asked again");
        editor.RotateLeftCommand.Execute(null);
        Wait(editor.SaveCommand.ExecuteAsync(null));
        Assert.False(editor.IsDirty);
        Assert.False(vm.HasUnsavedWork);
        window.Close();
    }

    [AvaloniaFact]
    public void A_mark_turns_with_its_page()
    {
        // A4 portrait shown 595 × 842; a box at the top left ends up at the top right after turning right.
        var mark = new PdfMarkItem(new PdfMark(0, PdfMarkKind.Rectangle, 10, 20, 100, 50));
        PdfEditorPageViewModel.TurnMark(mark, 90, new Avalonia.Size(595, 842));
        Assert.Equal((822, 10, -50, 100), (mark.X, mark.Y, mark.Width, mark.Height));
        PdfEditorPageViewModel.TurnMark(mark, -90, new Avalonia.Size(842, 595));
        Assert.Equal((10, 20, 100, 50), (Math.Min(mark.X, mark.X + mark.Width), mark.Y, Math.Abs(mark.Width), mark.Height));
    }

    [AvaloniaFact]
    public void A_password_protected_pdf_asks_for_its_password()
    {
        TestServices.EnsureInitialized();
        var plain = MakePdf(2);
        var locked = Path.Combine(Path.GetDirectoryName(plain)!, "locked.pdf");
        PdfEditor.Save(PdfEditor.AllPages(plain), locked, options: new PdfSaveOptions { UserPassword = "pw" });
        var (window, vm) = Open("pdf");
        var editor = (PdfEditorPageViewModel)vm.CurrentPage;
        var asked = 0;
        editor.AskPasswordAsync = _ => Task.FromResult<string?>(++asked == 1 ? "wrong" : "pw");

        Wait(editor.OpenAsync(locked));

        Assert.Equal(2, asked);
        Assert.Equal(2, editor.Pages.Count);
        WaitUntil(() => editor.Pages.All(p => p.Thumbnail is not null));
        window.Close();
    }

    /// <summary>A one-page letter with two lines of Arial text.</summary>
    private static string MakeLetter(string dir, string name = "letter.pdf")
    {
        _ = PdfEditor.NormalizeRotation(0); // sets up PDFsharp's Windows fonts
        var path = Path.Combine(dir, name);
        using var document = new PdfDocument();
        var page = document.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            gfx.DrawString("Invoice for March", new XFont("Arial", 18), XBrushes.Black, new XPoint(72, 100));
            gfx.DrawString("Keep this line", new XFont("Arial", 12), XBrushes.Black, new XPoint(72, 200));
        }
        document.Save(path);
        return path;
    }

    private static List<string> WordsOf(string path)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        return pig.GetPage(1).GetWords().Select(w => w.Text).ToList();
    }

    /// <summary>Regression: saving over the opened file, then editing and saving again, used to apply the earlier edits a
    /// second time on top of the already-edited file (the text check then refused every later save).</summary>
    [AvaloniaFact]
    public void Edit_save_over_the_original_and_edit_again_for_several_rounds()
    {
        TestServices.EnsureInitialized();
        var dir = Directory.CreateTempSubdirectory("filee-workspace-").FullName;
        var source = MakeLetter(dir);
        var (window, vm) = Open("pdf");
        var editor = (PdfEditorPageViewModel)vm.CurrentPage;
        editor.PickSavePathAsync = _ => Task.FromResult<string?>(source);
        Wait(vm.OpenPdfAsync(source));

        string[] months = ["April", "May", "June"];
        foreach (var (month, round) in months.Select((m, i) => (m, i)))
        {
            WaitUntil(() => editor.PreviewBitmap is not null);
            Wait(editor.AddMarkAsync(PdfTool.EditText, 100, 95, 0, 0));
            Assert.True(editor.Pages[0].Marks.Count == 1, $"round {round}: {editor.Message}");
            var edit = editor.Pages[0].Marks[0];
            Assert.True(edit.IsReplaceText, editor.Message);
            edit.Text = $"Invoice for {month}";
            // A note too, so other tools are checked after each save.
            Wait(editor.AddMarkAsync(PdfTool.Note, 300, 300 + round * 30, 0, 0));
            Assert.True(editor.IsDirty);

            Wait(editor.SaveCommand.ExecuteAsync(null));

            Assert.False(editor.IsDirty, editor.Message);
            Assert.False(editor.MessageIsError, editor.Message);
            var words = WordsOf(source);
            Assert.Contains(month, words);
            Assert.Contains("Keep", words);
            Assert.DoesNotContain(round == 0 ? "March" : months[round - 1], words);
            // What was saved is now part of the document: nothing is pending, and its notes are in the file.
            Assert.Empty(editor.Pages[0].Marks);
            using var saved = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            Assert.Equal(round + 1, saved.Pages[0].Elements.GetArray("/Annots")!.Elements.Count);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Close_asks_save_dont_save_or_cancel_and_keeps_edits_when_saving_does_not_happen()
    {
        TestServices.EnsureInitialized();
        var dir = Directory.CreateTempSubdirectory("filee-workspace-").FullName;
        var source = MakeLetter(dir);
        var sourceBytes = File.ReadAllBytes(source);
        var output = Path.Combine(dir, "saved.pdf");
        var (window, vm) = Open("pdf");
        var editor = (PdfEditorPageViewModel)vm.CurrentPage;
        var asked = 0;
        var answer = UnsavedChoice.Cancel;
        editor.AskUnsavedAsync = (_, _) => { asked++; return Task.FromResult(answer); };
        string? savePath = null;
        editor.PickSavePathAsync = _ => Task.FromResult(savePath);

        void OpenAndEdit()
        {
            Wait(vm.OpenPdfAsync(source));
            Wait(editor.AddMarkAsync(PdfTool.Rectangle, 100, 400, 120, 80));
            Assert.True(editor.IsDirty);
        }

        // Nothing changed: closes without asking.
        Wait(vm.OpenPdfAsync(source));
        Wait(editor.CloseCommand.ExecuteAsync(null));
        Assert.False(editor.HasDocument);
        Assert.Equal(0, asked);

        // Cancel: stays open with the edit.
        OpenAndEdit();
        answer = UnsavedChoice.Cancel;
        Wait(editor.CloseCommand.ExecuteAsync(null));
        Assert.True(editor.HasDocument);
        Assert.True(editor.IsDirty);
        Assert.Single(editor.Pages[0].Marks);

        // Save, but the Save As dialog is cancelled: not a save, the document stays open with the edit.
        answer = UnsavedChoice.Save;
        savePath = null;
        Wait(editor.CloseCommand.ExecuteAsync(null));
        Assert.True(editor.HasDocument);
        Assert.True(editor.IsDirty);
        Assert.Single(editor.Pages[0].Marks);

        // Save, and writing fails (the target is a folder): still open, edit kept, error shown.
        savePath = Directory.CreateDirectory(Path.Combine(dir, "a folder.pdf")).FullName;
        Wait(editor.CloseCommand.ExecuteAsync(null));
        Assert.True(editor.HasDocument);
        Assert.True(editor.IsDirty);
        Assert.True(editor.MessageIsError);
        Assert.Single(editor.Pages[0].Marks);

        // Save to a real path: written, then closed.
        savePath = output;
        Wait(editor.CloseCommand.ExecuteAsync(null));
        Assert.False(editor.HasDocument);
        using (var saved = PdfReader.Open(output, PdfDocumentOpenMode.Import))
            Assert.NotNull(saved.Pages[0].Elements.GetArray("/Annots"));

        // Don't save: closed, nothing written, the original as it was.
        OpenAndEdit();
        answer = UnsavedChoice.DontSave;
        File.Delete(output);
        Wait(editor.CloseCommand.ExecuteAsync(null));
        Assert.False(editor.HasDocument);
        Assert.False(editor.IsDirty);
        Assert.False(File.Exists(output));
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));

        // Opening another PDF over unsaved changes asks the same question; Cancel keeps the first one.
        OpenAndEdit();
        answer = UnsavedChoice.Cancel;
        var other = MakeLetter(dir, "other.pdf");
        Assert.False(TaskResult(editor.OpenAsync(other)));
        Assert.Equal(source, editor.DocumentPath);
        Assert.Single(editor.Pages[0].Marks);
        answer = UnsavedChoice.DontSave;
        Wait(vm.OpenPdfAsync(other));
        Assert.Equal(other, editor.DocumentPath);
        Assert.Empty(editor.Pages[0].Marks);
        window.Close();
    }

    [AvaloniaFact]
    public void Tools_keep_working_through_repeated_open_edit_close_cycles()
    {
        TestServices.EnsureInitialized();
        var dir = Directory.CreateTempSubdirectory("filee-workspace-").FullName;
        var source = MakeLetter(dir);
        var (window, vm) = Open("pdf");
        var editor = (PdfEditorPageViewModel)vm.CurrentPage;
        editor.AskUnsavedAsync = (_, _) => Task.FromResult(UnsavedChoice.DontSave);
        for (var cycle = 0; cycle < 5; cycle++)
        {
            Wait(vm.OpenPdfAsync(source));
            WaitUntil(() => editor.PreviewBitmap is not null);
            Assert.False(editor.CanUndo);
            Wait(editor.AddMarkAsync(PdfTool.EditText, 100, 95, 0, 0));
            Assert.True(Assert.Single(editor.Pages[0].Marks).IsReplaceText, editor.Message);
            Wait(editor.AddMarkAsync(PdfTool.FreeText, 72, 500, 0, 0));
            Wait(editor.AddMarkAsync(PdfTool.Highlight, 70, 90, 120, 20));
            Assert.Equal(3, editor.Pages[0].Marks.Count);
            editor.UndoCommand.Execute(null);
            Assert.Equal(2, editor.Pages[0].Marks.Count);
            editor.RedoCommand.Execute(null);
            Assert.Equal(3, editor.Pages[0].Marks.Count);
            // Away to Home and back: the document and its edits are still there.
            vm.Navigate("home");
            Pump();
            vm.Navigate("pdf");
            Assert.Same(editor, vm.CurrentPage);
            Assert.Equal(3, editor.Pages[0].Marks.Count);
            // The view hooks its real dialogs up again when it comes back; answer for the test.
            editor.AskUnsavedAsync = (_, _) => Task.FromResult(UnsavedChoice.DontSave);
            Wait(editor.CloseCommand.ExecuteAsync(null));
            Assert.False(editor.HasDocument);
            Assert.Empty(editor.Pages);
        }
        window.Close();
    }

    /// <summary>Found live: a long file name pushed Save and Close off the window; the recent list didn't update.</summary>
    [AvaloniaFact]
    public void A_long_file_name_keeps_save_and_close_in_reach_and_recent_files_update()
    {
        TestServices.EnsureInitialized();
        var dir = Directory.CreateTempSubdirectory("filee-workspace-").FullName;
        var source = MakeLetter(dir, "Guidehouse_Cleared_Data_Analytics_Consultant_COMPLETE_FULL_ANSWERS_with_a_very_long_name.pdf");
        var (window, vm) = Open("pdf");
        var editor = (PdfEditorPageViewModel)vm.CurrentPage;
        Wait(vm.OpenPdfAsync(source));
        Pump();
        // The title (trimmed) ends before Save starts, and Close is inside the window: neither is covered or cut off.
        var close = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CloseDocument");
        var save = window.GetVisualDescendants().OfType<Button>().First(b => b.Command == editor.SaveCommand && b.Classes.Contains("accent"));
        var title = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("h1") && t.Text == editor.Title);
        double Left(Visual v) => v.TranslatePoint(new Point(0, 0), window)!.Value.X;
        Assert.True(Left(title) + title.Bounds.Width <= Left(save), $"title ends at {Left(title) + title.Bounds.Width}, Save starts at {Left(save)}");
        Assert.True(Left(close) + close.Bounds.Width <= window.Bounds.Width);
        Wait(editor.CloseCommand.ExecuteAsync(null));
        Assert.Equal(source, editor.RecentDocuments.First());
        window.Close();
    }

    private static T TaskResult<T>(Task<T> task)
    {
        Wait(task);
        return task.Result;
    }

    [AvaloniaFact]
    public void Edits_existing_text_and_places_a_signature()
    {
        TestServices.EnsureInitialized();
        _ = PdfEditor.NormalizeRotation(0); // sets up PDFsharp's Windows fonts
        var dir = Directory.CreateTempSubdirectory("filee-workspace-").FullName;
        var source = Path.Combine(dir, "letter.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString("Invoice for March", new XFont("Arial", 18), XBrushes.Black, new XPoint(72, 100));
            gfx.DrawString("Keep this line", new XFont("Arial", 12), XBrushes.Black, new XPoint(72, 200));
            document.Save(source);
        }
        var signature = Path.Combine(dir, "sign.png");
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(60, 20)))
            bitmap.Save(signature);
        var output = Path.Combine(dir, "edited.pdf");
        var (window, vm) = Open("pdf");
        var editor = (PdfEditorPageViewModel)vm.CurrentPage;
        editor.PickSavePathAsync = _ => Task.FromResult<string?>(output);
        editor.CreateSignatureAsync = () => Task.FromResult<SignatureChoice?>(new SignatureChoice(signature, 180, 60));

        Wait(vm.OpenPdfAsync(source));
        Assert.False(editor.HasFields);
        WaitUntil(() => editor.Pages.All(p => p.Thumbnail is not null));

        // A click on the first line (shown page, y down) picks it up as a text change.
        Wait(editor.AddMarkAsync(PdfTool.EditText, 100, 95, 0, 0));
        var edit = Assert.Single(editor.Pages[0].Marks);
        Assert.True(edit.IsReplaceText, editor.Message);
        Assert.Equal("Invoice for March", edit.Text);
        edit.Text = "Invoice for April";
        Wait(editor.AddMarkAsync(PdfTool.Signature, 300, 600, 0, 0));
        Assert.Equal(2, editor.Pages[0].Marks.Count);
        Assert.True(editor.Pages[0].Marks[1].IsImage);
        Pump();
        Save(window, "pdf-editor-edit-text.png");

        Wait(editor.SaveCommand.ExecuteAsync(null));

        Assert.False(editor.IsDirty, editor.Message);
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(output))
        {
            var page = pig.GetPage(1);
            var words = page.GetWords().Select(w => w.Text).ToList();
            Assert.Contains("April", words);
            Assert.DoesNotContain("March", words);
            Assert.Contains("Keep", words);
            Assert.NotEmpty(page.GetImages());
        }
        window.Close();
    }
}

/// <summary>Modal dialogs run alone: the headless UI thread is shared, and a dialog test interleaved with other windows'
/// tests could answer the wrong dialog.</summary>
[CollectionDefinition("Modal dialogs", DisableParallelization = true)]
public sealed class ModalDialogCollection;

[Collection("Modal dialogs")]
public class SaveChangesDialogTests
{
    /// <summary>The Save changes dialog answers by keyboard: Enter saves, Alt+N doesn't save, Esc cancels; closing it cancels.</summary>
    [AvaloniaFact]
    public void Save_changes_dialog_answers_by_keyboard()
    {
        TestServices.EnsureInitialized();
        // The real dialog window, shown without blocking an owner (modal dialogs in a shared headless UI thread are
        // timing-sensitive); its keys and result are the same.
        UnsavedChoice Ask(Action<Window> act)
        {
            var dialog = PromptWindow.CreateUnsaved("Save changes?", "“a.pdf” has changes.");
            var closed = false;
            dialog.Closed += (_, _) => closed = true;
            dialog.Show();
            try
            {
                PdfWorkspaceTests.WaitUntil(() => dialog.OkButton.IsFocused, 5000);
                act(dialog);
                PdfWorkspaceTests.WaitUntil(() => closed, 5000);
                return dialog.UnsavedResult;
            }
            finally
            {
                if (!closed)
                    dialog.Close();
            }
        }
        // Headless KeyPress only presses: release each key too, or a held Alt leaks into later tests.
        static void Press(Window d, Key key, RawInputModifiers modifiers, PhysicalKey physical, string? text)
        {
            d.KeyPress(key, modifiers, physical, text);
            if (d.IsVisible) // Enter / Esc close the dialog on the press
                d.KeyRelease(key, modifiers, physical, text);
        }
        Assert.Equal(UnsavedChoice.Save, Ask(d => Press(d, Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null)));
        Assert.Equal(UnsavedChoice.Cancel, Ask(d => Press(d, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null)));
        Assert.Equal(UnsavedChoice.DontSave, Ask(d =>
        {
            Press(d, Key.N, RawInputModifiers.Alt, PhysicalKey.N, "n");
        }));
        Assert.Equal(UnsavedChoice.Cancel, Ask(d => d.Close()));
        // Asked again and again, it still answers.
        for (var i = 0; i < 3; i++)
            Assert.Equal(UnsavedChoice.Save, Ask(d => Press(d, Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null)));
    }
}
