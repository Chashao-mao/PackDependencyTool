using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using SkiaSharp;

namespace PackDepTool.App.Controls;

/// <summary>
/// Native, GPU-drawn star map. Every position comes from the immutable
/// <see cref="GalaxyScene"/> layout; interaction only changes the camera or the
/// dragged node's coordinates, so hovering/zooming never re-runs layout.
/// </summary>
public sealed class StarGalaxyView : Control
{
    public static readonly StyledProperty<GalaxyScene?> SceneProperty =
        AvaloniaProperty.Register<StarGalaxyView, GalaxyScene?>(nameof(Scene));

    public static readonly StyledProperty<string?> SearchTextProperty =
        AvaloniaProperty.Register<StarGalaxyView, string?>(nameof(SearchText));

    public static readonly StyledProperty<string?> SelectedNodeIdProperty =
        AvaloniaProperty.Register<StarGalaxyView, string?>(nameof(SelectedNodeId),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public event Action<string>? NodeSelected;
    public event Action<string>? NodeActivated;

    private const float LabelFontSize = 10.5f;
    private const float HubLabelFontSize = 12f;
    private const double LabelZoomThreshold = 1.0;
    private const double MinScreenNodeRadius = 6.5;
    private const double MaxScreenNodeRadius = 34;

    private static readonly SKTypeface? UiTypeface =
        SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyle.Normal)
        ?? SKTypeface.FromFamilyName("Microsoft YaHei")
        ?? SKTypeface.FromFamilyName("Segoe UI");

    private static readonly SKTypeface? UiSemibold =
        SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyle.Bold)
        ?? UiTypeface;

    private static readonly SKFont NodeLabelFont = new(UiTypeface, LabelFontSize) { Subpixel = true };
    private static readonly SKFont HubLabelFont = new(UiSemibold, HubLabelFontSize) { Subpixel = true };
    private static readonly SKFont TooltipFont = new(UiTypeface, 11f) { Subpixel = true };
    private static readonly SKFont TooltipBoldFont = new(UiSemibold, 12f) { Subpixel = true };
    private static readonly SKFont GalaxyTitleFont = new(UiSemibold, 12f) { Subpixel = true };
    private static readonly SKFont LegendFont = new(UiTypeface, 10.5f) { Subpixel = true };
    private static readonly SKFont OverviewFont = new(UiSemibold, 9.5f) { Subpixel = true };
    private static readonly SKFont OverviewTitleFont = new(UiSemibold, 11f) { Subpixel = true };
    private static readonly SKFont DetailsTitleFont = new(UiSemibold, 13.5f) { Subpixel = true };
    private static readonly SKFont InitialFont = new(UiSemibold, 13f) { Subpixel = true };
    private static readonly SKPaint MeasurePaint = new() { IsAntialias = true };
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);
    private static readonly (double X, double Y, float Radius, byte Alpha)[] Starfield = CreateStarfield();

    private static (double X, double Y, float Radius, byte Alpha)[] CreateStarfield()
    {
        var random = new Random(20261121);
        var stars = new (double, double, float, byte)[150];
        for (var i = 0; i < stars.Length; i++)
        {
            stars[i] = (
                random.NextDouble(),
                random.NextDouble(),
                0.5f + (float)random.NextDouble() * 1.1f,
                (byte)(18 + random.Next(46)));
        }
        return stars;
    }

    private static readonly ConcurrentDictionary<(uint Color, bool Stroke, float Width, bool Dashed), SKPaint>
        PaintCache = new();

    private readonly GalaxyIconCache _icons = new();

    private StarGalaxyCamera _camera = new(1, 0, 0);
    private bool _cameraUserAdjusted;
    private GalaxyNodeVm? _hover;
    private GalaxyNodeVm? _dragNode;
    private Point _dragStartScreen;
    private Point _dragStartWorld;
    private LayoutRect _dragStartLabel;
    private bool _dragMoved;
    private bool _panning;
    private bool _panMoved;
    private Point _panLast;
    private Point _pointerScreen;
    private HashSet<string>? _searchMatches;
    private bool _hasSearch;
    private int _sceneVersion;
    private string? _pendingFocusNodeId;
    private bool _overviewCollapsed;
    private bool _unconnectedExpanded;
    private double _unconnectedScroll;
    private int _frameVersion;
    private const int UnconnectedRowHeight = 26;
    private const int UnconnectedVisibleRows = 10;

    private void Refresh()
    {
        _frameVersion++;
        InvalidateVisual();
    }

    public GalaxyScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public string? SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public string? SelectedNodeId
    {
        get => GetValue(SelectedNodeIdProperty);
        set => SetValue(SelectedNodeIdProperty, value);
    }

    public StarGalaxyView()
    {
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) =>
        {
            if (!_cameraUserAdjusted) FitToScene();
            Refresh();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty)
            OnSceneChanged();
        else if (change.Property == SearchTextProperty)
            ApplySearch();
        else if (change.Property == SelectedNodeIdProperty)
            OnSelectedNodeChanged();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var version = _sceneVersion;
        if (Scene is not null)
            _icons.Load(Scene, () => Dispatcher.UIThread.Post(() =>
            {
                if (version == _sceneVersion) Refresh();
            }));
        FitToScene();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _icons.Cancel();
        _icons.Clear();
        _dragNode = null;
        _panning = false;
        _hover = null;
        _sceneVersion++;
    }

    // ------------------------------------------------------------------ scene / camera

    private void OnSceneChanged()
    {
        _sceneVersion++;
        _icons.Cancel();
        _icons.Clear();
        _dragNode = null;
        _panning = false;
        _hover = null;
        _hasSearch = SearchText is { Length: > 0 };
        _searchMatches = null;

        if (Scene is null)
        {
            _camera = new StarGalaxyCamera(1, 0, 0);
            Refresh();
            return;
        }

        _cameraUserAdjusted = false;
        FitToScene();
        var pending = _pendingFocusNodeId;
        _pendingFocusNodeId = null;
        if (pending is not null && Scene.ById.TryGetValue(pending, out var focus))
        {
            CenterOn(focus, 1.05);
            _cameraUserAdjusted = true;
        }

        ApplySearch();
        var version = _sceneVersion;
        _icons.Load(Scene, () => Dispatcher.UIThread.Post(() =>
        {
            if (version == _sceneVersion) Refresh();
        }));
        Refresh();
    }

    private void OnSelectedNodeChanged()
    {
        // Selection must never reset the view (only search and double-click move the camera).
        Refresh();
    }

    private void FitToScene()
    {
        if (Scene is not { Bounds.Width: > 0, Bounds.Height: > 0 } scene) return;
        if (Bounds.Width <= 1 || Bounds.Height <= 1) return;
        _camera = StarGalaxyCamera.Fit(scene.Bounds.Inflate(30), Bounds.Width, Bounds.Height, 30);
        _cameraUserAdjusted = false;
        Refresh();
    }

    /// <summary>Fits the whole constellation. Also used by the offline PNG preview tool.</summary>
    public void FitAll() => FitToScene();

    /// <summary>Centers one node at a readable zoom. Also used by the offline PNG preview tool.</summary>
    public void FocusNode(string id, double zoom = 1.05)
    {
        if (Scene is null || !Scene.ById.TryGetValue(id, out var node)) return;
        CenterOn(node, zoom);
        Refresh();
    }

    /// <summary>Expands the unconnected-mod panel (preview / diagnostics).</summary>
    public void ExpandUnconnectedPanel()
    {
        if (Scene is null || Scene.Unconnected.Count == 0) return;
        _unconnectedExpanded = true;
        Refresh();
    }

    /// <summary>Sets an explicit camera zoom around the layout center (preview / diagnostics).</summary>
    public void SetCameraZoom(double zoom)
    {
        if (Scene is null) return;
        var clamped = Math.Clamp(zoom, StarGalaxyCamera.MinZoom, StarGalaxyCamera.MaxZoom);
        var cx = Scene.Bounds.X + Scene.Bounds.Width / 2;
        var cy = Scene.Bounds.Y + Scene.Bounds.Height / 2;
        _camera = new StarGalaxyCamera(clamped,
            Bounds.Width / 2 - cx * clamped,
            Bounds.Height / 2 - cy * clamped);
        _cameraUserAdjusted = true;
        Refresh();
    }

    private void CenterOn(GalaxyNodeVm node, double zoom)
    {
        _camera = new StarGalaxyCamera(
            Math.Clamp(zoom, StarGalaxyCamera.MinZoom, StarGalaxyCamera.MaxZoom),
            Bounds.Width / 2 - node.X * zoom,
            Bounds.Height / 2 - node.Y * zoom);
        _cameraUserAdjusted = true;
    }

    private void FocusGalaxy(int galaxyIndex)
    {
        if (Scene is null || galaxyIndex < 0 || galaxyIndex >= Scene.Galaxies.Count) return;
        ZoomToGalaxy(galaxyIndex);
        if (Scene.ById.TryGetValue(Scene.Galaxies[galaxyIndex].HubId, out var hub)) SelectNode(hub);
        Refresh();
    }

    private void ZoomToGalaxy(int galaxyIndex)
    {
        if (Scene is null || galaxyIndex < 0 || galaxyIndex >= Scene.Galaxies.Count) return;
        var galaxy = Scene.Galaxies[galaxyIndex];
        _camera = new StarGalaxyCamera(
            1.0,
            Bounds.Width / 2 - galaxy.CenterX,
            Bounds.Height / 2 - galaxy.CenterY);
        _cameraUserAdjusted = true;
    }

    private void ApplySearch()
    {
        var term = SearchText?.Trim() ?? string.Empty;
        if (term.Length == 0 || Scene is null)
        {
            _hasSearch = false;
            _searchMatches = null;
            Refresh();
            return;
        }

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        var hasSpatialMatch = false;
        foreach (var node in Scene.Nodes)
        {
            if (!Matches(node, term)) continue;
            hasSpatialMatch = true;
            matches.Add(node.Id);
            minX = Math.Min(minX, node.X - node.Radius);
            minY = Math.Min(minY, node.Y - node.Radius);
            maxX = Math.Max(maxX, node.X + node.Radius);
            maxY = Math.Max(maxY, node.Y + node.Radius);
        }

        var firstUnconnected = -1;
        for (var index = 0; index < Scene.Unconnected.Count; index++)
        {
            var node = Scene.Unconnected[index];
            if (!Matches(node, term)) continue;
            matches.Add(node.Id);
            if (firstUnconnected < 0) firstUnconnected = index;
        }

        _hasSearch = true;
        _searchMatches = matches;
        if (hasSpatialMatch && Bounds.Width > 1 && Bounds.Height > 1)
        {
            var bbox = LayoutRect.FromEdges(minX, minY, maxX, maxY).Inflate(140);
            _camera = StarGalaxyCamera.Fit(bbox, Bounds.Width, Bounds.Height, 60, 2.2);
            _cameraUserAdjusted = true;
        }
        if (firstUnconnected >= 0)
        {
            _unconnectedExpanded = true;
            var max = Math.Max(0, Scene.Unconnected.Count - UnconnectedVisibleRows);
            _unconnectedScroll = Math.Clamp(firstUnconnected - 2, 0, max);
        }
        Refresh();

        static bool Matches(GalaxyNodeVm node, string value) =>
            node.Title.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            node.Model.Id.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            node.Model.Name.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            (node.Model.NameZh?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // ------------------------------------------------------------------ interaction

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        _pointerScreen = point;
        var current = e.GetCurrentPoint(this);
        var left = current.Properties.IsLeftButtonPressed;
        var middle = current.Properties.IsMiddleButtonPressed;

        if (left && HandleUnconnectedClick(point)) return;
        if (left && HandleSelectionCardClick(point)) return;
        if (left && HandleOverviewClick(point)) return;

        if (left && HitTest(point) is { } node)
        {
            if (e.ClickCount >= 2)
            {
                SelectNode(node);
                _pendingFocusNodeId = node.Id;
                NodeActivated?.Invoke(node.Id);
                e.Pointer.Capture(null);
                return;
            }

            _dragNode = node;
            _dragStartScreen = point;
            _dragStartWorld = new Point(node.X, node.Y);
            _dragStartLabel = node.LabelRect;
            _dragMoved = false;
            e.Pointer.Capture(this);
            return;
        }

        if (left || middle)
        {
            _panning = true;
            _panMoved = false;
            _panLast = point;
            Cursor = SizeAllCursor;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        _pointerScreen = point;

        if (_dragNode is null && !_panning)
        {
            var panel = UnconnectedPanelRect();
            if (panel.Width > 0 && panel.Contains(point))
            {
                if (_hover is not null)
                {
                    _hover = null;
                    Refresh();
                }
                Cursor = ArrowCursor;
                return;
            }
        }

        if (_dragNode is not null)
        {
            var delta = point - _dragStartScreen;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 2) _dragMoved = true;
            var worldDelta = new Point(delta.X / _camera.Zoom, delta.Y / _camera.Zoom);
            _dragNode.X = _dragStartWorld.X + worldDelta.X;
            _dragNode.Y = _dragStartWorld.Y + worldDelta.Y;
            _dragNode.LabelRect = new LayoutRect(
                _dragStartLabel.X + worldDelta.X,
                _dragStartLabel.Y + worldDelta.Y,
                _dragStartLabel.Width,
                _dragStartLabel.Height);
            Refresh();
            return;
        }

        if (_panning)
        {
            var delta = point - _panLast;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 2) _panMoved = true;
            _panLast = point;
            _camera = _camera.Panned(delta.X, delta.Y);
            _cameraUserAdjusted = true;
            Refresh();
            return;
        }

        var hover = HitTest(point);
        if (ReferenceEquals(hover, _hover)) return;
        _hover = hover;
        Cursor = hover is null ? ArrowCursor : HandCursor;
        Refresh();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var wasPanning = _panning;
        _panning = false;
        Cursor = ArrowCursor;
        var node = _dragNode;
        _dragNode = null;
        e.Pointer.Capture(null);
        if (node is null)
        {
            if (wasPanning && !_panMoved)
            {
                SetCurrentValue(SelectedNodeIdProperty, null);
                Refresh();
            }
            return;
        }
        if (!_dragMoved)
        {
            SelectNode(node);
            // Encyclopedia-like drill-down: a hub click at overview scale opens
            // its local relationship map, while leaf clicks only select.
            if (node.IsHub && _camera.Zoom < 0.5) ZoomToGalaxy(node.GalaxyIndex);
        }
        Refresh();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        Cursor = ArrowCursor;
        Refresh();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        if (_unconnectedExpanded && Scene is not null)
        {
            var panel = UnconnectedPanelRect();
            if (panel.Contains(point))
            {
                var max = Math.Max(0, Scene.Unconnected.Count - UnconnectedVisibleRows);
                _unconnectedScroll = Math.Clamp(
                    _unconnectedScroll - Math.Sign(e.Delta.Y), 0, max);
                Refresh();
                e.Handled = true;
                return;
            }
        }
        var factor = e.Delta.Y > 0 ? 1.18 : 1 / 1.18;
        _camera = _camera.ZoomAt(point.X, point.Y, factor);
        _cameraUserAdjusted = true;
        Refresh();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.D0 or Key.NumPad0 or Key.F)
        {
            FitToScene();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SetCurrentValue(SelectedNodeIdProperty, null);
            Refresh();
            e.Handled = true;
        }
    }

    private void SelectNode(GalaxyNodeVm node)
    {
        SetCurrentValue(SelectedNodeIdProperty, node.Id);
        NodeSelected?.Invoke(node.Id);
    }

    private GalaxyNodeVm? HitTest(Point point)
    {
        var scene = Scene;
        if (scene is null) return null;
        for (var index = scene.Nodes.Count - 1; index >= 0; index--)
        {
            var node = scene.Nodes[index];
            var screen = ToScreen(node);
            var radius = ScreenRadius(node);
            var hitRadius = Math.Max(radius + 4, 12);
            var dx = point.X - screen.X;
            var dy = point.Y - screen.Y;
            if (dx * dx + dy * dy <= hitRadius * hitRadius) return node;

            if (_camera.Zoom < LabelZoomThreshold || !node.ShowLabel) continue;
            if (LabelScreenRect(node).Contains(point)) return node;
        }
        return null;
    }

    // ------------------------------------------------------------------ unconnected panel

    private Rect UnconnectedPanelRect()
    {
        if (Scene is null || Scene.Unconnected.Count == 0) return default;
        const double width = 300;
        var x = Math.Max(8, Bounds.Width - width - 12);
        if (!_unconnectedExpanded) return new Rect(x, Bounds.Height - 48, width, 34);

        var rows = Math.Min(Scene.Unconnected.Count, UnconnectedVisibleRows);
        var height = 44 + rows * UnconnectedRowHeight;
        return new Rect(x, Math.Max(8, Bounds.Height - height - 14), width, height);
    }

    private bool HandleUnconnectedClick(Point point)
    {
        if (Scene is null || Scene.Unconnected.Count == 0) return false;
        var panel = UnconnectedPanelRect();
        if (!panel.Contains(point)) return false;

        if (!_unconnectedExpanded)
        {
            _unconnectedExpanded = true;
            Refresh();
            return true;
        }

        if (point.Y < panel.Y + 36)
        {
            _unconnectedExpanded = false;
            _unconnectedScroll = 0;
            Refresh();
            return true;
        }

        var start = (int)_unconnectedScroll;
        var row = (int)((point.Y - (panel.Y + 40)) / UnconnectedRowHeight);
        var index = start + row;
        if (index >= 0 && index < Scene.Unconnected.Count)
        {
            var id = Scene.Unconnected[index].Id;
            SetCurrentValue(SelectedNodeIdProperty, id);
            NodeSelected?.Invoke(id);
            Refresh();
        }
        return true;
    }

    private void DrawUnconnectedPanel(SKCanvas canvas, GalaxyFrameState frame, GalaxyScene scene)
    {
        if (scene.Unconnected.Count == 0) return;
        var panel = UnconnectedPanelRect();
        if (panel.Width <= 0) return;

        canvas.DrawRoundRect(ToSk(panel), 9, 9, Paint(0xF2161B26, false));
        canvas.DrawRoundRect(ToSk(panel), 9, 9, Paint(0xFF334155, true, 1f));

        var x = (float)panel.X + 12;
        DrawScreenText(canvas, $"未连接模组 · {scene.Unconnected.Count}",
            x, (float)panel.Y + 21, SKTextAlign.Left, OverviewTitleFont,
            Paint(0xFFCBD5E1, false));
        DrawScreenText(canvas, _unconnectedExpanded ? "收起 ▴" : "展开 ▸",
            (float)panel.Right - 12, (float)panel.Y + 21, SKTextAlign.Right,
            OverviewFont, Paint(0xFF94A3B8, false));

        if (!_unconnectedExpanded) return;

        var rowTop = (float)panel.Y + 40;
        canvas.Save();
        canvas.ClipRect(new SKRect((float)panel.X + 1, rowTop,
            (float)panel.Right - 1, (float)panel.Bottom - 2));
        var start = (int)_unconnectedScroll;
        var visible = Math.Min(UnconnectedVisibleRows, scene.Unconnected.Count - start);
        for (var i = 0; i < visible; i++)
        {
            var index = start + i;
            var node = scene.Unconnected[index];
            var rowY = rowTop + i * UnconnectedRowHeight;
            var isMatch = frame.HasSearch && frame.SearchMatches?.Contains(node.Id) == true;
            if (isMatch)
                canvas.DrawRoundRect(new SKRect((float)panel.X + 6, rowY,
                        (float)panel.Right - 6, rowY + UnconnectedRowHeight - 2),
                    4, 4, Paint(0x331D4ED8, false));
            else if (string.Equals(node.Id, frame.SelectedId, StringComparison.OrdinalIgnoreCase))
                canvas.DrawRoundRect(new SKRect((float)panel.X + 6, rowY,
                        (float)panel.Right - 6, rowY + UnconnectedRowHeight - 2),
                    4, 4, Paint(0x33E9EFF8, false));

            var icon = index < frame.UnconnectedIcons.Length ? frame.UnconnectedIcons[index] : null;
            var iconRect = new SKRect(x, rowY + 4, x + 18, rowY + 22);
            if (icon is not null)
            {
                canvas.Save();
                using var clip = new SKPath();
                clip.AddRoundRect(new SKRect(iconRect.Left, iconRect.Top,
                    iconRect.Right, iconRect.Bottom), 4, 4);
                canvas.ClipPath(clip, SKClipOperation.Intersect, true);
                canvas.DrawImage(icon, iconRect);
                canvas.Restore();
            }
            else
            {
                canvas.DrawRoundRect(iconRect, 4, 4,
                    Paint(WithAlpha(ToArgb(node.LoaderColor), 220), false));
            }

            var text = Ellipsize(node.Title, 10f, (float)panel.Width - 76);
            DrawScreenText(canvas, text, x + 26, rowY + 16, SKTextAlign.Left,
                LegendFont, Paint(0xFFE2E8F0, false));
            canvas.DrawCircle((float)panel.Right - 16, rowY + 13, 3f,
                Paint(ToArgb(node.StatusColor), false));
        }
        canvas.Restore();
    }

    // ------------------------------------------------------------------ selection details

    private sealed class SelectionDetails
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public required string StatusText { get; init; }
        public required uint StatusColor { get; init; }
        public required int Requires { get; init; }
        public required int Optional { get; init; }
        public required int Incompatible { get; init; }
        public required IReadOnlyList<DetailRow> Rows { get; init; }
    }

    private sealed record DetailRow(string Id, string Title, string Kind, uint Color);

    private static SelectionDetails? BuildSelectionDetails(GalaxyScene? scene, string? selectedId)
    {
        if (scene is null || selectedId is null || !scene.ById.TryGetValue(selectedId, out var node))
            return null;

        var requires = 0;
        var optional = 0;
        var incompatible = 0;
        var related = new List<(int Priority, GalaxyEdgeVm Edge, GalaxyNodeVm Other)>();
        foreach (var edge in scene.DetailEdges)
        {
            GalaxyNodeVm other;
            if (string.Equals(edge.From.Id, node.Id, StringComparison.OrdinalIgnoreCase))
                other = edge.To;
            else if (string.Equals(edge.To.Id, node.Id, StringComparison.OrdinalIgnoreCase))
                other = edge.From;
            else
                continue;

            switch (edge.Type)
            {
                case EdgeType.Requires:
                    requires++;
                    break;
                case EdgeType.Optional:
                case EdgeType.Recommends:
                    optional++;
                    break;
                case EdgeType.Incompatible:
                    incompatible++;
                    break;
            }

            var priority = edge.Type switch
            {
                EdgeType.Requires => 0,
                EdgeType.Incompatible => 1,
                EdgeType.Optional => 2,
                EdgeType.Recommends => 3,
                EdgeType.Embedded => 4,
                _ => 5,
            };
            related.Add((priority, edge, other));
        }

        var rows = related
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Other.Title, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DetailRow(item.Other.Id, item.Other.Title,
                GraphPalette.EdgeText(item.Edge.Type), ToArgb(item.Edge.Color)))
            .ToList();

        return new SelectionDetails
        {
            Id = node.Id,
            Title = node.Title,
            Subtitle = $"{node.Model.Id} · {node.Model.DisplayVersion} · " +
                       $"{GraphPalette.LoaderText(node.Model.Loader)}",
            StatusText = GraphPalette.StatusText(node.Model.Status),
            StatusColor = ToArgb(node.StatusColor),
            Requires = requires,
            Optional = optional,
            Incompatible = incompatible,
            Rows = rows,
        };
    }

    private static Rect SelectionPanelRect(Rect bounds, int rows)
    {
        var width = Math.Min(252, Math.Max(180, bounds.Width - 24));
        var visibleRows = Math.Min(rows, 7);
        var height = 114.0 + visibleRows * 20 + (rows > 7 ? 18 : 0);
        var x = Math.Max(8, bounds.Width - width - 12);
        const double y = 214;
        if (y + height > bounds.Height - 24)
            height = Math.Max(120, bounds.Height - y - 24);
        return new Rect(x, y, width, height);
    }

    private static SKRect ToSk(Rect rect) =>
        new((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);

    private bool HandleSelectionCardClick(Point point)
    {
        var details = BuildSelectionDetails(Scene, SelectedNodeId);
        if (details is null) return false;
        var panel = SelectionPanelRect(new Rect(Bounds.Size), details.Rows.Count);
        if (!panel.Contains(point)) return false;

        var rowTop = panel.Y + 92;
        for (var i = 0; i < Math.Min(details.Rows.Count, 7); i++)
        {
            var row = new Rect(panel.X + 8, rowTop + i * 20, panel.Width - 16, 18);
            if (!row.Contains(point)) continue;
            SetCurrentValue(SelectedNodeIdProperty, details.Rows[i].Id);
            NodeSelected?.Invoke(details.Rows[i].Id);
            FocusNodeInView(details.Rows[i].Id);
            Refresh();
            return true;
        }
        return true;
    }

    private void FocusNodeInView(string id)
    {
        if (Scene is null || !Scene.ById.TryGetValue(id, out var node)) return;
        var screen = ToScreen(node);
        if (screen.X >= 80 && screen.Y >= 80 && screen.X <= Bounds.Width - 80 &&
            screen.Y <= Bounds.Height - 80) return;
        _camera = new StarGalaxyCamera(
            _camera.Zoom,
            Bounds.Width / 2 - node.X * _camera.Zoom,
            Bounds.Height / 2 - node.Y * _camera.Zoom);
        _cameraUserAdjusted = true;
    }

    private void DrawSelectionCard(SKCanvas canvas, GalaxyFrameState frame)
    {
        var details = frame.SelectedDetails;
        if (details is null) return;
        var panel = SelectionPanelRect(frame.Bounds, details.Rows.Count);
        canvas.DrawRoundRect(ToSk(panel), 10, 10, Paint(0xF2161B26, false));
        canvas.DrawRoundRect(ToSk(panel), 10, 10, Paint(0xFF334155, true, 1f));

        var x = (float)panel.X + 12;
        var y = (float)panel.Y + 20;
        var title = Ellipsize(details.Title, DetailsTitleFont, (float)panel.Width - 24);
        DrawScreenText(canvas, title, x, y, SKTextAlign.Left, DetailsTitleFont,
            Paint(0xFFF1F5F9, false));
        DrawScreenText(canvas, Ellipsize(details.Subtitle, 10f, (float)panel.Width - 24),
            x, y + 16, SKTextAlign.Left, LegendFont, Paint(0xFF94A3B8, false));
        canvas.DrawCircle(x + 4, y + 31, 3.5f, Paint(details.StatusColor, false));
        DrawScreenText(canvas, details.StatusText, x + 12, y + 34, SKTextAlign.Left,
            OverviewFont, Paint(WithAlpha(details.StatusColor, 235), false));
        canvas.DrawLine(x, y + 43, (float)panel.Right - 12, y + 43,
            Paint(0xFF273043, true, 1f));

        var counts = details.Requires + details.Optional + details.Incompatible == 0
            ? "没有记录到关系"
            : $"{details.Requires} 必需 · {details.Optional} 可选 · {details.Incompatible} 冲突";
        DrawScreenText(canvas, counts, x, y + 59, SKTextAlign.Left, LegendFont,
            Paint(0xFFCBD5E1, false));

        var rowTop = (float)panel.Y + 92;
        var visibleRows = Math.Min(details.Rows.Count, 7);
        for (var i = 0; i < visibleRows; i++)
        {
            var row = details.Rows[i];
            var rowY = rowTop + i * 20;
            canvas.DrawCircle(x + 4, rowY + 9, 3f, Paint(row.Color, false));
            var maxNameWidth = (float)panel.Width - 86;
            DrawScreenText(canvas, Ellipsize(row.Title, 10f, maxNameWidth),
                x + 12, rowY + 12, SKTextAlign.Left, LegendFont, Paint(0xFFE2E8F0, false));
            DrawScreenText(canvas, row.Kind, (float)panel.Right - 12, rowY + 12,
                SKTextAlign.Right, OverviewFont, Paint(0xFF64748B, false));
        }

        if (details.Rows.Count > 7)
            DrawScreenText(canvas, $"还有 {details.Rows.Count - 7} 条关系…",
                x, rowTop + 7 * 20 + 11, SKTextAlign.Left, OverviewFont,
                Paint(0xFF64748B, false));

        DrawScreenText(canvas, "点击关系跳到该模组 · 双击节点重生成",
            x, (float)panel.Bottom - 9, SKTextAlign.Left, OverviewFont,
            Paint(0xFF475569, false));
    }

    private const float DetailsTitleFontSize = 13.5f;

    // ------------------------------------------------------------------ overview panel

    private readonly record struct OverviewLayout(Rect Panel, Point[] Points, bool Collapsed);

    private OverviewLayout GetOverview()
    {
        var collapsed = _overviewCollapsed;
        var panel = new Rect(Math.Max(8, Bounds.Width - 252), 14, 238, collapsed ? 36 : 190);
        var count = Scene?.Galaxies.Count ?? 0;
        var points = new Point[count];
        var center = new Point(panel.X + panel.Width / 2, panel.Y + (collapsed ? 18 : 106));
        const double radiusX = 84;
        const double radiusY = 52;
        for (var i = 0; i < count; i++)
        {
            var angle = -Math.PI / 2 + 2 * Math.PI * i / Math.Max(1, count);
            points[i] = count <= 1
                ? center
                : new Point(center.X + Math.Cos(angle) * radiusX, center.Y + Math.Sin(angle) * radiusY);
        }
        return new OverviewLayout(panel, points, collapsed);
    }

    private bool HandleOverviewClick(Point point)
    {
        if (Scene is null || Bounds.Width <= 1) return false;
        var overview = GetOverview();
        if (!overview.Panel.Contains(point)) return false;

        var header = new Rect(overview.Panel.X, overview.Panel.Y, overview.Panel.Width, 32);
        if (header.Contains(point) || overview.Collapsed)
        {
            _overviewCollapsed = !_overviewCollapsed;
            Refresh();
            return true;
        }

        for (var i = 0; i < overview.Points.Length; i++)
        {
            var dx = point.X - overview.Points[i].X;
            var dy = point.Y - overview.Points[i].Y;
            if (dx * dx + dy * dy > 12 * 12) continue;
            FocusGalaxy(i);
            return true;
        }
        return true;
    }

    // ------------------------------------------------------------------ rendering

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var scene = Scene;
        var selectedId = SelectedNodeId;
        var frame = new GalaxyFrameState
        {
            Bounds = new Rect(Bounds.Size),
            Scene = scene,
            Camera = _camera,
            HoverId = _hover?.Id,
            SelectedId = selectedId,
            SearchMatches = _searchMatches,
            HasSearch = _hasSearch,
            Overview = scene is null ? default : GetOverview(),
            Icons = BuildIconSnapshot(scene),
            UnconnectedIcons = BuildUnconnectedIconSnapshot(scene),
            Neighbors = BuildNeighbors(scene, selectedId ?? _hover?.Id),
            SelectedGalaxy = GetSelectedGalaxy(scene, selectedId),
            SelectedDetails = BuildSelectionDetails(scene, selectedId),
            Pointer = _pointerScreen,
            Version = _frameVersion,
        };
        context.Custom(new GalaxyDrawOperation(this, frame));
    }

    private SKImage?[] BuildIconSnapshot(GalaxyScene? scene)
    {
        if (scene is null) return [];
        var icons = new SKImage?[scene.Nodes.Count];
        for (var i = 0; i < icons.Length; i++) icons[i] = _icons.TryGet(scene.Nodes[i].Id);
        return icons;
    }

    private SKImage?[] BuildUnconnectedIconSnapshot(GalaxyScene? scene)
    {
        if (scene is null) return [];
        var icons = new SKImage?[scene.Unconnected.Count];
        for (var i = 0; i < icons.Length; i++) icons[i] = _icons.TryGet(scene.Unconnected[i].Id);
        return icons;
    }

    private void DrawGalaxy(ImmediateDrawingContext context, GalaxyFrameState frame)
    {
        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
            return;
        using var lease = feature.Lease();
        DrawSkia(lease.SkCanvas, frame);
    }

    private void DrawSkia(SKCanvas canvas, GalaxyFrameState frame)
    {
        var width = (float)frame.Bounds.Width;
        var height = (float)frame.Bounds.Height;
        canvas.Save();

        // The custom-draw canvas already carries the window's DPI scale
        // (e.g. 1.5 at 150%). Bounds and the camera are logical units, so undo
        // that scale once; otherwise the map is zoomed 1.5x and clips.
        var renderScale = canvas.TotalMatrix.ScaleX;
        if (renderScale > 0.01f && Math.Abs(renderScale - 1f) > 0.01f)
            canvas.Scale(1f / renderScale, 1f / renderScale);

        canvas.ClipRect(new SKRect(0, 0, width, height));
        canvas.DrawRect(new SKRect(0, 0, width, height), Paint(0xFF0B0D12, false));
        DrawStarfield(canvas, width, height);

        if (frame.Scene is null || frame.Scene.IsEmpty)
        {
            DrawScreenText(canvas, "还没有可显示的星图数据", width / 2, height / 2 - 8,
                SKTextAlign.Center, GalaxyTitleFont, Paint(0xFF98A2B3, false));
            DrawScreenText(canvas, "加载整合包后会自动按前置枢纽生成星系", width / 2, height / 2 + 16,
                SKTextAlign.Center, LegendFont, Paint(0xFF64748B, false));
            canvas.Restore();
            return;
        }

        var scene = frame.Scene;
        var camera = frame.Camera;
        var zoom = Math.Max(0.05, camera.Zoom);

        DrawGalaxyBackbone(canvas, frame, scene, camera);
        DrawGalaxyDiscs(canvas, frame, scene, camera);
        DrawEdges(canvas, frame, scene, camera);
        DrawCrossEdges(canvas, frame, scene, camera, zoom);
        DrawNodes(canvas, frame, scene, camera, zoom);
        DrawLabels(canvas, frame, scene, camera, zoom);
        DrawTooltip(canvas, frame, scene);

        DrawHeader(canvas, frame, scene, camera);
        DrawOverview(canvas, frame, scene);
        DrawSelectionCard(canvas, frame);
        DrawUnconnectedPanel(canvas, frame, scene);
        DrawLegend(canvas, frame);
        DrawHint(canvas, frame);

        canvas.Restore();
    }

    private void DrawStarfield(SKCanvas canvas, float width, float height)
    {
        foreach (var star in Starfield)
        {
            canvas.DrawCircle((float)(star.X * width), (float)(star.Y * height), star.Radius,
                Paint(WithAlpha(0xFFE2E8F0, star.Alpha), false));
        }
    }

    private void DrawGalaxyDiscs(SKCanvas canvas, GalaxyFrameState frame, GalaxyScene scene,
        StarGalaxyCamera camera)
    {
        for (var i = 0; i < scene.Galaxies.Count; i++)
        {
            var galaxy = scene.Galaxies[i];
            var center = camera.WorldToScreen(galaxy.CenterX, galaxy.CenterY);
            var radius = (float)(galaxy.Radius * camera.Zoom);
            if (radius < 4) continue;

            var color = ToArgb(galaxy.Color);
            var selected = i == frame.SelectedGalaxy;
            // No heavy "bubble": a faint halo plus a dashed orbit keeps the
            // groups legible without visually cutting them apart.
            canvas.DrawCircle((float)center.X, (float)center.Y, radius,
                Paint(WithAlpha(color, selected ? (byte)12 : (byte)4), false));
            if (selected)
                canvas.DrawCircle((float)center.X, (float)center.Y, radius,
                    Paint(WithAlpha(color, 120), true, 1.4f));

            if (galaxy.IsIsolated)
            {
                var captionY = Math.Max(16, (float)(center.Y - radius - 8));
                DrawScreenText(canvas, $"未连接 · {galaxy.NodeCount}",
                    (float)center.X, captionY,
                    SKTextAlign.Center, GalaxyTitleFont,
                    Paint(0xFF94A3B8, false));
            }
            else if (camera.Zoom >= 0.5)
            {
                var title = $"{galaxy.ShortTitle} · {galaxy.NodeCount} 个模组";
                DrawScreenText(canvas, title, (float)center.X, (float)(center.Y - radius - 8),
                    SKTextAlign.Center, GalaxyTitleFont,
                    Paint(WithAlpha(color, selected ? (byte)245 : (byte)200), false));
            }
        }
    }

    private void DrawGalaxyBackbone(SKCanvas canvas, GalaxyFrameState frame, GalaxyScene scene,
        StarGalaxyCamera camera)
    {
        // While inspecting a node, its own cross-galaxy edges are highlighted;
        // the global backbone would only add long chords through the viewport.
        if (frame.SelectedId is not null) return;

        foreach (var link in scene.CrossLinks)
        {
            if (link.FromGalaxy >= scene.Galaxies.Count || link.ToGalaxy >= scene.Galaxies.Count)
                continue;
            var fromGalaxy = scene.Galaxies[link.FromGalaxy];
            var toGalaxy = scene.Galaxies[link.ToGalaxy];
            var from = camera.WorldToScreen(fromGalaxy.CenterX, fromGalaxy.CenterY);
            var to = camera.WorldToScreen(toGalaxy.CenterX, toGalaxy.CenterY);
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var bend = Math.Min(54, 18 + length * 0.06);
            var control = new Point(
                (from.X + to.X) / 2 - dy / length * bend,
                (from.Y + to.Y) / 2 + dx / length * bend);

            var highlighted = frame.SelectedGalaxy == link.FromGalaxy ||
                              frame.SelectedGalaxy == link.ToGalaxy;
            var alpha = (byte)Math.Clamp(
                (highlighted ? 120 : 46) + Math.Min(58, link.Count * 4), 46, 180);
            var color = link.RequiresCount > 0 ? 0xFF3B82F6 : 0xFF64748B;
            var width = Math.Min(3.4f, 0.8f + (float)Math.Log2(link.Count + 1) * 0.6f);
            using var path = new SKPath();
            path.MoveTo((float)from.X, (float)from.Y);
            path.QuadTo((float)control.X, (float)control.Y, (float)to.X, (float)to.Y);
            canvas.DrawPath(path, Paint(WithAlpha(color, alpha), true, width));
        }
    }

    private void DrawEdges(SKCanvas canvas, GalaxyFrameState frame, GalaxyScene scene,
        StarGalaxyCamera camera)
    {
        using var requires = new SKPath();
        using var optional = new SKPath();
        using var embedded = new SKPath();
        using var manual = new SKPath();
        var view = new SKRect((float)frame.Bounds.X - 40, (float)frame.Bounds.Y - 40,
            (float)frame.Bounds.Right + 40, (float)frame.Bounds.Bottom + 40);
        var overview = camera.Zoom < 0.32;

        foreach (var edge in scene.Edges)
        {
            if (edge.CrossGalaxy) continue;
            var selected = IsEndpointSelected(frame, edge);
            if (edge.Type == EdgeType.Incompatible)
            {
                if (!selected) continue;
            }
            else
            {
                // At overview scale only solid requirements are worth drawing;
                // optional/recommended/embedded dashes turn the map into noise.
                if (overview && !selected && edge.Type != EdgeType.Requires) continue;
                if (!selected && IsSegmentVisible(edge, camera, view) is false) continue;
            }

            var path = edge.Manual
                ? manual
                : edge.Type switch
                {
                    EdgeType.Requires => requires,
                    EdgeType.Embedded => embedded,
                    _ => optional,
                };
            var from = camera.WorldToScreen(edge.From.X, edge.From.Y);
            var to = camera.WorldToScreen(edge.To.X, edge.To.Y);
            path.MoveTo((float)from.X, (float)from.Y);
            path.LineTo((float)to.X, (float)to.Y);
        }

        var requiresAlpha = overview ? 0x8C3B82F6u : 0xCC3B82F6u;
        var requiresWidth = overview ? 0.9f : 1.2f;
        canvas.DrawPath(requires, Paint(requiresAlpha, true, requiresWidth));
        canvas.DrawPath(optional, Paint(0xB394A3B8, true, 0.9f, dashed: true));
        canvas.DrawPath(embedded, Paint(0xCCA78BFA, true, 1.0f));
        canvas.DrawPath(manual, Paint(0xCCF5C542, true, 1.0f, dashed: true));

        var activeId = frame.SelectedId ?? frame.HoverId;
        if (activeId is null) return;
        foreach (var edge in scene.Edges)
        {
            if (edge.CrossGalaxy || edge.Type == EdgeType.Incompatible) continue;
            if (!string.Equals(edge.From.Id, activeId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(edge.To.Id, activeId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsSegmentVisible(edge, camera, view)) continue;
            var from = camera.WorldToScreen(edge.From.X, edge.From.Y);
            var to = camera.WorldToScreen(edge.To.X, edge.To.Y);
            canvas.DrawLine((float)from.X, (float)from.Y, (float)to.X, (float)to.Y,
                Paint(WithAlpha(ToArgb(edge.Color), 235), true, 2.2f));
        }
    }

    private static bool IsEndpointSelected(GalaxyFrameState frame, GalaxyEdgeVm edge) =>
        string.Equals(edge.From.Id, frame.SelectedId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(edge.To.Id, frame.SelectedId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(edge.From.Id, frame.HoverId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(edge.To.Id, frame.HoverId, StringComparison.OrdinalIgnoreCase);

    private static bool IsSegmentVisible(GalaxyEdgeVm edge, StarGalaxyCamera camera, SKRect view)
    {
        var from = camera.WorldToScreen(edge.From.X, edge.From.Y);
        var to = camera.WorldToScreen(edge.To.X, edge.To.Y);
        return (from.X >= view.Left || to.X >= view.Left) &&
               (from.X <= view.Right || to.X <= view.Right) &&
               (from.Y >= view.Top || to.Y >= view.Top) &&
               (from.Y <= view.Bottom || to.Y <= view.Bottom);
    }

    private void DrawCrossEdges(SKCanvas canvas, GalaxyFrameState frame, GalaxyScene scene,
        StarGalaxyCamera camera, double zoom)
    {
        var view = new SKRect((float)frame.Bounds.X - 40, (float)frame.Bounds.Y - 40,
            (float)frame.Bounds.Right + 40, (float)frame.Bounds.Bottom + 40);
        if (zoom >= 0.55 && frame.SelectedId is null && frame.HoverId is null)
        {
            using var requires = new SKPath();
            using var other = new SKPath();
            foreach (var edge in scene.Edges)
            {
                if (!edge.CrossGalaxy || edge.Type == EdgeType.Incompatible) continue;
                if (!IsSegmentVisible(edge, camera, view)) continue;
                var start = camera.WorldToScreen(edge.From.X, edge.From.Y);
                var end = camera.WorldToScreen(edge.To.X, edge.To.Y);
                var path = edge.Type == EdgeType.Requires ? requires : other;
                path.MoveTo((float)start.X, (float)start.Y);
                path.LineTo((float)end.X, (float)end.Y);
            }
            canvas.DrawPath(requires, Paint(0x553B82F6, true, 0.8f));
            canvas.DrawPath(other, Paint(0x4464748B, true, 0.7f, dashed: true));
        }

        var selectedId = frame.SelectedId ?? frame.HoverId;
        if (selectedId is null) return;
        var selected = scene.ById.GetValueOrDefault(selectedId);
        if (selected is null) return;
        var from = camera.WorldToScreen(selected.X, selected.Y);
        foreach (var edge in scene.Edges)
        {
            if (!edge.CrossGalaxy) continue;
            if (!string.Equals(edge.From.Id, selectedId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(edge.To.Id, selectedId, StringComparison.OrdinalIgnoreCase)) continue;
            var other = string.Equals(edge.From.Id, selectedId, StringComparison.OrdinalIgnoreCase)
                ? edge.To
                : edge.From;
            var to = camera.WorldToScreen(other.X, other.Y);
            var color = ToArgb(edge.Color);
            canvas.DrawLine((float)from.X, (float)from.Y, (float)to.X, (float)to.Y,
                Paint(WithAlpha(color, 170), true, 1.2f, dashed: true));
            var radius = ScreenRadius(other, zoom) + 4;
            canvas.DrawCircle((float)to.X, (float)to.Y, radius,
                Paint(WithAlpha(color, 210), true, 1.5f));
        }
    }

    private void DrawNodes(SKCanvas canvas, GalaxyFrameState frame, GalaxyScene scene,
        StarGalaxyCamera camera, double zoom)
    {
        var view = new SKRect((float)frame.Bounds.X - 50, (float)frame.Bounds.Y - 50,
            (float)frame.Bounds.Right + 50, (float)frame.Bounds.Bottom + 50);
        for (var index = 0; index < scene.Nodes.Count; index++)
        {
            var node = scene.Nodes[index];
            var screen = camera.WorldToScreen(node.X, node.Y);
            var radius = ScreenRadius(node, zoom);
            if (!CircleVisible(screen, radius, view)) continue;

            var searchDim = frame.HasSearch &&
                            (frame.SearchMatches is null || !frame.SearchMatches.Contains(node.Id));
            var focusId = frame.SelectedId ?? frame.HoverId;
            var focusDim = focusId is not null &&
                           !string.Equals(node.Id, focusId, StringComparison.OrdinalIgnoreCase) &&
                           !frame.Neighbors.Contains(node.Id);
            var dim = searchDim || focusDim;
            var fill = Paint(dim ? WithAlpha(ToArgb(node.FillColor), 60) : ToArgb(node.FillColor), false);
            var border = Paint(dim ? WithAlpha(ToArgb(node.BorderColor), 70) : ToArgb(node.BorderColor),
                true, node.IsHub ? 2.2f : 1.4f);

            if (node.IsHub && !node.IsIsolatedGalaxy)
                canvas.DrawCircle((float)screen.X, (float)screen.Y, (float)(radius + 4),
                    Paint(dim ? 0x1AF5C542u : 0x33F5C542u, true, 2f));

            canvas.DrawCircle((float)screen.X, (float)screen.Y, (float)radius, fill);
            canvas.DrawCircle((float)screen.X, (float)screen.Y, (float)radius, border);

            var innerRadius = Math.Max(2, radius - (node.IsHub ? 4 : 3));
            var icon = index < frame.Icons.Length ? frame.Icons[index] : null;
            if (icon is not null)
            {
                canvas.Save();
                using var clip = new SKPath();
                clip.AddCircle((float)screen.X, (float)screen.Y, (float)innerRadius);
                canvas.ClipPath(clip, SKClipOperation.Intersect, true);
                var dest = new SKRect((float)screen.X - (float)innerRadius,
                    (float)screen.Y - (float)innerRadius,
                    (float)screen.X + (float)innerRadius,
                    (float)screen.Y + (float)innerRadius);
                canvas.DrawImage(icon, dest);
                canvas.Restore();
            }
            else
            {
                // No icon bytes (or still decoding): never leave an empty hole.
                // A loader-colored core stays visible at any zoom; larger nodes
                // additionally show the M/CJK initial.
                canvas.DrawCircle((float)screen.X, (float)screen.Y, (float)(innerRadius * 0.62),
                    Paint(WithAlpha(ToArgb(node.LoaderColor), dim ? (byte)100 : (byte)225), false));
                if (radius >= 9)
                {
                    var initial = FirstGlyph(node.Title);
                    DrawScreenText(canvas, initial, (float)screen.X,
                        (float)(screen.Y + radius * 0.36), SKTextAlign.Center, InitialFont,
                        Paint(dim ? 0x99FFFFFF : 0xFF10141C, false));
                }
            }

            if (radius >= 9)
            {
                var dot = Math.Max(2.5f, (float)radius * 0.2f);
                var dotCenter = new SKPoint((float)(screen.X + radius * 0.68), (float)(screen.Y - radius * 0.68));
                canvas.DrawCircle(dotCenter, dot + 1.5f, Paint(0xFF10141C, false));
                canvas.DrawCircle(dotCenter, dot,
                    Paint(dim ? WithAlpha(ToArgb(node.StatusColor), 90) : ToArgb(node.StatusColor), false));
            }

            if (string.Equals(node.Id, frame.SelectedId, StringComparison.OrdinalIgnoreCase))
                canvas.DrawCircle((float)screen.X, (float)screen.Y, (float)(radius + 5),
                    Paint(0xFFE9EFF8, true, 2f));
            else if (string.Equals(node.Id, frame.HoverId, StringComparison.OrdinalIgnoreCase))
                canvas.DrawCircle((float)screen.X, (float)screen.Y, (float)(radius + 4),
                    Paint(0xFF60A5FA, true, 1.5f));

            if (frame.HasSearch && frame.SearchMatches?.Contains(node.Id) == true)
                canvas.DrawCircle((float)screen.X, (float)screen.Y, (float)(radius + 3),
                    Paint(0xFF3B82F6, true, 2f));
            else if (frame.Neighbors.Contains(node.Id))
                canvas.DrawCircle((float)screen.X, (float)screen.Y, (float)(radius + 3),
                    Paint(0xCC60A5FA, true, 1.5f));
        }
    }

    private void DrawLabels(SKCanvas canvas, GalaxyFrameState frame, GalaxyScene scene,
        StarGalaxyCamera camera, double zoom)
    {
        var allLabels = zoom >= 0.85;
        var showTopMembers = zoom >= 0.35;
        var nodes = allLabels
            ? (IEnumerable<GalaxyNodeVm>)scene.Nodes
            : scene.Nodes
                .OrderBy(node => node.IsHub ? 0 : 1)
                .ThenBy(node => string.Equals(node.Id, frame.SelectedId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(node => frame.HasSearch && frame.SearchMatches?.Contains(node.Id) == true ? 0 : 1)
                .ThenBy(node => frame.Neighbors.Contains(node.Id) ? 0 : 1)
                .ThenBy(node => node.LabelRank)
                .ThenByDescending(node => node.Degree)
                .ThenBy(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var placedLabels = new List<SKRect>(32);

        foreach (var node in nodes)
        {
            var isMatch = frame.HasSearch && frame.SearchMatches?.Contains(node.Id) == true;
            if (!node.ShowLabel && !node.IsHub) continue;
            // The isolated group has a group caption above the grid; do not
            // stamp an arbitrary "hub" name on top of its icons.
            if (node.IsHub && node.IsIsolatedGalaxy) continue;
            if (!allLabels)
            {
                var important = node.IsHub || isMatch ||
                                string.Equals(node.Id, frame.SelectedId, StringComparison.OrdinalIgnoreCase) ||
                                frame.Neighbors.Contains(node.Id) ||
                                (showTopMembers && node.LabelRank <= 1);
                if (!important) continue;
                if (!node.IsHub && placedLabels.Count >= 40) break;
            }

            var screen = camera.WorldToScreen(node.X, node.Y);
            var radius = ScreenRadius(node, zoom);
            if (!CircleVisible(screen, radius + 40,
                    new SKRect(0, 0, (float)frame.Bounds.Width, (float)frame.Bounds.Height))) continue;

            string text;
            float fontSize;
            if (allLabels)
            {
                var rect = LabelScreenRect(node, camera);
                if (!rect.Intersects(frame.Bounds)) continue;
                fontSize = node.IsHub ? HubLabelFontSize : LabelFontSize;
                var galaxy = scene.Galaxies[node.GalaxyIndex];
                text = Ellipsize(node.IsHub && galaxy.IsIsolated
                    ? $"未连接 · {galaxy.NodeCount}"
                    : node.Title, fontSize, (float)rect.Width - 8);
                var back = new SKRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);
                canvas.DrawRoundRect(back, 4, 4,
                    Paint(isMatch ? 0xCC1D4ED8 : 0xD90B0D12, false));
                DrawScreenText(canvas, text, (float)rect.X + (float)rect.Width / 2,
                    (float)rect.Y + (float)rect.Height / 2 + fontSize * 0.34f,
                    SKTextAlign.Center, node.IsHub ? HubLabelFont : NodeLabelFont,
                    Paint(isMatch ? 0xFFFFFFFF : 0xFFCBD5E1, false));
                continue;
            }

            // Overview zoom: hubs plus the two highest-degree members of each
            // galaxy stay readable, with a cheap screen-space collision pass.
            fontSize = node.IsHub ? HubLabelFontSize : LabelFontSize - 0.5f;
            var nodeGalaxy = scene.Galaxies[node.GalaxyIndex];
            var maxLabelWidth = node.IsHub ? 240 : 170;
            text = node.IsHub && nodeGalaxy.IsIsolated
                ? $"未连接 · {nodeGalaxy.NodeCount}"
                : node.IsHub
                    ? Ellipsize($"{node.Title} · {nodeGalaxy.NodeCount}", fontSize, maxLabelWidth)
                    : Ellipsize(node.Title, fontSize, maxLabelWidth);
            var baseline = (float)(screen.Y + radius + 13);
            var textWidth = Measure(node.IsHub ? HubLabelFont : NodeLabelFont, text);
            var labelRect = new SKRect(
                (float)screen.X - textWidth / 2 - 4,
                baseline - fontSize - 3,
                (float)screen.X + textWidth / 2 + 4,
                baseline + 2);
            if (labelRect.Right < 0 || labelRect.Left > (float)frame.Bounds.Width ||
                labelRect.Bottom < 0 || labelRect.Top > (float)frame.Bounds.Height) continue;
            if (placedLabels.Any(other => other.IntersectsWith(labelRect))) continue;
            if (!node.IsHub && !isMatch &&
                !string.Equals(node.Id, frame.SelectedId, StringComparison.OrdinalIgnoreCase) &&
                OverlapsNode(labelRect, scene, camera, zoom, node.Id)) continue;
            placedLabels.Add(labelRect);

            canvas.DrawRoundRect(labelRect, 4, 4,
                Paint(isMatch ? 0xDD1D4ED8 : 0xE60B0D12, false));
            DrawScreenText(canvas, text, (float)screen.X, baseline, SKTextAlign.Center,
                node.IsHub ? HubLabelFont : NodeLabelFont,
                Paint(isMatch ? 0xFFFFFFFF : (node.IsHub ? 0xFFF8FAFC : 0xFFCBD5E1), false));
        }
    }

    private void DrawTooltip(SKCanvas canvas, GalaxyFrameState frame, GalaxyScene scene)
    {
        if (frame.HoverId is null || string.Equals(frame.HoverId, frame.SelectedId, StringComparison.OrdinalIgnoreCase))
            return;
        if (!scene.ById.TryGetValue(frame.HoverId, out var node)) return;

        var lines = new[] { node.Title, node.Subtitle, GraphPalette.StatusText(node.Model.Status) };
        var lineWidths = new[]
        {
            Measure(TooltipBoldFont, lines[0]),
            Measure(TooltipFont, lines[1]),
            Measure(TooltipFont, lines[2]),
        };
        var width = lineWidths.Max() + 26;
        var height = 66f;
        var x = Math.Clamp((float)frame.Pointer.X + 16, 6, (float)frame.Bounds.Width - width - 6);
        var y = Math.Clamp((float)frame.Pointer.Y + 16, 6, (float)frame.Bounds.Height - height - 6);
        var box = new SKRect(x, y, x + width, y + height);
        canvas.DrawRoundRect(box, 8, 8, Paint(0xF21A2030, false));
        canvas.DrawRoundRect(box, 8, 8, Paint(0xFF334155, true, 1f));

        DrawScreenText(canvas, lines[0], x + 12, y + 19, SKTextAlign.Left,
            TooltipBoldFont, Paint(0xFFF1F5F9, false));
        DrawScreenText(canvas, lines[1], x + 12, y + 37, SKTextAlign.Left,
            TooltipFont, Paint(0xFF94A3B8, false));
        DrawScreenText(canvas, lines[2], x + 12, y + 54, SKTextAlign.Left,
            TooltipFont, Paint(WithAlpha(ToArgb(node.StatusColor), 235), false));
    }

    private void DrawHeader(SKCanvas canvas, GalaxyFrameState frame, GalaxyScene scene,
        StarGalaxyCamera camera)
    {
        string text;
        uint color;
        if (frame.HasSearch && frame.SearchMatches is { } matches)
        {
            text = $"匹配 {matches.Count} 个模组 · 已自动聚焦 · Esc 取消高亮";
            color = 0xFF93C5FD;
        }
        else
        {
            text = $"{scene.TotalCount} 个模组 · {scene.Galaxies.Count} 个星系" +
                   (scene.Unconnected.Count > 0
                       ? $" · 未连接 {scene.Unconnected.Count}"
                       : string.Empty) +
                   " · 滚轮放大查看全部名称";
            color = 0xFF94A3B8;
        }
        DrawScreenText(canvas, text, 16, 24, SKTextAlign.Left, LegendFont, Paint(color, false));
    }

    private void DrawOverview(SKCanvas canvas, GalaxyFrameState frame, GalaxyScene scene)
    {
        var overview = frame.Overview;
        if (overview.Panel.Width <= 0) return;
        var panel = overview.Panel;
        canvas.DrawRoundRect(new SKRect((float)panel.X, (float)panel.Y,
            (float)panel.Right, (float)panel.Bottom), 10, 10, Paint(0xF2141822, false));
        canvas.DrawRoundRect(new SKRect((float)panel.X, (float)panel.Y,
            (float)panel.Right, (float)panel.Bottom), 10, 10, Paint(0xFF2B3345, true, 1f));

        DrawScreenText(canvas, "星系关系总览", (float)panel.X + 12, (float)panel.Y + 21,
            SKTextAlign.Left, OverviewTitleFont, Paint(0xFFCBD5E1, false));
        DrawScreenText(canvas, overview.Collapsed ? "＋" : "－",
            (float)panel.Right - 16, (float)panel.Y + 21, SKTextAlign.Center,
            OverviewTitleFont, Paint(0xFF94A3B8, false));
        if (overview.Collapsed) return;

        var points = overview.Points;

        // Only the strongest cross-galaxy relations: a full 46-link mesh turns
        // the mini-map back into the hairball this view is meant to avoid.
        var links = scene.CrossLinks
            .Where(link => link.FromGalaxy < points.Length && link.ToGalaxy < points.Length)
            .OrderByDescending(link => link.Count)
            .ThenBy(link => link.FromGalaxy)
            .ThenBy(link => link.ToGalaxy)
            .Take(9);
        foreach (var link in links)
        {
            var from = points[link.FromGalaxy];
            var to = points[link.ToGalaxy];
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var bend = 10 + Math.Min(14, link.Count * 1.5);
            var control = new Point(
                (from.X + to.X) / 2 - dy / length * bend,
                (from.Y + to.Y) / 2 + dx / length * bend);
            var width = 0.9f + (float)Math.Log2(link.Count + 1);
            var alpha = (byte)Math.Clamp(70 + link.Count * 12, 70, 210);
            var color = link.RequiresCount > 0 ? 0xFF3B82F6 : 0xFF64748B;
            using var path = new SKPath();
            path.MoveTo((float)from.X, (float)from.Y);
            path.QuadTo((float)control.X, (float)control.Y, (float)to.X, (float)to.Y);
            canvas.DrawPath(path, Paint(WithAlpha(color, alpha), true, width));
        }

        var selectedGalaxy = frame.SelectedGalaxy;
        var maxCount = Math.Max(1, scene.Galaxies.Max(galaxy => galaxy.NodeCount));
        foreach (var galaxy in scene.Galaxies)
        {
            if (galaxy.Index >= points.Length) continue;
            var point = points[galaxy.Index];
            var color = ToArgb(galaxy.Color);
            var radius = 7.5f + 4.5f * galaxy.NodeCount / maxCount;
            if (galaxy.Index == selectedGalaxy)
                canvas.DrawCircle((float)point.X, (float)point.Y, radius + 4,
                    Paint(WithAlpha(color, 110), true, 2f));
            canvas.DrawCircle((float)point.X, (float)point.Y, radius,
                Paint(WithAlpha(color, 235), false));
            DrawScreenText(canvas, galaxy.OverviewTitle, (float)point.X, (float)point.Y + 2.8f,
                SKTextAlign.Center, OverviewFont, Paint(0xFF0B0D12, false));
        }

        DrawScreenText(canvas, "点击星系聚焦 · 仅显示主要跨星系连接",
            (float)panel.X + 12, (float)panel.Bottom - 11,
            SKTextAlign.Left, OverviewFont, Paint(0xFF64748B, false));
    }

    private void DrawLegend(SKCanvas canvas, GalaxyFrameState frame)
    {
        var x = 16f;
        var y = (float)frame.Bounds.Height - 92;
        var entries = new (uint Color, string Text)[]
        {
            (0xFFF5C542, "星系核心"),
            (0xFF3B82F6, "直接依赖"),
            (0xFF64748B, "深层依赖"),
            (0xFF34D399, "可选关系"),
            (0xFF8492A6, "跨星系关系（聚合）"),
        };
        for (var i = 0; i < entries.Length; i++)
        {
            var rowY = y + i * 16;
            canvas.DrawCircle(x + 4, rowY - 3, 3.5f, Paint(entries[i].Color, false));
            DrawScreenText(canvas, entries[i].Text, x + 13, rowY, SKTextAlign.Left,
                LegendFont, Paint(0xFF94A3B8, false));
        }
    }

    private void DrawHint(SKCanvas canvas, GalaxyFrameState frame)
    {
        var text = "滚轮缩放 · 拖空白平移 · 拖节点移动 · 单击枢纽聚焦 · 双击重生成 · 0 回总览";
        DrawScreenText(canvas, text, (float)frame.Bounds.Width / 2,
            (float)frame.Bounds.Height - 12, SKTextAlign.Center, LegendFont,
            Paint(0xFF475569, false));
    }

    // ------------------------------------------------------------------ drawing helpers

    private static HashSet<string> BuildNeighbors(GalaxyScene? scene, string? selectedId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scene is null || selectedId is null) return result;
        foreach (var edge in scene.Edges)
        {
            if (string.Equals(edge.From.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                result.Add(edge.To.Id);
            else if (string.Equals(edge.To.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                result.Add(edge.From.Id);
        }
        return result;
    }

    private static int GetSelectedGalaxy(GalaxyScene? scene, string? selectedId)
    {
        if (scene is null || selectedId is null) return -1;
        return scene.ById.TryGetValue(selectedId, out var node) ? node.GalaxyIndex : -1;
    }

    private Point ToScreen(GalaxyNodeVm node) =>
        new(node.X * _camera.Zoom + _camera.PanX, node.Y * _camera.Zoom + _camera.PanY);

    private float ScreenRadius(GalaxyNodeVm node) => ScreenRadius(node, _camera.Zoom);

    private static float ScreenRadius(GalaxyNodeVm node, double zoom)
    {
        // The unconnected group is a compact asteroid belt, not another
        // galaxy; keep its markers small so icons cannot dominate the canvas.
        if (node.IsIsolatedGalaxy)
            return (float)Math.Clamp(node.Radius * zoom * 0.55, 4.0, 9.0);
        var minimum = node.IsHub ? 11.0 : 7.5;
        var maximum = node.IsHub ? 40.0 : MaxScreenNodeRadius;
        return (float)Math.Clamp(node.Radius * zoom, minimum, maximum);
    }

    private Rect LabelScreenRect(GalaxyNodeVm node) => LabelScreenRect(node, _camera);

    private static Rect LabelScreenRect(GalaxyNodeVm node, StarGalaxyCamera camera)
    {
        var rect = node.LabelRect;
        return new Rect(
            rect.X * camera.Zoom + camera.PanX,
            rect.Y * camera.Zoom + camera.PanY,
            rect.Width * camera.Zoom,
            rect.Height * camera.Zoom);
    }

    private static bool OverlapsNode(SKRect label, GalaxyScene scene, StarGalaxyCamera camera,
        double zoom, string excludeId)
    {
        foreach (var node in scene.Nodes)
        {
            if (string.Equals(node.Id, excludeId, StringComparison.OrdinalIgnoreCase)) continue;
            var screen = camera.WorldToScreen(node.X, node.Y);
            var radius = ScreenRadius(node, zoom) + 2;
            var nodeRect = new SKRect((float)(screen.X - radius), (float)(screen.Y - radius),
                (float)(screen.X + radius), (float)(screen.Y + radius));
            if (nodeRect.IntersectsWith(label)) return true;
        }
        return false;
    }

    private static bool CircleVisible(LayoutPoint center, double radius, SKRect view) =>
        center.X + radius >= view.Left && center.X - radius <= view.Right &&
        center.Y + radius >= view.Top && center.Y - radius <= view.Bottom;

    private static string Ellipsize(string text, float fontSize, float maxWidth)
    {
        var font = Math.Abs(fontSize - HubLabelFontSize) < 0.01f ? HubLabelFont : NodeLabelFont;
        return Ellipsize(text, font, maxWidth);
    }

    private static string Ellipsize(string text, SKFont font, float maxWidth)
    {
        if (Measure(font, text) <= maxWidth) return text;
        const string ellipsis = "…";
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + ellipsis;
            if (Measure(font, candidate) <= maxWidth) return candidate;
        }
        return ellipsis;
    }

    private static string FirstGlyph(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            return rune.ToString();
        return "?";
    }

    private static void DrawScreenText(SKCanvas canvas, string text, float x, float y,
        SKTextAlign align, SKFont font, SKPaint paint)
    {
        var width = Measure(font, text);
        var left = align switch
        {
            SKTextAlign.Center => x - width / 2,
            SKTextAlign.Right => x - width,
            _ => x,
        };
        canvas.DrawText(text, left, y, font, paint);
    }

    private static float Measure(SKFont font, string text) =>
        string.IsNullOrEmpty(text)
            ? 0
            : font.MeasureText(MemoryMarshal.Cast<char, ushort>(text.AsSpan()), MeasurePaint);

    private static SKPaint Paint(uint color, bool stroke, float width = 1f, bool dashed = false) =>
        PaintCache.GetOrAdd((color, stroke, width, dashed), key =>
        {
            var paint = new SKPaint
            {
                Color = new SKColor(key.Color),
                IsAntialias = true,
                Style = key.Stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
                StrokeWidth = key.Width,
            };
            if (key.Dashed) paint.PathEffect = SKPathEffect.CreateDash([4f, 3f], 0);
            return paint;
        });

    private static SKColor ToSk(Color color) => new(color.R, color.G, color.B, color.A);

    private static uint ToArgb(Color color) =>
        (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);

    private static uint WithAlpha(uint argb, byte alpha) => (argb & 0x00FFFFFF) | ((uint)alpha << 24);

    // ------------------------------------------------------------------ draw operation

    private sealed class GalaxyDrawOperation(StarGalaxyView view, GalaxyFrameState frame)
        : ICustomDrawOperation
    {
        public Rect Bounds => frame.Bounds;

        public bool HitTest(Point p) => true;

        public Rect FrameBounds => frame.Bounds;

        public int FrameVersion => frame.Version;

        public bool Equals(ICustomDrawOperation? other) =>
            other is GalaxyDrawOperation operation &&
            operation.FrameVersion == FrameVersion &&
            operation.FrameBounds == FrameBounds;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context) => view.DrawGalaxy(context, frame);
    }

    private sealed class GalaxyFrameState
    {
        public Rect Bounds;
        public GalaxyScene? Scene;
        public StarGalaxyCamera Camera;
        public string? HoverId;
        public string? SelectedId;
        public HashSet<string>? SearchMatches;
        public bool HasSearch;
        public OverviewLayout Overview;
        public SKImage?[] Icons = [];
        public SKImage?[] UnconnectedIcons = [];
        public HashSet<string> Neighbors = [];
        public int SelectedGalaxy = -1;
        public SelectionDetails? SelectedDetails;
        public Point Pointer;
        public int Version;
    }

    // ------------------------------------------------------------------ icon cache

    private sealed class GalaxyIconCache
    {
        private readonly Dictionary<string, SKImage?> _images = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;

        public SKImage? TryGet(string id)
        {
            lock (_gate) return _images.GetValueOrDefault(id);
        }

        public void Clear()
        {
            lock (_gate) _images.Clear();
        }

        public void Cancel()
        {
            var cts = Interlocked.Exchange(ref _cts, null);
            cts?.Cancel();
        }

        public void Load(GalaxyScene scene, Action onBatch)
        {
            Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            var items = scene.Nodes
                .Concat(scene.Unconnected)
                .Where(node => node.Model.IconData is { Length: > 0 })
                .Select(node => (node.Id, Data: node.Model.IconData!))
                .ToArray();
            if (items.Length == 0) return;

            _ = Task.Run(() =>
            {
                var decoded = 0;
                foreach (var item in items)
                {
                    if (cts.IsCancellationRequested) return;
                    var image = Decode(item.Data);
                    lock (_gate) _images[item.Id] = image;
                    decoded++;
                    if (decoded % 24 == 0) onBatch();
                }
                onBatch();
            });
        }

        private static SKImage? Decode(byte[] data)
        {
            try
            {
                using var bitmap = SKBitmap.Decode(data);
                if (bitmap is null) return null;
                using var resized = bitmap.Resize(new SKImageInfo(64, 64), SKFilterQuality.Medium);
                return SKImage.FromBitmap(resized ?? bitmap);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
