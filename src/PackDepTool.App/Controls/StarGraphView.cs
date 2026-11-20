using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PackDepTool.App.Services;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;

namespace PackDepTool.App.Controls;

public sealed class StarGraphView : Control
{
    private const double NodeWidth = 170;
    private const double NodeHeight = 52;
    private const double CenterWidth = 220;
    private const double CenterHeight = 70;
    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Semibold = new("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);

    public static readonly StyledProperty<StarScene?> SceneProperty =
        AvaloniaProperty.Register<StarGraphView, StarScene?>(nameof(Scene));

    public event Action<string>? RecenterRequested;

    private readonly List<(StarNodeVm Node, Rect Rect)> _hitRects = [];
    private readonly Dictionary<string, Point> _positions = new(StringComparer.OrdinalIgnoreCase);
    private double _zoom = 1;
    private Point _pan;
    private Point _lastPointer;
    private bool _dragging;
    private bool _moved;
    private StarNodeVm? _hover;
    private StarNodeVm? _dragNode;
    private readonly Dictionary<string, Point> _nodeOffsets = new(StringComparer.OrdinalIgnoreCase);

    public StarScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public StarGraphView()
    {
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0C0E12")), new Rect(Bounds.Size));
        var scene = Scene;
        _hitRects.Clear();
        _positions.Clear();
        if (scene is null || scene.IsEmpty) return;

        Layout(scene);
        using (context.PushTransform(Matrix.CreateTranslation(_pan.X, _pan.Y) *
                                     Matrix.CreateScale(_zoom, _zoom)))
        {
            DrawEdges(context, scene);
            DrawNodes(context, scene);
        }

        if (scene.HiddenCount > 0)
        {
            var text = Format($"+{scene.HiddenCount} 个关系未显示", 10, Brushes.Gray, Regular);
            context.DrawText(text, new Point(16, Bounds.Height - 24));
        }
    }

    private void Layout(StarScene scene)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var clientWidth = topLevel?.ClientSize.Width ?? Bounds.Width;
        var clientHeight = topLevel?.ClientSize.Height ?? Bounds.Height;
        var viewportWidth = Math.Max(420, Math.Min(Bounds.Width, clientWidth - 160));
        var viewportHeight = Math.Max(320, Math.Min(Bounds.Height, clientHeight - 210));
        var center = new Point(viewportWidth / 2, viewportHeight / 2);
        _positions[scene.Center.Model.Id] = center;

        var rings = scene.Nodes.Where(node => !ReferenceEquals(node, scene.Center))
            .GroupBy(node => Math.Max(1, node.Ring))
            .OrderBy(group => group.Key);
        // Keep the whole star inside the viewport: the ring radius is derived
        // from the shorter viewport edge, never from an unbounded label width.
        var maxRadius = Math.Max(140, Math.Min(viewportWidth, viewportHeight) * 0.38);
        foreach (var ring in rings)
        {
            var nodes = ring.ToList();
            var radius = Math.Min(maxRadius, 190 + (ring.Key - 1) * 180);
            for (var i = 0; i < nodes.Count; i++)
            {
                var angle = 2 * Math.PI * i / nodes.Count - Math.PI / 2 + (ring.Key - 1) * 0.2;
                _positions[nodes[i].Model.Id] = new Point(
                    center.X + Math.Cos(angle) * radius,
                    center.Y + Math.Sin(angle) * radius);
            }
        }
        foreach (var node in scene.Nodes)
        {
            if (_nodeOffsets.TryGetValue(node.Model.Id, out var offset) &&
                _positions.TryGetValue(node.Model.Id, out var basePoint))
                _positions[node.Model.Id] = basePoint + offset;
        }
    }

    private void DrawEdges(DrawingContext context, StarScene scene)
    {
        foreach (var edge in scene.Edges)
        {
            if (!_positions.TryGetValue(edge.From.Model.Id, out var from) ||
                !_positions.TryGetValue(edge.To.Model.Id, out var to)) continue;
            var pen = new Pen(new SolidColorBrush(edge.Color, 0.75), edge.Type == EdgeType.Requires ? 2.0 : 1.3,
                edge.Type is EdgeType.Optional or EdgeType.Recommends ? DashStyle.Dash : null);
            var mid = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var curve = new Point(mid.X - dy / length * 22, mid.Y + dx / length * 22);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(from, false);
                g.QuadraticBezierTo(curve, to);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
            DrawArrow(context, to, curve, edge.Color);
        }
    }

    private static void DrawArrow(DrawingContext context, Point tip, Point control, Color color)
    {
        var dx = tip.X - control.X;
        var dy = tip.Y - control.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.1) return;
        var ux = dx / length;
        var uy = dy / length;
        var p1 = new Point(tip.X - ux * 10 - uy * 4, tip.Y - uy * 10 + ux * 4);
        var p2 = new Point(tip.X - ux * 10 + uy * 4, tip.Y - uy * 10 - ux * 4);
        context.DrawLine(new Pen(new SolidColorBrush(color, 0.9), 1.5), tip, p1);
        context.DrawLine(new Pen(new SolidColorBrush(color, 0.9), 1.5), tip, p2);
    }

    private void DrawNodes(DrawingContext context, StarScene scene)
    {
        foreach (var node in scene.Nodes)
        {
            if (!_positions.TryGetValue(node.Model.Id, out var point)) continue;
            var isCenter = ReferenceEquals(node, scene.Center);
            var width = isCenter ? CenterWidth : NodeWidth;
            var height = isCenter ? CenterHeight : NodeHeight;
            var rect = new Rect(point.X - width / 2, point.Y - height / 2, width, height);
            _hitRects.Add((node, rect));

            var fill = new SolidColorBrush(Color.Parse(isCenter ? "#241A26" : "#161B24"));
            if (isCenter)
                context.DrawRectangle(new SolidColorBrush(GraphPalette.Accent, 0.22), null, rect.Inflate(8), 16, 16);
            context.DrawRectangle(fill,
                new Pen(new SolidColorBrush(isCenter ? GraphPalette.Accent : node.LoaderColor), isCenter ? 2.4 : 1.2),
                rect, isCenter ? 14 : 10);
            context.DrawRectangle(new SolidColorBrush(node.LoaderColor), null,
                new Rect(rect.X + 1, rect.Y + 10, 4, rect.Height - 20), 2, 2);

            var iconRect = new Rect(rect.X + 10, rect.Y + (rect.Height - 32) / 2, 32, 32);
            var icon = ModIconCache.Get(node.Model);
            if (icon is not null)
            {
                using (context.PushClip(new RoundedRect(iconRect, 7)))
                    context.DrawImage(icon, iconRect);
            }
            else
            {
                context.DrawRectangle(new SolidColorBrush(node.LoaderColor, 0.3), null, iconRect, 7, 7);
            }

            var textX = rect.X + 50;
            var title = Format(Ellipsize(node.Title, isCenter ? 18 : 15), isCenter ? 13.5 : 11.5,
                new SolidColorBrush(Color.Parse("#F3F5F8")), isCenter ? Semibold : Semibold);
            context.DrawText(title, new Point(textX, rect.Y + 11));
            var subtitle = Format(Ellipsize(node.Subtitle, isCenter ? 22 : 18), isCenter ? 10 : 9,
                new SolidColorBrush(Color.Parse("#98A2B3")), Regular);
            context.DrawText(subtitle, new Point(textX, rect.Y + (isCenter ? 36 : 32)));

            var dot = isCenter ? 9 : 6;
            context.DrawEllipse(new SolidColorBrush(node.StatusColor), null,
                new Point(rect.Right - 12, rect.Y + 12), dot / 2, dot / 2);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _lastPointer = e.GetPosition(this);
        _moved = false;
        var hit = HitTest(e.GetPosition(this));
        if (hit is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragNode = hit;
            _dragging = false;
            e.Pointer.Capture(this);
            return;
        }
        if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed ||
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_dragNode is not null)
        {
            var delta = point - _lastPointer;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 1) _moved = true;
            _nodeOffsets[_dragNode.Model.Id] =
                (_nodeOffsets.TryGetValue(_dragNode.Model.Id, out var previous) ? previous : default) + delta;
            _lastPointer = point;
            InvalidateVisual();
            return;
        }
        if (_dragging)
        {
            var delta = point - _lastPointer;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 2) _moved = true;
            _pan += delta;
            _lastPointer = point;
            InvalidateVisual();
            return;
        }
        var hover = HitTest(point);
        if (!ReferenceEquals(hover, _hover))
        {
            _hover = hover;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        var clickedNode = _dragNode;
        _dragNode = null;
        e.Pointer.Capture(null);
        if (_moved) return;
        var node = clickedNode ?? HitTest(e.GetPosition(this));
        if (node is not null) RecenterRequested?.Invoke(node.Model.Id);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1), 0.55, 1.8);
        InvalidateVisual();
        e.Handled = true;
    }

    private StarNodeVm? HitTest(Point point)
    {
        var world = new Point((point.X - _pan.X) / _zoom, (point.Y - _pan.Y) / _zoom);
        for (var i = _hitRects.Count - 1; i >= 0; i--)
            if (_hitRects[i].Rect.Contains(world)) return _hitRects[i].Node;
        return null;
    }

    private static string Ellipsize(string text, int max)
    {
        var value = text ?? string.Empty;
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
    }

    private static FormattedText Format(string text, double size, IBrush brush, Typeface typeface) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, size, brush);
}
