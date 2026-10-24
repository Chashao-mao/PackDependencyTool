using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PackDepTool.App.Services;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Models;

namespace PackDepTool.App.Controls;

public sealed class NodeSelectionEventArgs : EventArgs
{
    public IReadOnlyList<string> NodeIds { get; init; } = [];
    public string? EdgeId { get; init; }
}

public sealed class NodeMovedEventArgs : EventArgs
{
    public string NodeId { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>
/// Custom-rendered dependency graph: zoom/pan, node dragging, marquee selection,
/// context menus and search highlighting. Rendering is cached per text/brush and
/// simplified when zoomed out so large packs stay responsive.
/// </summary>
public sealed class DependencyGraphView : Control
{
    public static readonly StyledProperty<GraphScene?> SceneProperty =
        AvaloniaProperty.Register<DependencyGraphView, GraphScene?>(nameof(Scene));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<DependencyGraphView, IBrush?>(nameof(Background));

    private const double MinScale = 0.12;
    private const double ReadableMinScale = 0.6;
    private const double MaxScale = 3.5;

    private static readonly Typeface Regular =
        new(new FontFamily("Microsoft YaHei UI, Segoe UI, Inter"), FontStyle.Normal, FontWeight.Normal);

    private static readonly Typeface Semibold =
        new(new FontFamily("Microsoft YaHei UI, Segoe UI, Inter"), FontStyle.Normal, FontWeight.SemiBold);

    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor DragCursor = new(StandardCursorType.SizeAll);

    private readonly Dictionary<TextKey, FormattedText> _textCache = new();
    private readonly Dictionary<uint, IBrush> _brushCache = new();
    private readonly HashSet<string> _selectedNodeIds = new(StringComparer.OrdinalIgnoreCase);

    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private string? _selectedEdgeId;
    private GraphNodeVm? _hoveredNode;
    private GraphEdgeVm? _hoveredEdge;
    private bool _suppressSelectionEvents;

    private bool _panning;
    private bool _minimapDragging;
    private Point _panStart;
    private double _panOffsetX;
    private double _panOffsetY;

    private GraphNodeVm? _draggingNode;
    private Point _dragStartWorld;
    private double _dragNodeX;
    private double _dragNodeY;

    private bool _boxSelecting;
    private Point _boxStart;
    private Point _boxCurrent;
    private bool _dragMoved;

    public DependencyGraphView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = ArrowCursor;
    }

    static DependencyGraphView()
    {
        AffectsRender<DependencyGraphView>(SceneProperty, BackgroundProperty);
    }

    public GraphScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SceneProperty)
        {
            var scene = Scene;
            if (scene is null)
            {
                _selectedNodeIds.Clear();
                _selectedEdgeId = null;
            }
            else
            {
                var ids = scene.Nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _selectedNodeIds.RemoveWhere(id => !ids.Contains(id));
                if (_selectedEdgeId is not null &&
                    scene.Edges.All(e => !string.Equals(e.Id, _selectedEdgeId, StringComparison.OrdinalIgnoreCase)))
                    _selectedEdgeId = null;
            }

            InvalidateVisual();
        }
    }

    public event EventHandler<NodeSelectionEventArgs>? SelectionChanged;
    public event EventHandler<NodeMovedEventArgs>? NodeMoved;
    public event Action<string>? AddRelationRequested;
    public event Action<string, string, EdgeType>? QuickLinkToRequested;
    public event Action<string>? EditManualRequested;
    public event Action<string>? DeleteManualRequested;
    public event Action<string>? OpenProjectRequested;
    public event Action<string>? OpenFileRequested;
    public event Action<string>? CopyIdRequested;

    // ------------------------------------------------------------------ public API

    public void FitToView(bool allowTiny = false)
    {
        if (Scene is null || Scene.Nodes.Count == 0 || Bounds.Width < 20 || Bounds.Height < 20)
            return;

        var content = Scene.ContentBounds;
        if (content.Width <= 0 || content.Height <= 0) return;

        var scaleX = (Bounds.Width - 100) / content.Width;
        var scaleY = (Bounds.Height - 100) / content.Height;
        var floor = allowTiny ? MinScale : ReadableMinScale;
        _scale = Math.Clamp(Math.Min(scaleX, scaleY), floor, 1.25);
        _offsetX = (Bounds.Width - content.Width * _scale) / 2 - content.X * _scale;
        _offsetY = (Bounds.Height - content.Height * _scale) / 2 - content.Y * _scale;
        InvalidateVisual();
    }

    public void ZoomIn() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1.18);

    public void ZoomOut() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1 / 1.18);

    public void SelectNode(string id, bool center = true)
    {
        var node = Scene?.Nodes.FirstOrDefault(n =>
            string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
        if (node is null) return;

        _selectedNodeIds.Clear();
        _selectedNodeIds.Add(node.Id);
        _selectedEdgeId = null;

        if (center) CenterOn(node);
        RaiseSelection();
        InvalidateVisual();
    }

    public void SelectEdge(string id, bool center = true)
    {
        var edge = Scene?.Edges.FirstOrDefault(e =>
            string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (edge is null) return;

        _selectedNodeIds.Clear();
        _selectedEdgeId = edge.Id;

        if (center)
        {
            var midX = (edge.From.X + edge.To.X) / 2;
            var midY = (edge.From.Y + edge.To.Y) / 2;
            if (_scale < 0.65) _scale = 0.9;
            _offsetX = Bounds.Width / 2 - midX * _scale;
            _offsetY = Bounds.Height / 2 - midY * _scale;
        }

        RaiseSelection();
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (_selectedNodeIds.Count == 0 && _selectedEdgeId is null) return;
        _selectedNodeIds.Clear();
        _selectedEdgeId = null;
        RaiseSelection();
        InvalidateVisual();
    }

    public void FocusNode(string id) => SelectNode(id, center: true);

    /// <summary>Called when the theme changes so cached text/brush colours are rebuilt.</summary>
    public void RefreshTheme()
    {
        _textCache.Clear();
        _brushCache.Clear();
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y >= 0 ? 1.12 : 1 / 1.12;
        ZoomAt(e.GetPosition(this), factor);
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsRightButtonPressed)
        {
            ShowContextMenu(point);
            e.Handled = true;
            return;
        }

        if (properties.IsMiddleButtonPressed)
        {
            StartPan(point, e);
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            base.OnPointerPressed(e);
            return;
        }

        var hitNode = HitTestNode(point);
        if (hitNode is not null)
        {
            var toggle = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (!_selectedNodeIds.Contains(hitNode.Id) || toggle)
            {
                if (toggle && _selectedNodeIds.Contains(hitNode.Id))
                    _selectedNodeIds.Remove(hitNode.Id);
                else if (!toggle)
                {
                    _selectedNodeIds.Clear();
                    _selectedNodeIds.Add(hitNode.Id);
                }
                else
                {
                    _selectedNodeIds.Add(hitNode.Id);
                }
                _selectedEdgeId = null;
                RaiseSelection();
            }

            if (e.ClickCount == 2)
            {
                CenterOn(hitNode);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            _draggingNode = hitNode;
            _dragStartWorld = ScreenToWorld(point);
            _dragNodeX = hitNode.X;
            _dragNodeY = hitNode.Y;
            _dragMoved = false;
            Cursor = DragCursor;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var hitEdge = HitTestEdge(point);
        if (hitEdge is not null)
        {
            _selectedNodeIds.Clear();
            _selectedEdgeId = hitEdge.Id;
            RaiseSelection();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _boxSelecting = true;
            _boxStart = point;
            _boxCurrent = point;
            _dragMoved = false;
            e.Pointer.Capture(this);
        }
        else
        {
            StartPan(point, e);
        }

        e.Handled = true;
    }

    private void StartPan(Point point, PointerEventArgs e)
    {
        _panning = true;
        _panStart = point;
        _panOffsetX = _offsetX;
        _panOffsetY = _offsetY;
        _dragMoved = false;
        Cursor = DragCursor;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);

        if (_minimapDragging)
        {
            PanFromMinimap(point);
            e.Handled = true;
            return;
        }

        if (_draggingNode is not null)
        {
            var world = ScreenToWorld(point);
            _draggingNode.X = _dragNodeX + (world.X - _dragStartWorld.X);
            _draggingNode.Y = _dragNodeY + (world.Y - _dragStartWorld.Y);
            _dragMoved = true;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_panning)
        {
            _offsetX = _panOffsetX + (point.X - _panStart.X);
            _offsetY = _panOffsetY + (point.Y - _panStart.Y);
            if (Math.Abs(point.X - _panStart.X) > 2 || Math.Abs(point.Y - _panStart.Y) > 2)
                _dragMoved = true;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_boxSelecting)
        {
            _boxCurrent = point;
            _dragMoved = true;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var hovered = HitTestNode(point);
        var edge = hovered is null ? HitTestEdge(point) : null;
        if (!ReferenceEquals(hovered, _hoveredNode) || !ReferenceEquals(edge, _hoveredEdge))
        {
            _hoveredNode = hovered;
            _hoveredEdge = edge;
            Cursor = hovered is not null ? HandCursor : ArrowCursor;
            InvalidateVisual();
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var point = e.GetPosition(this);

        if (_minimapDragging)
        {
            _minimapDragging = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            base.OnPointerReleased(e);
            return;
        }

        if (_draggingNode is not null)
        {
            var node = _draggingNode;
            _draggingNode = null;
            Cursor = HandCursor;
            if (_dragMoved)
                NodeMoved?.Invoke(this, new NodeMovedEventArgs { NodeId = node.Id, X = node.X, Y = node.Y });
            InvalidateVisual();
        }

        if (_panning)
        {
            _panning = false;
            Cursor = ArrowCursor;
            if (!_dragMoved)
            {
                _selectedNodeIds.Clear();
                _selectedEdgeId = null;
                RaiseSelection();
            }
            InvalidateVisual();
        }

        if (_boxSelecting)
        {
            _boxSelecting = false;
            var rect = Normalize(_boxStart, _boxCurrent);
            var p1 = ScreenToWorld(rect.TopLeft);
            var p2 = ScreenToWorld(rect.BottomRight);
            var worldRect = new Rect(
                Math.Min(p1.X, p2.X),
                Math.Min(p1.Y, p2.Y),
                Math.Abs(p1.X - p2.X),
                Math.Abs(p1.Y - p2.Y));

            _selectedNodeIds.Clear();
            _selectedEdgeId = null;
            if (Scene is not null)
            {
                foreach (var node in Scene.Nodes)
                {
                    if (worldRect.Intersects(node.Bounds))
                        _selectedNodeIds.Add(node.Id);
                }
            }

            RaiseSelection();
            InvalidateVisual();
        }

        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ClearSelection();
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                ZoomIn();
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                ZoomOut();
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                FitToView();
                e.Handled = true;
                break;
            case Key.Delete:
                if (_selectedEdgeId is not null)
                {
                    DeleteManualRequested?.Invoke(_selectedEdgeId);
                    e.Handled = true;
                }
                break;
        }

        base.OnKeyDown(e);
    }

    // ------------------------------------------------------------------ context menu

    private void ShowContextMenu(Point screenPoint)
    {
        var menu = new ContextMenu();
        var node = HitTestNode(screenPoint);

        if (node is not null)
        {
            _selectedNodeIds.Clear();
            _selectedNodeIds.Add(node.Id);
            _selectedEdgeId = null;
            RaiseSelection();
            InvalidateVisual();

            menu.Items.Add(Item("查看详情", () => { }));
            menu.Items.Add(QuickLinkSubmenu(node, "连接到必需依赖", EdgeType.Requires));
            menu.Items.Add(QuickLinkSubmenu(node, "连接到可选依赖", EdgeType.Optional));
            menu.Items.Add(QuickLinkSubmenu(node, "声明不兼容", EdgeType.Incompatible));
            menu.Items.Add(QuickLinkSubmenu(node, "连接到推荐依赖", EdgeType.Recommends));
            menu.Items.Add(Item("添加关系（表单）", () => AddRelationRequested?.Invoke(node.Id)));
            menu.Items.Add(Item("复制 modId", () => CopyIdRequested?.Invoke(node.Id)));
            if (!string.IsNullOrWhiteSpace(node.Model.ProjectIds.Modrinth) ||
                !string.IsNullOrWhiteSpace(node.Model.ProjectIds.CurseForge))
                menu.Items.Add(Item("打开项目页面", () => OpenProjectRequested?.Invoke(node.Id)));
            if (!string.IsNullOrWhiteSpace(node.Model.FilePath))
                menu.Items.Add(Item("在资源管理器中显示", () => OpenFileRequested?.Invoke(node.Id)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("聚焦此节点", () => { CenterOn(node); InvalidateVisual(); }));
        }
        else
        {
            var edge = HitTestEdge(screenPoint);
            if (edge is not null)
            {
                _selectedNodeIds.Clear();
                _selectedEdgeId = edge.Id;
                RaiseSelection();
                InvalidateVisual();

                menu.Items.Add(Item("查看详情", () => { }));
                if (edge.Model.ManualOverride)
                {
                    menu.Items.Add(Item("编辑手动关系", () => EditManualRequested?.Invoke(edge.Id)));
                    menu.Items.Add(Item("删除手动关系", () => DeleteManualRequested?.Invoke(edge.Id)));
                }
                else
                {
                    menu.Items.Add(Item(
                        "改为手动关系",
                        () => AddRelationRequested?.Invoke(edge.From.Id)));
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("聚焦此关系", () => SelectEdge(edge.Id, center: true)));
            }
            else
            {
                menu.Items.Add(Item("可读范围", () => FitToView()));
                menu.Items.Add(Item("全局总览", () => FitToView(allowTiny: true)));
                menu.Items.Add(Item("清除选择", ClearSelection));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("缩放 +", ZoomIn));
                menu.Items.Add(Item("缩放 −", ZoomOut));
            }
        }

        menu.Open(this);
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem QuickLinkSubmenu(GraphNodeVm source, string header, EdgeType type)
    {
        var menu = new MenuItem { Header = header };
        var targets = Scene?.Nodes
            .Where(node => !string.Equals(node.Id, source.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(60)
            .ToList() ?? [];

        menu.IsEnabled = targets.Count > 0;
        foreach (var target in targets)
        {
            var item = new MenuItem { Header = $"{target.Title}  ·  {target.Id}" };
            item.Click += (_, _) => QuickLinkToRequested?.Invoke(source.Id, target.Id, type);
            menu.Items.Add(item);
        }

        return menu;
    }

    // ------------------------------------------------------------------ rendering

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is not null)
            context.FillRectangle(Background, bounds);

        if (Scene is null || Scene.Nodes.Count == 0)
            return;

        using (context.PushClip(bounds))
        {
            using (context.PushTransform(
                       Matrix.CreateScale(_scale, _scale) *
                       Matrix.CreateTranslation(_offsetX, _offsetY)))
            {
                DrawGrid(context);
                DrawEdges(context);
                DrawNodes(context);
            }

            DrawSelectionBox(context);
            DrawEdgeLabel(context);
        }
    }

    private Rect GetMinimapRect() => new(14, Math.Max(14, Bounds.Height - 142), 184, 128);

    private bool TryStartMinimap(Point point, PointerPressedEventArgs e)
    {
        if (Scene is null || Scene.Nodes.Count == 0) return false;
        var rect = GetMinimapRect();
        if (!rect.Contains(point)) return false;

        _minimapDragging = true;
        PanFromMinimap(point);
        e.Pointer.Capture(this);
        return true;
    }

    private void PanFromMinimap(Point point)
    {
        if (Scene is null) return;
        var content = Scene.ContentBounds;
        if (content.Width <= 0 || content.Height <= 0) return;

        var rect = GetMinimapRect();
        var inner = rect.Deflate(9);
        var miniScale = Math.Min(inner.Width / content.Width, inner.Height / content.Height);
        var miniOffsetX = inner.X + (inner.Width - content.Width * miniScale) / 2 - content.X * miniScale;
        var miniOffsetY = inner.Y + (inner.Height - content.Height * miniScale) / 2 - content.Y * miniScale;

        var worldX = (point.X - miniOffsetX) / miniScale;
        var worldY = (point.Y - miniOffsetY) / miniScale;
        _offsetX = Bounds.Width / 2 - worldX * _scale;
        _offsetY = Bounds.Height / 2 - worldY * _scale;
        InvalidateVisual();
    }

    private void DrawMinimap(DrawingContext context)
    {
        if (Scene is null || Scene.Nodes.Count < 8) return;
        var content = Scene.ContentBounds;
        if (content.Width <= 0 || content.Height <= 0) return;

        var rect = GetMinimapRect();
        var inner = rect.Deflate(9);
        var miniScale = Math.Min(inner.Width / content.Width, inner.Height / content.Height);
        if (miniScale <= 0) return;
        var miniOffsetX = inner.X + (inner.Width - content.Width * miniScale) / 2 - content.X * miniScale;
        var miniOffsetY = inner.Y + (inner.Height - content.Height * miniScale) / 2 - content.Y * miniScale;

        context.DrawRectangle(GetBrush(GraphPalette.NodeFill, 235),
            new Pen(GetBrush(GraphPalette.NodeBorder), 1), rect, 8, 8);

        foreach (var node in Scene.Nodes)
        {
            var x = miniOffsetX + node.X * miniScale;
            var y = miniOffsetY + node.Y * miniScale;
            context.DrawEllipse(GetBrush(GraphPalette.StatusColor(node.Model.Status)), null,
                new Point(x, y), 1.4, 1.4);
        }

        var topLeft = ScreenToWorld(new Point(0, 0));
        var bottomRight = ScreenToWorld(new Point(Bounds.Width, Bounds.Height));
        var viewLeft = miniOffsetX + topLeft.X * miniScale;
        var viewTop = miniOffsetY + topLeft.Y * miniScale;
        var viewWidth = (bottomRight.X - topLeft.X) * miniScale;
        var viewHeight = (bottomRight.Y - topLeft.Y) * miniScale;
        context.DrawRectangle(null, new Pen(GetBrush(GraphPalette.Accent), 1.5),
            new Rect(viewLeft, viewTop, Math.Max(2, viewWidth), Math.Max(2, viewHeight)), 2, 2);

        var hint = GetText("小地图：拖动定位", 8.5, Regular, GraphPalette.TextMuted);
        context.DrawText(hint, new Point(rect.X + 8, rect.Y + 6));
    }

    private void DrawGrid(DrawingContext context)
    {
        var topLeft = ScreenToWorld(new Point(0, 0));
        var bottomRight = ScreenToWorld(new Point(Bounds.Width, Bounds.Height));
        var step = 80.0;
        var color = IsLightTheme ? Color.Parse("#DDE5EF") : Color.Parse("#16202F");
        var pen = new Pen(GetBrush(color), 1 / _scale);

        var startX = Math.Floor(topLeft.X / step) * step;
        var startY = Math.Floor(topLeft.Y / step) * step;
        var lines = 0;

        for (var x = startX; x <= bottomRight.X && lines < 400; x += step, lines++)
            context.DrawLine(pen, new Point(x, topLeft.Y), new Point(x, bottomRight.Y));

        lines = 0;
        for (var y = startY; y <= bottomRight.Y && lines < 400; y += step, lines++)
            context.DrawLine(pen, new Point(topLeft.X, y), new Point(bottomRight.X, y));
    }

    private void DrawEdges(DrawingContext context)
    {
        if (Scene is null) return;

        var focus = GetFocusIds();
        var focusRoot = GetFocusRootIds();
        var selected = Scene.Edges.FirstOrDefault(e =>
            string.Equals(e.Id, _selectedEdgeId, StringComparison.OrdinalIgnoreCase));
        var hovered = _hoveredEdge;

        foreach (var edge in Scene.Edges)
        {
            if (ReferenceEquals(edge, selected) || ReferenceEquals(edge, hovered)) continue;

            if (Scene.FocusEdgesOnly &&
                (focusRoot is null ||
                 (!focusRoot.Contains(edge.From.Id) && !focusRoot.Contains(edge.To.Id))))
                continue;

            var connected = focus is null ||
                            (focus.Contains(edge.From.Id) && focus.Contains(edge.To.Id));
            var opacity = Scene.SearchTerm.Length == 0 || edge.IsSearchMatch ? 1.0 : 0.12;
            if (!connected) opacity = Math.Min(opacity, 0.1);
            using (context.PushOpacity(opacity))
                DrawEdge(context, edge, selected: false, hovered: false);
        }

        if (hovered is not null && !ReferenceEquals(hovered, selected))
        {
            using (context.PushOpacity(Scene.SearchTerm.Length == 0 || hovered.IsSearchMatch ? 1.0 : 0.2))
                DrawEdge(context, hovered, selected: false, hovered: true);
        }

        if (selected is not null)
            DrawEdge(context, selected, selected: true, hovered: false);
    }

    private HashSet<string>? GetFocusRootIds()
    {
        if (_selectedNodeIds.Count > 0)
            return new HashSet<string>(_selectedNodeIds, StringComparer.OrdinalIgnoreCase);
        if (_hoveredNode is not null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _hoveredNode.Id };
        return null;
    }

    private HashSet<string>? GetFocusIds()
    {
        if (Scene is null) return null;
        HashSet<string>? focus = null;
        if (_selectedNodeIds.Count > 0)
            focus = new HashSet<string>(_selectedNodeIds, StringComparer.OrdinalIgnoreCase);
        else if (_hoveredNode is not null)
            focus = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _hoveredNode.Id };

        if (focus is null) return null;
        foreach (var edge in Scene.Edges)
        {
            if (focus.Contains(edge.From.Id) || focus.Contains(edge.To.Id))
            {
                focus.Add(edge.From.Id);
                focus.Add(edge.To.Id);
            }
        }

        return focus;
    }

    private void DrawEdge(DrawingContext context, GraphEdgeVm edge, bool selected, bool hovered)
    {
        var curve = GetCurve(edge);
        var manual = edge.Model.ManualOverride || edge.Model.Type == Core.Models.EdgeType.Manual;
        var color = selected
            ? (IsLightTheme ? Color.Parse("#17202B") : Colors.White)
            : GraphPalette.EdgeColor(edge.Model.Type, manual);

        var thickness = edge.Model.Type == Core.Models.EdgeType.Incompatible ? 2.2 : 1.5;
        if (manual) thickness = 2.2;
        if (selected) thickness = 3;
        if (hovered) thickness += 0.6;

        var dash = edge.Model.Type switch
        {
            Core.Models.EdgeType.Optional or Core.Models.EdgeType.Recommends => DashStyle.Dash,
            Core.Models.EdgeType.Embedded => DashStyle.Dot,
            _ => null,
        };

        if (manual)
        {
            var glow = new Pen(GetBrush(GraphPalette.EdgeManual, 70), thickness + 5, dash);
            context.DrawGeometry(null, glow, curve.Geometry);
        }

        var pen = new Pen(GetBrush(color), thickness, dash);
        context.DrawGeometry(null, pen, curve.Geometry);
        DrawArrowHead(context, curve.End, curve.Control, GetBrush(color), thickness);
    }

    private void DrawArrowHead(DrawingContext context, Point tip, Point control, IBrush brush, double thickness)
    {
        var dx = tip.X - control.X;
        var dy = tip.Y - control.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) return;

        var ux = dx / length;
        var uy = dy / length;
        var size = 9.0 + thickness;
        var half = 4.4 + thickness * 0.5;

        var basePoint = new Point(tip.X - ux * size, tip.Y - uy * size);
        var left = new Point(basePoint.X - uy * half, basePoint.Y + ux * half);
        var right = new Point(basePoint.X + uy * half, basePoint.Y - ux * half);

        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(tip, true);
            stream.LineTo(left);
            stream.LineTo(right);
            stream.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    private void DrawNodes(DrawingContext context)
    {
        if (Scene is null) return;

        var focus = GetFocusIds();
        foreach (var node in Scene.Nodes)
        {
            var selected = _selectedNodeIds.Contains(node.Id);
            var opacity = selected || Scene.SearchTerm.Length == 0 || node.IsSearchMatch ? 1.0 : 0.18;
            if (focus is not null && !selected && !focus.Contains(node.Id))
                opacity = Math.Min(opacity, 0.15);
            using (context.PushOpacity(opacity))
                DrawNode(context, node, selected, ReferenceEquals(node, _hoveredNode));
        }
    }

    private void DrawNode(DrawingContext context, GraphNodeVm node, bool selected, bool hovered)
    {
        var model = node.Model;
        var rect = node.Bounds;
        var statusColor = GraphPalette.StatusColor(model.Status);
        var loaderColor = GraphPalette.LoaderColor(model.Loader);

        var fill = IsLightTheme ? Color.Parse("#FFFFFF") : GraphPalette.NodeFill;
        if (model.Status is Core.Models.ModStatus.Missing or Core.Models.ModStatus.Conflict)
            fill = IsLightTheme ? Color.Parse("#FDF0EF") : Color.Parse("#2A171A");

        var borderColor = model.Status switch
        {
            Core.Models.ModStatus.Missing or Core.Models.ModStatus.Conflict => GraphPalette.Danger,
            Core.Models.ModStatus.VersionMismatch => GraphPalette.Warning,
            Core.Models.ModStatus.Unknown => Color.Parse("#8A94A6"),
            _ => loaderColor,
        };

        if (selected) borderColor = IsLightTheme ? Color.Parse("#17202B") : Colors.White;
        else if (hovered) borderColor = GraphPalette.Accent;

        if (selected)
        {
            context.DrawRectangle(
                GetBrush(GraphPalette.Accent, 55), null,
                rect.Inflate(5), 13, 13);
        }

        context.DrawRectangle(GetBrush(fill), null, rect, 11, 11);

        // Loader accent strip.
        var stripRect = new Rect(rect.X + 1, rect.Y + 10, 4, rect.Height - 20);
        context.DrawRectangle(GetBrush(loaderColor), null, stripRect, 2, 2);

        var optional = model.IsOptionalDependency || model.IsRecommendedDependency;
        var borderPen = new Pen(
            GetBrush(optional ? GraphPalette.EdgeOptional : borderColor),
            selected ? 2.2 : 1.5,
            optional ? DashStyle.Dash : null);
        context.DrawRectangle(null, borderPen, rect, 11, 11);

        if (model.Source == Core.Models.EdgeSource.Manual)
        {
            var manualPen = new Pen(GetBrush(GraphPalette.EdgeManual), 1.2, DashStyle.Dash);
            context.DrawRectangle(null, manualPen, rect.Inflate(3), 13, 13);
        }

        // Project logo next to the title, matching the mod list.
        var iconRect = new Rect(rect.X + 11, rect.Y + (rect.Height - 34) / 2, 34, 34);
        var icon = ModIconCache.Get(model);
        if (icon is not null)
        {
            using (context.PushClip(new RoundedRect(iconRect, 8)))
                context.DrawImage(icon, iconRect);
        }
        else
        {
            context.DrawRectangle(GetBrush(loaderColor, 32), null, iconRect, 8, 8);
            var initial = node.Title.TrimStart().FirstOrDefault();
            var initialText = GetText(initial == default ? "?" : char.ToUpperInvariant(initial).ToString(),
                14, Semibold, loaderColor);
            context.DrawText(initialText, new Point(
                iconRect.X + (iconRect.Width - initialText.WidthIncludingTrailingWhitespace) / 2,
                iconRect.Y + (iconRect.Height - initialText.Height) / 2));
        }

        var textColor = IsLightTheme ? Color.Parse("#17202B") : Color.Parse("#E9EFF8");
        var secondaryColor = IsLightTheme ? Color.Parse("#5B6B7F") : Color.Parse("#94A3B8");
        if (model.Status is Core.Models.ModStatus.Missing or Core.Models.ModStatus.Conflict)
            secondaryColor = GraphPalette.Danger;
        else if (model.Status == Core.Models.ModStatus.VersionMismatch)
            secondaryColor = GraphPalette.Warning;

        if (Scene?.ShowLabels == true || selected)
        {
            if (_scale >= 0.4 || selected)
            {
                const double textX = 53;
                var title = GetText(Ellipsize(node.Title, 23), 12.5, Semibold, textColor);
                context.DrawText(title, new Point(rect.X + textX, rect.Y + 12));

                var subtitle = GetText(Ellipsize(node.Subtitle, 27), 10, Regular, secondaryColor);
                context.DrawText(subtitle, new Point(rect.X + textX, rect.Y + 38));

                if (model.IsOptionalDependency || model.IsRecommendedDependency)
                {
                    var optionalTag = GetText("可选", 9, Semibold, GraphPalette.TextMuted);
                    context.DrawText(optionalTag, new Point(rect.Right - 34, rect.Bottom - 16));
                }
                else if (model.IsEmbedded)
                {
                    var embedded = GetText("内嵌", 9, Regular, GraphPalette.TextMuted);
                    context.DrawText(embedded, new Point(rect.Right - 34, rect.Bottom - 16));
                }
            }
        }

        var badge = new Point(rect.Right - 14, rect.Y + 14);
        context.DrawEllipse(GetBrush(statusColor), null, badge, 4, 4);

        if (model.Status == Core.Models.ModStatus.Missing)
        {
            var missing = GetText("缺失", 9.5, Semibold, GraphPalette.Danger);
            context.DrawText(missing, new Point(rect.Right - 38, rect.Y + 30));
        }

        if (node.EmbeddedCount > 0)
        {
            var label = GetText($"内嵌 {node.EmbeddedCount}", 8.5, Regular, GraphPalette.TextMuted);
            var embeddedBadge = new Rect(rect.Right - 48, rect.Bottom - 18, 42, 13);
            context.DrawRectangle(GetBrush(GraphPalette.NodeFill, 210), null, embeddedBadge, 4, 4);
            context.DrawRectangle(null, new Pen(GetBrush(GraphPalette.NodeBorder), 1), embeddedBadge, 4, 4);
            context.DrawText(label, new Point(embeddedBadge.X + (embeddedBadge.Width - label.WidthIncludingTrailingWhitespace) / 2,
                embeddedBadge.Y + (embeddedBadge.Height - label.Height) / 2));
        }

        if (node.HubInDegree >= 5)
        {
            var label = GetText($"{node.HubInDegree} 入", 8.5, Regular, GraphPalette.TextMuted);
            var hubBadge = new Rect(rect.X + 10, rect.Bottom - 18, 38, 13);
            context.DrawRectangle(GetBrush(GraphPalette.NodeFill, 210), null, hubBadge, 4, 4);
            context.DrawRectangle(null, new Pen(GetBrush(GraphPalette.NodeBorder), 1), hubBadge, 4, 4);
            context.DrawText(label, new Point(hubBadge.X + (hubBadge.Width - label.WidthIncludingTrailingWhitespace) / 2,
                hubBadge.Y + (hubBadge.Height - label.Height) / 2));
        }
    }

    private void DrawSelectionBox(DrawingContext context)
    {
        if (!_boxSelecting) return;
        var rect = Normalize(_boxStart, _boxCurrent);
        context.DrawRectangle(
            GetBrush(GraphPalette.Accent, 38),
            new Pen(GetBrush(GraphPalette.Accent), 1, DashStyle.Dash),
            rect);
    }

    private void DrawEdgeLabel(DrawingContext context)
    {
        if (Scene is null || _selectedEdgeId is null) return;
        var edge = Scene.Edges.FirstOrDefault(e =>
            string.Equals(e.Id, _selectedEdgeId, StringComparison.OrdinalIgnoreCase));
        if (edge is null) return;

        var curve = GetCurve(edge);
        var worldMid = new Point(
            (curve.Start.X + 2 * curve.Control.X + curve.End.X) / 4,
            (curve.Start.Y + 2 * curve.Control.Y + curve.End.Y) / 4);
        var screen = WorldToScreen(worldMid);

        var label = GraphPalette.EdgeText(edge.Model.Type);
        if (!string.IsNullOrWhiteSpace(edge.Model.VersionRange)) label += " " + edge.Model.VersionRange;
        if (edge.Model.ManualOverride) label += " · 手动";

        var text = GetText(label, 10.5, Semibold, IsLightTheme ? Color.Parse("#17202B") : Colors.White);
        var width = text.WidthIncludingTrailingWhitespace + 14;
        var rect = new Rect(screen.X - width / 2, screen.Y - 26, width, 20);

        var fill = IsLightTheme ? Color.Parse("#FFFFFF") : Color.Parse("#1B2637");
        context.DrawRectangle(GetBrush(fill, 240), new Pen(GetBrush(GraphPalette.Accent), 1), rect, 5, 5);
        context.DrawText(text, new Point(rect.X + 7, rect.Y + 3));
    }

    // ------------------------------------------------------------------ geometry

    private (StreamGeometry Geometry, Point Start, Point Control, Point End) GetCurve(GraphEdgeVm edge)
    {
        var fromCenter = new Point(edge.From.X, edge.From.Y);
        var toCenter = new Point(edge.To.X, edge.To.Y);
        var dx = toCenter.X - fromCenter.X;
        var dy = toCenter.Y - fromCenter.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) length = 0.001;

        var ux = dx / length;
        var uy = dy / length;
        var start = RectEdge(fromCenter, ux, uy);
        var end = RectEdge(toCenter, -ux, -uy);

        var offset = 0.0;
        if (edge.ParallelIndex > 0)
        {
            var magnitude = 22 + (edge.ParallelIndex - 1) / 2 * 18;
            offset = edge.ParallelIndex % 2 == 1 ? magnitude : -magnitude;
        }

        var control = new Point(
            (start.X + end.X) / 2 - uy * offset,
            (start.Y + end.Y) / 2 + ux * offset);

        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(start, false);
            stream.QuadraticBezierTo(control, end);
        }

        return (geometry, start, control, end);
    }

    private static Point RectEdge(Point center, double ux, double uy)
    {
        var tx = Math.Abs(ux) < 0.0001 ? double.MaxValue : GraphNodeVm.HalfWidth / Math.Abs(ux);
        var ty = Math.Abs(uy) < 0.0001 ? double.MaxValue : GraphNodeVm.HalfHeight / Math.Abs(uy);
        var t = Math.Min(tx, ty);
        return new Point(center.X + ux * t, center.Y + uy * t);
    }

    private static Point QuadraticPoint(Point start, Point control, Point end, double t)
    {
        var oneMinus = 1 - t;
        return new Point(
            oneMinus * oneMinus * start.X + 2 * oneMinus * t * control.X + t * t * end.X,
            oneMinus * oneMinus * start.Y + 2 * oneMinus * t * control.Y + t * t * end.Y);
    }

    // ------------------------------------------------------------------ hit testing

    private GraphNodeVm? HitTestNode(Point screenPoint)
    {
        if (Scene is null) return null;
        var world = ScreenToWorld(screenPoint);
        for (var i = Scene.Nodes.Count - 1; i >= 0; i--)
        {
            if (Scene.Nodes[i].Bounds.Contains(world))
                return Scene.Nodes[i];
        }
        return null;
    }

    private GraphEdgeVm? HitTestEdge(Point screenPoint)
    {
        if (Scene is null) return null;
        var world = ScreenToWorld(screenPoint);
        var tolerance = 7 / _scale;

        foreach (var edge in Scene.Edges)
        {
            var curve = GetCurve(edge);
            for (var i = 0; i <= 20; i++)
            {
                var point = QuadraticPoint(curve.Start, curve.Control, curve.End, i / 20.0);
                var dx = point.X - world.X;
                var dy = point.Y - world.Y;
                if (dx * dx + dy * dy <= tolerance * tolerance)
                    return edge;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ helpers

    private void ZoomAt(Point screenPoint, double factor)
    {
        var world = ScreenToWorld(screenPoint);
        _scale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        _offsetX = screenPoint.X - world.X * _scale;
        _offsetY = screenPoint.Y - world.Y * _scale;
        InvalidateVisual();
    }

    private void CenterOn(GraphNodeVm node)
    {
        if (_scale < 0.6) _scale = 0.9;
        _offsetX = Bounds.Width / 2 - node.X * _scale;
        _offsetY = Bounds.Height / 2 - node.Y * _scale;
    }

    private Point ScreenToWorld(Point screen) =>
        new((screen.X - _offsetX) / _scale, (screen.Y - _offsetY) / _scale);

    private Point WorldToScreen(Point world) =>
        new(world.X * _scale + _offsetX, world.Y * _scale + _offsetY);

    private void RaiseSelection()
    {
        if (_suppressSelectionEvents) return;
        _suppressSelectionEvents = true;
        try
        {
            SelectionChanged?.Invoke(this, new NodeSelectionEventArgs
            {
                NodeIds = _selectedNodeIds.ToList(),
                EdgeId = _selectedEdgeId,
            });
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private bool IsLightTheme => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;

    private IBrush GetBrush(Color color, byte? alpha = null)
    {
        var final = alpha is null
            ? color
            : Color.FromArgb(alpha.Value, color.R, color.G, color.B);
        var key = final.ToUInt32();
        if (_brushCache.TryGetValue(key, out var brush)) return brush;
        brush = new SolidColorBrush(final);
        _brushCache[key] = brush;
        return brush;
    }

    private FormattedText GetText(string text, double size, Typeface typeface, Color color)
    {
        var key = new TextKey(text, size, typeface.Weight, color.ToUInt32());
        if (_textCache.TryGetValue(key, out var formatted)) return formatted;

        formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            GetBrush(color));

        if (_textCache.Count > 4000) _textCache.Clear();
        _textCache[key] = formatted;
        return formatted;
    }

    private static Rect Normalize(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static string Ellipsize(string text, int maxUnits)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var units = 0;
        var index = 0;
        while (index < text.Length)
        {
            var width = text[index] > 0x2E80 ? 2 : 1;
            if (units + width > maxUnits)
                return text[..Math.Max(0, index - 1)] + "…";
            units += width;
            index++;
        }

        return text;
    }

    private readonly record struct TextKey(string Text, double Size, FontWeight Weight, uint Color);
}
