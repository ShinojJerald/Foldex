// Opens, fills and closes the live donut toolbar.
//
// Drag mode  (modifier + drag in Explorer):
//   gesture detected → an invisible window waits at the cursor (not activated) → DragEnter brings the file list →
//   the donut pops out → drop on a slice converts / drop in the hole or release elsewhere closes. Without files
//   (text, a rubber-band selection) nothing ever shows.
// Click mode (keyboard shortcut, context menu, drop zone):
//   files are known up front → donut appears activated → click a slice to convert, right-click to edit,
//   Esc / click outside closes.
// After a drop / click the donut becomes a progress ring at the same spot (QuickPanel.Progress.cs):
//   finished quickly → ✓ pops up in the centre, then the ring fades out;
//   still running after HandOffAfter, failed, Esc, click elsewhere or a new gesture → the toast takes over.

using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class RadialController(
    RadialViewModel viewModel,
    ConversionService conversions,
    WindowService windows,
    UserDataStore store,
    IPlatformServices platform,
    ILogger<RadialController> log)
{
    /// <summary>
    /// How long the ring shows a running job before the toast window takes over. Long enough for a typical office
    /// document (LibreOffice needs a few seconds), so the user sees it finish where they dropped it.
    /// </summary>
    private static readonly TimeSpan HandOffAfter = TimeSpan.FromSeconds(5);

    private QuickPanelWindow? _window;
    private bool _visible;
    private bool _dropped;
    private bool _editing;
    private JobViewModel? _progressJob;   // job currently shown by the ring (null once finished / handed off)
    private JobViewModel? _lastRingJob;   // for "click the ✓ to open the folder"
    private IDisposable? _handOffTimer;
    private int _dragGeneration;
    private bool _dragReachedDonut;

    /// <summary>A gesture over something that isn't a file drag (a rubber-band selection) is common: a line now and then.</summary>
    private static readonly LogThrottle Throttle = new(TimeSpan.FromMinutes(1));
    private bool _converted;   // a conversion started from the files on the donut now
    private readonly HashSet<string> _formatRequestsOffered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a drag that never brought files may keep the invisible window up, in case the button release is
    /// missed (the system dropped the input hook, a UAC prompt took the desktop): the window catches clicks.
    /// </summary>
    private static readonly TimeSpan EmptyDragLimit = TimeSpan.FromSeconds(10);

    public bool IsVisible => _visible;

    internal QuickPanelWindow? Window => _window;

    /// <summary>
    /// Drag gesture started at the given physical screen point. The window is placed under the cursor but stays
    /// invisible until files are dragged into it (FilesEntered); a gesture without files (text, a rubber-band
    /// selection, nothing at all) never shows anything and closes when the button is released.
    /// </summary>
    public void ShowForDrag(int x, int y)
    {
        ReleaseProgressToToast();
        var window = EnsureWindow();
        // A ring from an earlier conversion (finished or handed to the toast) must not keep this gesture's window up.
        window.Panel.LeaveProgressMode();
        _dropped = false;
        _dragReachedDonut = false;
        _converted = false;
        viewModel.ResetForDrag();
        window.Panel.AllowContextEdit = false;
        PlaceAndShow(window, x, y, activate: false);

        var generation = ++_dragGeneration;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation == _dragGeneration && _visible && !viewModel.HasFiles && !viewModel.IsClickMode)
                Close(animate: false);
        }, EmptyDragLimit);
    }

    /// <summary>Opens the donut for known files (click mode).</summary>
    public void ShowForFiles(int x, int y, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return;
        ReleaseProgressToToast();
        var window = EnsureWindow();
        _dropped = false;
        _converted = false;
        viewModel.Load(files, clickMode: true);
        viewModel.FitToThisDonut();
        window.Panel.AllowContextEdit = true;
        PlaceAndShow(window, x, y, activate: true);
        window.Panel.PlayOpenAnimation();
    }

    /// <summary>The mouse button of the drag gesture was released somewhere.</summary>
    public void OnDragGestureEnded()
    {
        if (!_visible || viewModel.IsClickMode)
            return;
        // No drag ever entered the donut's window: nothing was dragged (a rubber-band selection), or Windows kept
        // Explorer's drag out (Filee runs as administrator). Tells "the donut didn't react" reports apart.
        if (!_dragReachedDonut && Throttle.TryEnter("no-drag-enter", out var held))
            log.LogInformation("Drag gesture ended without a drag entering the donut{Elevation}{Held}",
                Program.Elevation.ShellCannotReachFilee ? " (Filee runs as administrator: Windows blocks drops from Explorer)" : "",
                LogThrottle.HeldText(held));
        // Nothing was brought to the donut: the window is invisible but catches clicks, so go right away.
        if (!viewModel.HasFiles && !_window!.Panel.IsInProgressMode)
        {
            Close(animate: false);
            return;
        }
        // A drop on the donut arrives around the same time as the button release; give it a moment.
        DispatcherTimer.RunOnce(() =>
        {
            if (_visible && !_dropped && !viewModel.IsClickMode)
                Close(animate: true);
        }, TimeSpan.FromMilliseconds(250));
    }

    public void Close(bool animate = true)
    {
        if (!_visible || _window is null)
            return;
        if (_window.Panel.IsInProgressMode)
        {
            // Esc / click elsewhere while the ring is showing: let the toast carry on (a finishing ✓ just fades).
            if (_progressJob is not null)
            {
                HandOffToToast();
                return;
            }
            if (_window.Panel.IsFinishingProgress)
                return;
            // A ring left over after it finished: nothing is showing any more, so close like any donut.
            _window.Panel.LeaveProgressMode();
        }
        _visible = false;
        var window = _window;
        // A group about to open (its dwell timer running) must not open on a closing donut.
        window.Panel.CollapseGroup();
        if (animate)
            window.Panel.PlayCloseAnimation(() =>
            {
                if (!_visible)
                    window.Hide();
            });
        else
            window.Hide();
        OfferFormatRequest();
    }

    /// <summary>
    /// The donut closed on files Filee can't read at all (".ncm"): offer to request the format, once per extension
    /// while Filee runs. Files it reads, even mixed with unknown ones, get a conversion and the toast instead.
    /// </summary>
    private void OfferFormatRequest()
    {
        if (_converted || viewModel.Files.Count == 0 || ProfileSelector.DetectFormats(viewModel.Files).Count > 0)
            return;
        var extensions = FormatRequest.UnknownExtensions(viewModel.Files).Where(_formatRequestsOffered.Add).ToList();
        if (extensions.Count == 0)
            return;
        log.LogInformation("Donut closed on files Filee can't read: {Extensions}", FormatRequest.Label(extensions));
        windows.ShowFormatRequestNotice(extensions);
    }

    private QuickPanelWindow EnsureWindow()
    {
        if (_window is not null)
            return _window;

        var window = new QuickPanelWindow { DataContext = viewModel };
        var donut = window.Panel;

        window.DragEntered += () => _dragReachedDonut = true;
        window.FilesEntered += files =>
        {
            if (viewModel.IsClickMode || SameFiles(files, viewModel.Files))
                return;
            viewModel.Load(files, clickMode: false);
            donut.PlayOpenAnimation();
        };
        window.Dropped += (hit, files) =>
        {
            _dropped = true;
            if (hit.Kind is PanelHitKind.Segment or PanelHitKind.SubSegment)
                Convert(hit.Index, hit.Sub, files.Count > 0 ? files : viewModel.Files);
            else
                Close();
        };
        // One line per drag, to tell "the drag never reached the donut" from "it did but nothing lit up" when someone
        // reports a donut that doesn't react.
        window.DragSummary += (events, hit, lit) =>
            log.LogInformation("Drag over the donut: {Events} move event(s), ended on {Kind} {Index}, drawn lit {Lit:0.00}",
                events, hit.Kind, hit.Index, lit);
        window.EscapePressed += () => Close();
        window.Deactivated += (_, _) =>
        {
            if (viewModel.IsClickMode && !_editing)
                Close();
        };

        donut.ActiveIndexChanged += (_, index) => viewModel.ShowHint(index, donut.ActiveSubIndex, donut.IsCenterHot);
        donut.SegmentInvoked += (_, index) => Convert(index, -1, viewModel.Files);
        donut.SubSegmentInvoked += (_, hit) => Convert(hit.Group, hit.Sub, viewModel.Files);
        donut.CenterInvoked += (_, _) => Close();
        donut.SegmentContextRequested += async (_, index) => await EditPresetAsync(index);
        donut.SubSegmentContextRequested += async (_, hit) => await EditPresetAsync(hit.Group, hit.Sub);
        donut.ProgressCancelRequested += (_, _) => _progressJob?.CancelCommand.Execute(null);
        donut.ProgressClicked += (_, _) =>
        {
            if (_lastRingJob is { HasOutputs: true } job)
                job.OpenFolderCommand.Execute(null);
        };

        _window = window;
        return window;
    }

    /// <summary>Converts with slice <paramref name="index"/>, or with slice <paramref name="sub"/> of that group's ring.</summary>
    private void Convert(int index, int sub, IReadOnlyList<string> files)
    {
        if (viewModel.PresetAt(index, sub) is not { } preset || files.Count == 0)
        {
            Close();
            return;
        }

        log.LogInformation("Converting {Count} file(s) with preset {Preset}", files.Count, preset.Id);
        var job = conversions.Start(files, preset, showInToast: false);
        if (job is null || _window is null)
        {
            Close();
            return;
        }
        _converted = true;
        ShowProgress(job);
    }

    // ───────── Progress ring ─────────

    private void ShowProgress(JobViewModel job)
    {
        var donut = _window!.Panel;
        _dropped = true;
        _progressJob = job;
        _lastRingJob = job;
        donut.BeginProgress();
        donut.SetProgress(job.Progress / 100);
        job.PropertyChanged += OnProgressJobChanged;
        _handOffTimer = DispatcherTimer.RunOnce(() =>
        {
            if (_progressJob == job && job.IsRunning)
                HandOffToToast();
        }, HandOffAfter);
    }

    private void OnProgressJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not JobViewModel job || job != _progressJob || _window is null)
            return;
        var donut = _window.Panel;
        donut.SetProgress(job.Progress / 100);
        if (job.IsRunning || e.PropertyName != nameof(JobViewModel.IsRunning))
            return;

        var outcome = job.Job.State switch
        {
            JobState.Completed => PanelOutcome.Succeeded,
            JobState.Cancelled => PanelOutcome.Cancelled,
            _ => PanelOutcome.Failed,
        };
        if (outcome == PanelOutcome.Failed)
            conversions.ShowInToast(job); // the toast explains what went wrong
        DetachProgress();
        donut.CompleteProgress(outcome, HideWindow);
    }

    /// <summary>Hands the running job to the toast with a short "shrink away" of the ring.</summary>
    private void HandOffToToast()
    {
        if (_progressJob is null || _window is null)
            return;
        conversions.ShowInToast(_progressJob);
        DetachProgress();
        _window.Panel.HandOffProgress(HideWindow);
    }

    /// <summary>A new gesture needs the window right now: move the job to the toast without animation.</summary>
    private void ReleaseProgressToToast()
    {
        if (_progressJob is not null)
            conversions.ShowInToast(_progressJob);
        DetachProgress();
    }

    private void DetachProgress()
    {
        _handOffTimer?.Dispose();
        _handOffTimer = null;
        if (_progressJob is not null)
            _progressJob.PropertyChanged -= OnProgressJobChanged;
        _progressJob = null;
    }

    private void HideWindow()
    {
        _visible = false;
        _window?.Panel.CollapseGroup();
        _window?.Hide();
    }

    /// <summary>Edits the preset of slice <paramref name="index"/>, or of slice <paramref name="sub"/> of its group's ring.</summary>
    private async Task EditPresetAsync(int index, int sub = -1)
    {
        if (_window is null || viewModel.PresetShownAt(index, sub) is not { } preset)
            return;
        _editing = true;
        try
        {
            if (await windows.EditPresetAsync(preset, _window))
            {
                store.SaveLibrary();
                viewModel.Load(viewModel.Files, viewModel.IsClickMode);
            }
        }
        finally
        {
            _editing = false;
        }
        if (_visible)
            _window.Activate();
    }

    /// <summary>
    /// Top-left corner of the panel window so the cursor sits on its header, centred, moved into <paramref name="area"/>
    /// when it would cross an edge (the panel grows downwards when files arrive, so its full height is kept on screen).
    /// </summary>
    internal static PixelPoint WindowPosition(PixelPoint cursor, PixelRect? area, int widthPx, int heightPx, int headerPx)
    {
        var position = new PixelPoint(cursor.X - widthPx / 2, cursor.Y - headerPx);
        if (area is not { } a)
            return position;
        return new PixelPoint(
            Math.Clamp(position.X, a.X, Math.Max(a.X, a.Right - widthPx)),
            Math.Clamp(position.Y, a.Y, Math.Max(a.Y, a.Bottom - heightPx)));
    }

    private void PlaceAndShow(QuickPanelWindow window, int x, int y, bool activate)
    {
        // No known cursor position (the mouse hasn't moved since Foldex started): the middle of the main screen.
        if (x == 0 && y == 0 && window.Screens.Primary is { } primary)
            (x, y) = (primary.WorkingArea.Center.X, primary.WorkingArea.Y + primary.WorkingArea.Height / 3);
        var cursor = new PixelPoint(x, y);
        var screen = window.Screens.ScreenFromPoint(cursor) ?? window.Screens.Primary;
        var scaling = OperatingSystem.IsMacOS() ? 1 : screen?.Scaling ?? 1;

        // The window follows the panel (SizeToContent); size it up front. Before a drag brings files the panel is
        // empty, so keep room for a typical panel of three rows.
        const double margin = 18;
        var panel = QuickPanel.SizeFor(viewModel.Items);
        var height = Math.Max(panel.Height, QuickPanel.SizeFor(Enumerable.Range(0, 9).Select(i => new PanelItem { Id = $"{i}", Label = "" }).ToList()).Height);
        var position = WindowPosition(cursor, screen?.WorkingArea, (int)Math.Ceiling((panel.Width + 2 * margin) * scaling),
            (int)Math.Ceiling((height + 2 * margin) * scaling), (int)Math.Ceiling((margin + 30) * scaling));

        Motion.Refresh();
        window.Position = position;
        if (!window.IsVisible)
            window.Show();
        window.Position = position; // again, in case showing changed the DPI scale
        // Showing a window that is already marked topmost does not move it in the z-order, and Windows can push it
        // below normal windows while it keeps the mark (after the Photos app opened, for one). Then Explorer stays on
        // top of the donut and the drop never reaches it, so put it back on top every time.
        if (window.TryGetPlatformHandle()?.Handle is { } handle)
            platform.RaiseTopmost(handle);
        _visible = true;
        if (activate)
            window.Activate();
    }

    private static bool SameFiles(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.SequenceEqual(b, FileSystemPaths.Comparer);
}
