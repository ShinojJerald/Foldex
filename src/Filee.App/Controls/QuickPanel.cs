// The Quick panel: a glass card of action tiles that opens at the cursor when files are dragged with the shortcut held
// (or picked with Browse / dropped on Home). Used both live (QuickPanelWindow) and as the editor in Settings → Quick
// panel (IsEditMode).
//
// Layout, top to bottom: a header (what was brought: "3 × JPG", and a hint line), a close button, then tiles in three
// columns. A group preset becomes a small heading followed by its presets' tiles. After a drop the tiles give way to a
// progress bar that ends in ✓ or ✕.
//
// Drawn by hand (no child controls) so it opens instantly and every tile hit-tests exactly while Explorer's drag is
// over it; the window never takes focus during a drag. Animations are short property transitions and are skipped with
// Motion.Enabled == false.

using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Controls;

/// <summary>One action of the panel (a preset), or a group of them.</summary>
public sealed class PanelItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }

    /// <summary>Small second line, e.g. the target format.</summary>
    public string? Caption { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>Why a disabled action can't run (shown in the header hint).</summary>
    public string? DisabledReason { get; init; }

    /// <summary>A group: shown as a heading with these actions under it.</summary>
    public IReadOnlyList<PanelItem>? Children { get; init; }

    public bool IsGroup => Children is not null;
    public bool CanExpand => Children is { Count: > 0 };
}

/// <summary>What is under a point of the panel.</summary>
public enum PanelHitKind
{
    Outside,
    /// <summary>The close button (drop there to cancel).</summary>
    Center,
    /// <summary>An action tile; <see cref="PanelHit.Index"/> is valid.</summary>
    Segment,
    /// <summary>The body of a panel without actions.</summary>
    EmptyRing,
    /// <summary>The ✎ button of the hovered tile (edit mode).</summary>
    EditButton,
    /// <summary>The progress view shown after a drop.</summary>
    ProgressRing,
    /// <summary>The cancel button of the progress view.</summary>
    ProgressCancel,
    /// <summary>An action inside a group: <see cref="PanelHit.Index"/> is the group, <see cref="PanelHit.Sub"/> the action.</summary>
    SubSegment,
    /// <summary>Elsewhere on the card (header, gaps).</summary>
    Card,
}

public readonly record struct PanelHit(PanelHitKind Kind, int Index = -1, int Sub = -1);

public enum PanelOutcome { Succeeded, Failed, Cancelled }

public sealed class QuickPanel : Control
{
    public const double TileWidth = 118;
    public const double TileHeight = 84;
    public const double Gap = 8;
    public const double Pad = 16;
    public const double HeaderHeight = 62;
    public const double GroupHeaderHeight = 26;
    public const int Columns = 3;
    public const double PanelWidth = Pad * 2 + Columns * TileWidth + (Columns - 1) * Gap;
    private const double DragStartDistance = 5;
    private static readonly TimeSpan AnimationTime = TimeSpan.FromMilliseconds(170);

    public static readonly StyledProperty<IReadOnlyList<PanelItem>?> ItemsProperty =
        AvaloniaProperty.Register<QuickPanel, IReadOnlyList<PanelItem>?>(nameof(Items));
    public static readonly StyledProperty<string?> CenterTitleProperty = AvaloniaProperty.Register<QuickPanel, string?>(nameof(CenterTitle));
    public static readonly StyledProperty<string?> CenterSubtitleProperty = AvaloniaProperty.Register<QuickPanel, string?>(nameof(CenterSubtitle));
    public static readonly StyledProperty<bool> IsEditModeProperty = AvaloniaProperty.Register<QuickPanel, bool>(nameof(IsEditMode));
    public static readonly StyledProperty<bool> AllowContextEditProperty = AvaloniaProperty.Register<QuickPanel, bool>(nameof(AllowContextEdit));
    public static readonly StyledProperty<IBrush?> SurfaceProperty = AvaloniaProperty.Register<QuickPanel, IBrush?>(nameof(Surface));
    public static readonly StyledProperty<IBrush?> TileBrushProperty = AvaloniaProperty.Register<QuickPanel, IBrush?>(nameof(TileBrush));
    public static readonly StyledProperty<IBrush?> BorderBrushProperty = AvaloniaProperty.Register<QuickPanel, IBrush?>(nameof(BorderBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<QuickPanel, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<QuickPanel, IBrush?>(nameof(Foreground));
    public static readonly StyledProperty<IBrush?> SecondaryForegroundProperty = AvaloniaProperty.Register<QuickPanel, IBrush?>(nameof(SecondaryForeground));
    public static readonly StyledProperty<IBrush?> DangerBrushProperty = AvaloniaProperty.Register<QuickPanel, IBrush?>(nameof(DangerBrush));
    public static readonly StyledProperty<IBrush?> SuccessBrushProperty = AvaloniaProperty.Register<QuickPanel, IBrush?>(nameof(SuccessBrush));

    static QuickPanel()
    {
        AffectsRender<QuickPanel>(CenterTitleProperty, CenterSubtitleProperty, SurfaceProperty, TileBrushProperty, BorderBrushProperty,
            AccentBrushProperty, ForegroundProperty, SecondaryForegroundProperty, IsEditModeProperty);
        AffectsMeasure<QuickPanel>(ItemsProperty);
        ItemsProperty.Changed.AddClassHandler<QuickPanel>((p, _) => p.OnItemsChanged());
    }

    public QuickPanel()
    {
        ClipToBounds = false;
        Focusable = true;
        RenderTransformOrigin = RelativePoint.Center;
    }

    public IReadOnlyList<PanelItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public string? CenterTitle { get => GetValue(CenterTitleProperty); set => SetValue(CenterTitleProperty, value); }
    public string? CenterSubtitle { get => GetValue(CenterSubtitleProperty); set => SetValue(CenterSubtitleProperty, value); }
    public bool IsEditMode { get => GetValue(IsEditModeProperty); set => SetValue(IsEditModeProperty, value); }
    /// <summary>Right-click a tile to edit its preset (live panel in click mode).</summary>
    public bool AllowContextEdit { get => GetValue(AllowContextEditProperty); set => SetValue(AllowContextEditProperty, value); }
    public IBrush? Surface { get => GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }
    public IBrush? TileBrush { get => GetValue(TileBrushProperty); set => SetValue(TileBrushProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? SecondaryForeground { get => GetValue(SecondaryForegroundProperty); set => SetValue(SecondaryForegroundProperty, value); }
    public IBrush? DangerBrush { get => GetValue(DangerBrushProperty); set => SetValue(DangerBrushProperty, value); }
    public IBrush? SuccessBrush { get => GetValue(SuccessBrushProperty); set => SetValue(SuccessBrushProperty, value); }

    // ───────── Events (same meaning as before for the controller and the editor) ─────────

    public event EventHandler<int>? SegmentInvoked;
    public event EventHandler<(int Group, int Sub)>? SubSegmentInvoked;
    public event EventHandler<int>? SegmentContextRequested;
    public event EventHandler<(int Group, int Sub)>? SubSegmentContextRequested;
    public event EventHandler? CenterInvoked;
    public event EventHandler<(int Index, PointerEventArgs Args)>? SegmentDragStarted;
    public event EventHandler<int>? ActiveIndexChanged;
    public event EventHandler? ProgressCancelRequested;
    public event EventHandler? ProgressClicked;

    // ───────── Layout ─────────

    /// <summary>A tile (or group heading) and where it is.</summary>
    internal readonly record struct Cell(int Index, int Sub, Rect Bounds, bool IsHeading);

    private List<Cell> _cells = [];

    /// <summary>Size of a panel showing <paramref name="items"/> (for placing its window before it is shown).</summary>
    public static Size SizeFor(IReadOnlyList<PanelItem>? items) => new(PanelWidth, Layout(items).Height);

    private static (List<Cell> Cells, double Height) Layout(IReadOnlyList<PanelItem>? items)
    {
        var cells = new List<Cell>();
        var y = HeaderHeight;
        var column = 0;
        void NewRow()
        {
            if (column > 0)
            {
                y += TileHeight + Gap;
                column = 0;
            }
        }
        void Place(int index, int sub)
        {
            var x = Pad + column * (TileWidth + Gap);
            cells.Add(new Cell(index, sub, new Rect(x, y, TileWidth, TileHeight), false));
            if (++column == Columns)
                NewRow();
        }
        for (var i = 0; i < (items?.Count ?? 0); i++)
        {
            var item = items![i];
            if (!item.IsGroup)
            {
                Place(i, -1);
                continue;
            }
            NewRow();
            cells.Add(new Cell(i, -1, new Rect(Pad, y, PanelWidth - 2 * Pad, GroupHeaderHeight), true));
            y += GroupHeaderHeight;
            for (var j = 0; j < item.Children!.Count; j++)
                Place(i, j);
            NewRow();
        }
        NewRow();
        // An empty panel (before files arrived, or no actions) keeps one row of room for its message.
        if (cells.Count == 0)
            y += TileHeight + Gap;
        return (cells, y + Pad - Gap);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        _progressMode ? new Size(PanelWidth, Math.Max(_progressHeight, HeaderHeight + 96)) : SizeFor(Items);

    private double _progressHeight;

    private void OnItemsChanged()
    {
        _cells = Layout(Items).Cells;
        _hover = -1;
        _hoverSub = -1;
        _external = -1;
        _externalSub = -1;
        _dimmed = -1;
        _previewSlot = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Where the tile of action <paramref name="index"/> (or of action <paramref name="sub"/> in that group) is.</summary>
    internal Rect TileBounds(int index, int sub = -1) =>
        _cells.FirstOrDefault(c => !c.IsHeading && c.Index == index && c.Sub == sub).Bounds;

    private Rect CloseButton => new(PanelWidth - Pad - 28, Pad - 2, 28, 28);

    // ───────── Hit testing ─────────

    public PanelHit HitTest(Point p)
    {
        var bounds = new Rect(Bounds.Size);
        if (!bounds.Contains(p))
            return new PanelHit(PanelHitKind.Outside);
        if (_progressMode)
            return _progressCancel.Contains(p) && _outcome is null ? new PanelHit(PanelHitKind.ProgressCancel) : new PanelHit(PanelHitKind.ProgressRing);
        if (!IsEditMode && CloseButton.Inflate(4).Contains(p))
            return new PanelHit(PanelHitKind.Center);
        if (IsEditMode && _hover >= 0 && EditButtonFor(_hover, _hoverSub) is { } edit && edit.Inflate(3).Contains(p))
            return new PanelHit(PanelHitKind.EditButton, _hover, _hoverSub);
        foreach (var cell in _cells)
        {
            if (cell.IsHeading || !cell.Bounds.Inflate(Gap / 2).Contains(p))
                continue;
            return cell.Sub >= 0 ? new PanelHit(PanelHitKind.SubSegment, cell.Index, cell.Sub) : new PanelHit(PanelHitKind.Segment, cell.Index);
        }
        if ((Items?.Count ?? 0) == 0)
            return new PanelHit(PanelHitKind.EmptyRing);
        return new PanelHit(PanelHitKind.Card);
    }

    /// <summary>Whether a drop on <paramref name="hit"/> starts a conversion.</summary>
    public bool Converts(PanelHit hit) => hit.Kind switch
    {
        PanelHitKind.Segment => Items is { } items && hit.Index < items.Count && items[hit.Index] is { IsEnabled: true, IsGroup: false },
        PanelHitKind.SubSegment => Items is { } items && hit.Index < items.Count && items[hit.Index].Children is { } c && hit.Sub < c.Count && c[hit.Sub].IsEnabled,
        _ => false,
    };

    /// <summary>Insertion slot (0..<paramref name="slots"/>-1) nearest <paramref name="p"/>, for the editor.</summary>
    public int SlotAt(Point p, int slots)
    {
        var tiles = _cells.Where(c => !c.IsHeading && c.Sub < 0).ToList();
        if (tiles.Count == 0)
            return 0;
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var slot = 0; slot < Math.Min(slots, tiles.Count + 1); slot++)
        {
            var at = slot < tiles.Count ? tiles[slot].Bounds.TopLeft : tiles[^1].Bounds.TopRight;
            var anchor = new Point(at.X, at.Y + TileHeight / 2);
            var distance = Math.Pow(anchor.X - p.X, 2) + Math.Pow(anchor.Y - p.Y, 2);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = slot;
            }
        }
        return Math.Clamp(best, 0, slots - 1);
    }

    // ───────── Highlight (pointer, keyboard, an external drag) ─────────

    private int _hover = -1;
    private int _hoverSub = -1;
    private int _external = -1;
    private int _externalSub = -1;
    private bool _externalCloseHot;
    private int _dimmed = -1;
    private int _dropTarget = -1;
    private int _previewSlot = -1;
    private string? _previewLabel;
    private (int Index, Point Start, PointerPressedEventArgs Args)? _press;

    public int ActiveIndex => _external >= 0 ? _external : _hover;
    public int ActiveSubIndex => _external >= 0 ? _externalSub : _hoverSub;
    public bool IsCenterHot => _externalCloseHot;

    /// <summary>How lit tile <paramref name="index"/> is drawn (1 = fully), for the drag log.</summary>
    public double LitAmount(int index) => index >= 0 && index == ActiveIndex ? 1 : 0;

    public void SetExternalHighlight(int index, bool centerHot = false)
    {
        var changed = index != _external || centerHot != _externalCloseHot;
        _external = index;
        _externalSub = -1;
        _externalCloseHot = centerHot;
        if (changed)
        {
            ActiveIndexChanged?.Invoke(this, ActiveIndex);
            InvalidateVisual();
        }
    }

    /// <summary>An external drag is over <paramref name="hit"/>.</summary>
    public void SetExternalHit(PanelHit hit, Point? at = null)
    {
        var index = hit.Kind is PanelHitKind.Segment or PanelHitKind.SubSegment ? hit.Index : -1;
        var sub = hit.Kind == PanelHitKind.SubSegment ? hit.Sub : -1;
        var close = hit.Kind == PanelHitKind.Center;
        if (index == _external && sub == _externalSub && close == _externalCloseHot)
            return;
        _external = index;
        _externalSub = sub;
        _externalCloseHot = close;
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        InvalidateVisual();
    }

    public void ExternalDragLeft(Point? lastAt) => SetExternalHighlight(-1);

    /// <summary>Kept for the controller: the panel shows groups open, so there is nothing to collapse.</summary>
    public void CollapseGroup() { }

    public void SetDimmedIndex(int index)
    {
        _dimmed = index;
        InvalidateVisual();
    }

    public void SetDropTarget(int index)
    {
        _dropTarget = index;
        InvalidateVisual();
    }

    /// <summary>Editor: where a dragged preset would go (an insertion bar before tile <paramref name="slot"/>).</summary>
    public void SetDropPreview(int slot, string? label = null, int removed = -1)
    {
        _previewSlot = slot;
        _previewLabel = label;
        InvalidateVisual();
    }

    private void SetHover(int index, int sub)
    {
        if (index == _hover && sub == _hoverSub)
            return;
        _hover = index;
        _hoverSub = sub;
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_press is { } press && IsEditMode && Math.Sqrt(Math.Pow(p.X - press.Start.X, 2) + Math.Pow(p.Y - press.Start.Y, 2)) > DragStartDistance)
        {
            _press = null;
            SegmentDragStarted?.Invoke(this, (press.Index, e));
            return;
        }
        var hit = HitTest(p);
        if (hit.Kind == PanelHitKind.EditButton)
            return;
        SetHover(hit.Kind is PanelHitKind.Segment or PanelHitKind.SubSegment ? hit.Index : -1, hit.Kind == PanelHitKind.SubSegment ? hit.Sub : -1);
        Cursor = hit.Kind is PanelHitKind.Segment or PanelHitKind.SubSegment or PanelHitKind.Center or PanelHitKind.ProgressCancel
            ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(-1, -1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var hit = HitTest(p);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed && (IsEditMode || AllowContextEdit))
        {
            if (hit.Kind == PanelHitKind.Segment)
                SegmentContextRequested?.Invoke(this, hit.Index);
            else if (hit.Kind == PanelHitKind.SubSegment)
                SubSegmentContextRequested?.Invoke(this, (hit.Index, hit.Sub));
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed)
            return;
        if (hit.Kind == PanelHitKind.EditButton)
        {
            if (hit.Sub >= 0)
                SubSegmentContextRequested?.Invoke(this, (hit.Index, hit.Sub));
            else
                SegmentContextRequested?.Invoke(this, hit.Index);
            e.Handled = true;
            return;
        }
        if (IsEditMode && hit.Kind == PanelHitKind.Segment)
            _press = (hit.Index, p, e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pressed = _press;
        _press = null;
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        var hit = HitTest(e.GetPosition(this));
        if (IsEditMode)
            return;
        switch (hit.Kind)
        {
            case PanelHitKind.Segment when Converts(hit):
                SegmentInvoked?.Invoke(this, hit.Index);
                break;
            case PanelHitKind.SubSegment when Converts(hit):
                SubSegmentInvoked?.Invoke(this, (hit.Index, hit.Sub));
                break;
            case PanelHitKind.Center:
                CenterInvoked?.Invoke(this, EventArgs.Empty);
                break;
            case PanelHitKind.ProgressCancel:
                ProgressCancelRequested?.Invoke(this, EventArgs.Empty);
                break;
            case PanelHitKind.ProgressRing:
                ProgressClicked?.Invoke(this, EventArgs.Empty);
                break;
        }
        _ = pressed;
    }

    /// <summary>Arrow keys move between tiles, Enter runs the highlighted one. True when the key was used.</summary>
    public bool HandleKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (_progressMode || IsEditMode)
            return false;
        var tiles = _cells.Where(c => !c.IsHeading).ToList();
        if (tiles.Count == 0)
            return false;
        var current = tiles.FindIndex(c => c.Index == ActiveIndex && c.Sub == ActiveSubIndex);
        int Next(int step) => current < 0 ? 0 : Math.Clamp(current + step, 0, tiles.Count - 1);
        switch (key)
        {
            case Key.Right or Key.Tab when modifiers == KeyModifiers.None:
                Select(Next(1));
                return true;
            case Key.Left:
                Select(Next(-1));
                return true;
            case Key.Down:
                Select(NearestBelow(tiles, current, +1));
                return true;
            case Key.Up:
                Select(NearestBelow(tiles, current, -1));
                return true;
            case Key.Enter or Key.Space when current >= 0:
                var cell = tiles[current];
                var hit = cell.Sub >= 0 ? new PanelHit(PanelHitKind.SubSegment, cell.Index, cell.Sub) : new PanelHit(PanelHitKind.Segment, cell.Index);
                if (!Converts(hit))
                    return true;
                if (cell.Sub >= 0)
                    SubSegmentInvoked?.Invoke(this, (cell.Index, cell.Sub));
                else
                    SegmentInvoked?.Invoke(this, cell.Index);
                return true;
        }
        return false;

        void Select(int i)
        {
            _external = -1;
            SetHover(tiles[i].Index, tiles[i].Sub);
        }
    }

    private static int NearestBelow(List<Cell> tiles, int current, int direction)
    {
        if (current < 0)
            return 0;
        var from = tiles[current].Bounds.Center;
        var candidates = tiles.Select((c, i) => (c, i))
            .Where(t => direction > 0 ? t.c.Bounds.Top > from.Y + 1 : t.c.Bounds.Bottom < from.Y - 1)
            .OrderBy(t => Math.Abs(t.c.Bounds.Top - from.Y)).ThenBy(t => Math.Abs(t.c.Bounds.Center.X - from.X))
            .ToList();
        return candidates.Count == 0 ? current : candidates[0].i;
    }

    // ───────── Open / close ─────────

    public void PlayOpenAnimation()
    {
        Transitions = null;
        if (!Motion.Enabled)
        {
            Opacity = 1;
            RenderTransform = null;
            return;
        }
        Opacity = 0;
        RenderTransform = TransformOperations.Parse("scale(0.96) translateY(6px)");
        Transitions = [
            new DoubleTransition { Property = OpacityProperty, Duration = AnimationTime, Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = AnimationTime, Easing = new CubicEaseOut() },
        ];
        Dispatcher.UIThread.Post(() =>
        {
            Opacity = 1;
            RenderTransform = TransformOperations.Parse("scale(1) translateY(0px)");
        }, DispatcherPriority.Render);
    }

    public void PlayCloseAnimation(Action done)
    {
        if (!Motion.Enabled)
        {
            done();
            return;
        }
        Opacity = 0;
        RenderTransform = TransformOperations.Parse("scale(0.97) translateY(4px)");
        DispatcherTimer.RunOnce(() =>
        {
            done();
            Transitions = null;
            Opacity = 1;
            RenderTransform = null;
        }, AnimationTime);
    }

    /// <summary>A short nudge (used after a click mode drop on the close button).</summary>
    public void PulseCenter() => InvalidateVisual();

    // ───────── Progress ─────────

    private bool _progressMode;
    private double _progress;
    private PanelOutcome? _outcome;
    private Action? _progressDone;
    private Rect _progressCancel;

    public bool IsInProgressMode => _progressMode;
    public bool IsFinishingProgress => _progressMode && _progressDone is not null;
    internal PanelOutcome? ShownOutcome => _outcome;

    public void LeaveProgressMode()
    {
        _progressMode = false;
        _progress = 0;
        _outcome = null;
        _progressDone = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void BeginProgress()
    {
        _progressMode = true;
        _progress = 0;
        _outcome = null;
        _progressDone = null;
        _progressHeight = Bounds.Height > 0 ? Math.Min(Bounds.Height, HeaderHeight + 110) : HeaderHeight + 110;
        _external = -1;
        _hover = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetProgress(double value)
    {
        _progress = Math.Clamp(value, 0, 1);
        InvalidateVisual();
    }

    /// <summary>Shows ✓ / ✕ for a moment, then fades and calls <paramref name="done"/>.</summary>
    public void CompleteProgress(PanelOutcome outcome, Action done)
    {
        _outcome = outcome;
        if (outcome == PanelOutcome.Succeeded)
            _progress = 1;
        _progressDone = done;
        InvalidateVisual();
        var hold = outcome == PanelOutcome.Succeeded ? TimeSpan.FromMilliseconds(900) : TimeSpan.FromMilliseconds(1400);
        DispatcherTimer.RunOnce(() => PlayCloseAnimation(() =>
        {
            var callback = _progressDone;
            LeaveProgressMode();
            callback?.Invoke();
        }), Motion.Enabled ? hold : TimeSpan.FromMilliseconds(300));
    }

    /// <summary>The job carries on in the toast: fade out now.</summary>
    public void HandOffProgress(Action done)
    {
        _progressDone = done;
        PlayCloseAnimation(() =>
        {
            LeaveProgressMode();
            done();
        });
    }

    // ───────── Drawing ─────────

    private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Regular = new(FontFamily.Default);

    private static readonly (string Key, Color A, Color B)[] Families =
    [
        ("pdf", Color.Parse("#FB7185"), Color.Parse("#E11D48")),
        ("image", Color.Parse("#38BDF8"), Color.Parse("#2563EB")),
        ("doc", Color.Parse("#818CF8"), Color.Parse("#4338CA")),
        ("sheet", Color.Parse("#34D399"), Color.Parse("#059669")),
        ("audio", Color.Parse("#C084FC"), Color.Parse("#9333EA")),
        ("video", Color.Parse("#F472B6"), Color.Parse("#DB2777")),
        ("archive", Color.Parse("#FBBF24"), Color.Parse("#D97706")),
        ("other", Color.Parse("#22D3EE"), Color.Parse("#0891B2")),
    ];

    /// <summary>Colour family of a tile from its format caption.</summary>
    internal static string FamilyOf(string? caption)
    {
        var c = (caption ?? "").ToUpperInvariant();
        if (c.Contains("PDF")) return "pdf";
        if (c is "PNG" or "JPG" or "JPEG" or "WEBP" or "AVIF" or "HEIC" or "GIF" or "BMP" or "TIFF" or "ICO" or "SVG" or "PSD") return "image";
        if (c is "XLSX" or "CSV" or "ODS" or "XLS") return "sheet";
        if (c is "MP3" or "WAV" or "FLAC" or "M4A" or "OGG" or "OPUS" or "AAC" or "ALAC") return "audio";
        if (c is "MP4" or "MKV" or "WEBM" or "MOV" or "GIF ANIMATION" or "AVI") return "video";
        if (c is "ZIP" or "7Z" or "TAR" or "RAR") return "archive";
        if (c is "DOCX" or "HWPX" or "ODT" or "RTF" or "TXT" or "MARKDOWN" or "HTML" or "EPUB" or "PPTX") return "doc";
        return "other";
    }

    private static string BadgeText(PanelItem item)
    {
        var caption = item.Caption ?? item.Label;
        var letters = new string(caption.Where(char.IsLetterOrDigit).ToArray());
        if (letters.Length == 0)
            return "•";
        // Wide scripts (Hangul, Han): two characters fit the badge.
        if (letters.Any(c => c > 0x2E7F))
            return letters[..Math.Min(2, letters.Length)];
        return letters.Length <= 4 ? letters.ToUpperInvariant() : letters[..3].ToUpperInvariant();
    }

    private static IBrush Gradient(string family)
    {
        var (_, a, b) = Families.First(f => f.Key == family);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
        };
    }

    private Rect? EditButtonFor(int index, int sub)
    {
        var cell = _cells.FirstOrDefault(c => !c.IsHeading && c.Index == index && c.Sub == sub);
        return cell.Bounds.Width > 0 ? new Rect(cell.Bounds.Right - 24, cell.Bounds.Top + 4, 20, 20) : null;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var card = new RoundedRect(new Rect(size).Deflate(0.5), 18);
        var foreground = Foreground ?? Brushes.White;
        var secondary = SecondaryForeground ?? Brushes.Gray;
        var accent = AccentBrush ?? Brushes.SlateBlue;
        context.DrawRectangle(Surface ?? Brushes.Black, new Pen(BorderBrush ?? Brushes.Gray, 1), card,
            new BoxShadows(new BoxShadow { OffsetY = 12, Blur = 36, Color = Color.FromArgb(90, 0, 0, 0) }));
        // A thin brand line along the top edge.
        context.DrawRectangle(accent, null, new RoundedRect(new Rect(24, 0.5, size.Width - 48, 2.5), 2));

        // Header: what was brought and the hint.
        var titleWidth = size.Width - 2 * Pad - 40;
        var title = Text(CenterTitle ?? "", Bold, 15, foreground, titleWidth);
        context.DrawText(title, new Point(Pad, Pad));
        if (!string.IsNullOrEmpty(CenterSubtitle))
            context.DrawText(Text(CenterSubtitle, Regular, 12, secondary, titleWidth), new Point(Pad, Pad + 22));

        if (_progressMode)
        {
            DrawProgress(context, size, foreground, secondary);
            return;
        }

        // Close button (drop here to cancel).
        if (!IsEditMode)
        {
            var close = CloseButton;
            var hot = _externalCloseHot;
            context.DrawRectangle(hot ? DangerBrush ?? Brushes.IndianRed : TileBrush, null, new RoundedRect(close, 9));
            var x = Text("✕", Bold, 12, hot ? Brushes.White : secondary, 28);
            context.DrawText(x, new Point(close.Center.X - x.Width / 2, close.Center.Y - x.Height / 2));
        }

        if (Items is not { Count: > 0 } items)
        {
            var body = new Rect(Pad, HeaderHeight, size.Width - 2 * Pad, TileHeight);
            context.DrawRectangle(null, new Pen(BorderBrush ?? Brushes.Gray, 1.2, new DashStyle([4, 3], 0)), new RoundedRect(body, 12));
            var message = Text(IsEditMode ? "+" : "…", Bold, 22, secondary, body.Width);
            context.DrawText(message, new Point(body.Center.X - message.Width / 2, body.Center.Y - message.Height / 2));
            return;
        }

        var tileIndex = 0;
        foreach (var cell in _cells)
        {
            if (cell.IsHeading)
            {
                var heading = items[cell.Index];
                var text = Text(heading.Label.ToUpper(CultureInfo.CurrentCulture), Bold, 11, secondary, cell.Bounds.Width);
                context.DrawText(text, new Point(cell.Bounds.X + 2, cell.Bounds.Y + 6));
                continue;
            }
            var item = cell.Sub >= 0 ? items[cell.Index].Children![cell.Sub] : items[cell.Index];
            if (IsEditMode && cell.Sub < 0 && _previewSlot == tileIndex)
                DrawInsertionBar(context, cell.Bounds, accent);
            if (cell.Sub < 0)
                tileIndex++;
            DrawTile(context, cell, item, foreground, secondary, accent);
        }
        if (IsEditMode && _previewSlot >= 0 && _previewSlot == tileIndex)
        {
            var last = _cells.LastOrDefault(c => !c.IsHeading && c.Sub < 0);
            if (last.Bounds.Width > 0)
                DrawInsertionBar(context, new Rect(last.Bounds.Right + Gap, last.Bounds.Y, TileWidth, TileHeight), accent);
        }
    }

    private void DrawInsertionBar(DrawingContext context, Rect before, IBrush accent) =>
        context.DrawRectangle(accent, null, new RoundedRect(new Rect(before.X - Gap / 2 - 1.5, before.Y + 6, 3, before.Height - 12), 2));

    private void DrawTile(DrawingContext context, Cell cell, PanelItem item, IBrush foreground, IBrush secondary, IBrush accent)
    {
        var lit = cell.Index == ActiveIndex && cell.Sub == ActiveSubIndex || IsEditMode && cell.Index == _dropTarget && cell.Sub < 0;
        var enabled = item.IsEnabled;
        using var opacity = context.PushOpacity(!enabled || (IsEditMode && cell.Index == _dimmed && cell.Sub < 0) ? 0.42 : 1);
        var rect = new RoundedRect(cell.Bounds, 14);
        if (lit && enabled)
            context.DrawRectangle(accent, null, rect,
                new BoxShadows(new BoxShadow { OffsetY = 6, Blur = 18, Color = Color.FromArgb(110, 99, 102, 241) }));
        else
            context.DrawRectangle(TileBrush, new Pen(BorderBrush ?? Brushes.Gray, 1), rect);

        // Format badge
        var family = FamilyOf(item.Caption);
        var badge = new Rect(cell.Bounds.Center.X - 17, cell.Bounds.Y + 11, 34, 34);
        context.DrawRectangle(lit && enabled ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : Gradient(family), null, new RoundedRect(badge, 10));
        var badgeText = Text(BadgeText(item), Bold, BadgeText(item).Length > 3 ? 9 : 10.5, Brushes.White, 34);
        context.DrawText(badgeText, new Point(badge.Center.X - badgeText.Width / 2, badge.Center.Y - badgeText.Height / 2));

        // Label
        var label = Text(item.Label, Bold, 12, lit && enabled ? Brushes.White : foreground, cell.Bounds.Width - 10, TextAlignment.Center, maxLines: 2);
        context.DrawText(label, new Point(cell.Bounds.X + 5, cell.Bounds.Y + 50));
        if (item.IsGroup)
            return;

        if (IsEditMode && cell.Index == _hover && cell.Sub == _hoverSub && EditButtonFor(cell.Index, cell.Sub) is { } edit)
        {
            context.DrawRectangle(Surface, new Pen(BorderBrush ?? Brushes.Gray, 1), new RoundedRect(edit, 6));
            var pencil = Text("✎", Regular, 11, foreground, 20);
            context.DrawText(pencil, new Point(edit.Center.X - pencil.Width / 2, edit.Center.Y - pencil.Height / 2));
        }
    }

    private void DrawProgress(DrawingContext context, Size size, IBrush foreground, IBrush secondary)
    {
        var bar = new Rect(Pad, HeaderHeight + 18, size.Width - 2 * Pad, 10);
        context.DrawRectangle(TileBrush, null, new RoundedRect(bar, 5));
        var fill = _outcome switch
        {
            PanelOutcome.Failed => DangerBrush,
            PanelOutcome.Cancelled => secondary,
            _ => AccentBrush,
        };
        if (_progress > 0)
            context.DrawRectangle(fill, null, new RoundedRect(bar.WithWidth(Math.Max(10, bar.Width * _progress)), 5));

        var status = _outcome switch
        {
            PanelOutcome.Succeeded => "✓",
            PanelOutcome.Failed => "✕",
            PanelOutcome.Cancelled => "–",
            _ => $"{Math.Round(_progress * 100)}%",
        };
        var statusBrush = _outcome switch
        {
            PanelOutcome.Succeeded => SuccessBrush ?? foreground,
            PanelOutcome.Failed => DangerBrush ?? foreground,
            _ => foreground,
        };
        var text = Text(status, Bold, _outcome is null ? 14 : 22, statusBrush, 120);
        context.DrawText(text, new Point(Pad, bar.Bottom + 12));

        _progressCancel = _outcome is null ? new Rect(size.Width - Pad - 28, bar.Bottom + 10, 28, 28) : default;
        if (_outcome is null)
        {
            context.DrawRectangle(TileBrush, null, new RoundedRect(_progressCancel, 9));
            var x = Text("✕", Bold, 12, secondary, 28);
            context.DrawText(x, new Point(_progressCancel.Center.X - x.Width / 2, _progressCancel.Center.Y - x.Height / 2));
        }
    }

    private static FormattedText Text(string text, Typeface typeface, double size, IBrush brush, double maxWidth,
        TextAlignment alignment = TextAlignment.Left, int maxLines = 1)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = maxLines,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };
        return formatted;
    }
}
