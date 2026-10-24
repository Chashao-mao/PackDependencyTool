using SkiaSharp;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Models;

namespace PackDepTool.App.Services;

/// <summary>
/// Renders the currently visible relationship graph to a large PNG without
/// depending on the on-screen viewport or zoom level. The export uses its own
/// compact radial/grid layout so a focused graph is not stretched into a long strip.
/// </summary>
public static class GraphPngExporter
{
    private const double MaxWorldScale = 2.0;
    private const double MinWorldScale = 0.4;
    private const int MaxDimension = 18_000;
    private const long MaxPixels = 60_000_000;
    private const double NodeWidth = GraphNodeVm.Width;
    private const double NodeHeight = GraphNodeVm.Height;

    public static string Export(
        GraphScene scene,
        string path,
        string title,
        bool lightTheme,
        string? focusNodeId = null)
    {
        if (scene.Nodes.Count == 0)
            throw new InvalidOperationException("当前关系图没有可导出的节点。");

        var positions = BuildExportPositions(scene, focusNodeId);
        var halfWidth = NodeWidth / 2;
        var halfHeight = NodeHeight / 2;
        var minX = positions.Values.Min(position => position.X) - halfWidth;
        var minY = positions.Values.Min(position => position.Y) - halfHeight;
        var maxX = positions.Values.Max(position => position.X) + halfWidth;
        var maxY = positions.Values.Max(position => position.Y) + halfHeight;
        const double margin = 90;
        var worldWidth = Math.Max(360, maxX - minX + margin * 2);
        var worldHeight = Math.Max(240, maxY - minY + margin * 2);

        var scale = MaxWorldScale;
        while (worldWidth * scale > MaxDimension || worldHeight * scale > MaxDimension ||
               worldWidth * scale * worldHeight * scale > MaxPixels)
        {
            scale -= 0.05;
            if (scale <= MinWorldScale) break;
        }

        scale = Math.Max(MinWorldScale, scale);
        var width = Math.Clamp((int)Math.Ceiling(worldWidth * scale), 1, MaxDimension);
        var height = Math.Clamp((int)Math.Ceiling(worldHeight * scale), 1, MaxDimension);

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        var lightBackground = new SKColor(246, 249, 253);
        canvas.Clear(lightTheme ? lightBackground : new SKColor(15, 20, 27));

        var typeface = SKTypeface.FromFamilyName("Microsoft YaHei") ?? SKTypeface.Default;
        using var textPaint = new SKPaint { IsAntialias = true, Typeface = typeface };
        using var edgePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
        };
        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var strokePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
        };

        var offsetX = -minX * scale + margin * scale;
        var offsetY = -minY * scale + margin * scale;
        float X(double x) => (float)(x * scale + offsetX);
        float Y(double y) => (float)(y * scale + offsetY);

        var background = lightTheme ? SKColors.White : new SKColor(23, 36, 51);
        var border = lightTheme ? new SKColor(201, 213, 226) : new SKColor(46, 66, 88);
        var primary = lightTheme ? new SKColor(30, 41, 59) : new SKColor(233, 239, 248);
        var secondary = lightTheme ? new SKColor(71, 85, 105) : new SKColor(148, 163, 184);
        var muted = new SKColor(100, 116, 139);

        textPaint.TextSize = (float)(14 * Math.Max(1, scale));
        textPaint.Color = secondary;
        canvas.DrawText(title, (float)(margin * scale), (float)(32 * scale), textPaint);

        // Edges are straight radial/grid links. Straight lines stay readable even
        // when a focused graph contains many cross-relations.
        foreach (var edge in scene.Edges)
        {
            if (!positions.TryGetValue(edge.From.Id, out var fromPosition) ||
                !positions.TryGetValue(edge.To.Id, out var toPosition))
                continue;

            var forward = toPosition.X >= fromPosition.X;
            var fromX = X(forward ? fromPosition.X + halfWidth - 4 : fromPosition.X - halfWidth + 4);
            var toX = X(forward ? toPosition.X - halfWidth + 4 : toPosition.X + halfWidth - 4);
            var fromY = Y(fromPosition.Y);
            var toY = Y(toPosition.Y);
            var color = ToColor(edge.HasMissingEndpoint
                ? GraphPalette.Danger
                : edge.IsInactiveConflict
                    ? GraphPalette.TextMuted
                    : GraphPalette.EdgeColor(edge.Model.Type,
                        edge.Model.ManualOverride || edge.Model.Type == EdgeType.Manual));
            edgePaint.Color = color;
            edgePaint.StrokeWidth = (float)((edge.HasMissingEndpoint
                ? 1.8
                : edge.Model.Type == EdgeType.Incompatible ? 2.2 : 1.4) * Math.Max(0.7, scale));
            if (edge.HasMissingEndpoint || edge.Model.Type is EdgeType.Optional or EdgeType.Recommends)
                edgePaint.PathEffect = SKPathEffect.CreateDash(
                    [6f * (float)scale, 5f * (float)scale], 0);
            else
                edgePaint.PathEffect = null;

            canvas.DrawLine(fromX, fromY, toX, toY, edgePaint);
            edgePaint.PathEffect?.Dispose();
            edgePaint.PathEffect = null;

            var direction = Math.Sign(toX - fromX);
            if (direction == 0) direction = 1;
            using var arrow = new SKPath();
            arrow.MoveTo(toX, toY);
            arrow.LineTo(toX - direction * 8 * (float)scale, toY - 4 * (float)scale);
            arrow.LineTo(toX - direction * 8 * (float)scale, toY + 4 * (float)scale);
            arrow.Close();
            fillPaint.Color = color;
            canvas.DrawPath(arrow, fillPaint);
        }

        foreach (var node in scene.Nodes)
        {
            if (!positions.TryGetValue(node.Id, out var position)) continue;
            var rect = new SKRect(
                X(position.X - halfWidth), Y(position.Y - halfHeight),
                X(position.X + halfWidth), Y(position.Y + halfHeight));
            var radius = 12f * (float)scale;
            fillPaint.Color = background;
            canvas.DrawRoundRect(rect, radius, radius, fillPaint);

            var loaderColor = ToColor(GraphPalette.LoaderColor(node.Model.Loader));
            var statusColor = ToColor(GraphPalette.StatusColor(node.Model.Status));
            fillPaint.Color = loaderColor;
            canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top, rect.Left + 5 * (float)scale,
                rect.Bottom), radius, radius, fillPaint);

            strokePaint.Color = node.IsProblem ? statusColor : border;
            strokePaint.StrokeWidth = (float)((node.IsProblem ? 2 : 1.3) * Math.Max(0.7, scale));
            canvas.DrawRoundRect(rect, radius, radius, strokePaint);

            var iconSize = 28f * (float)scale;
            var iconLeft = rect.Left + 14 * (float)scale;
            var iconTop = rect.Top + 13 * (float)scale;
            var iconRect = new SKRect(iconLeft, iconTop, iconLeft + iconSize, iconTop + iconSize);
            var drewIcon = false;
            if (node.Model.IconData is { Length: > 0 })
            {
                try
                {
                    using var icon = SKBitmap.Decode(node.Model.IconData);
                    if (icon is not null)
                    {
                        canvas.Save();
                        using var clip = new SKRoundRect(iconRect, 5f * (float)scale);
                        canvas.ClipRoundRect(clip, SKClipOperation.Intersect, true);
                        canvas.DrawBitmap(icon, iconRect);
                        canvas.Restore();
                        drewIcon = true;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // Broken embedded icon; fall back to the initial.
                }
            }

            if (!drewIcon)
            {
                fillPaint.Color = loaderColor.WithAlpha(38);
                canvas.DrawRoundRect(iconRect, 5f * (float)scale, 5f * (float)scale, fillPaint);
                var initial = string.IsNullOrWhiteSpace(node.Title)
                    ? "?"
                    : node.Title.TrimStart()[..1].ToUpperInvariant();
                textPaint.TextSize = 12f * (float)scale;
                textPaint.Color = loaderColor;
                textPaint.FakeBoldText = true;
                canvas.DrawText(initial,
                    iconLeft + iconSize / 2 - textPaint.MeasureText(initial) / 2,
                    iconTop + iconSize * 0.7f, textPaint);
                textPaint.FakeBoldText = false;
            }

            var textLeft = iconLeft + iconSize + 9 * (float)scale;
            var textWidth = Math.Max(20, rect.Right - textLeft - 10 * (float)scale);
            textPaint.TextSize = 12f * (float)scale;
            textPaint.Color = primary;
            textPaint.FakeBoldText = true;
            canvas.DrawText(Truncate(node.Title, textPaint, (float)textWidth), textLeft,
                rect.Top + 25 * (float)scale, textPaint);
            textPaint.FakeBoldText = false;

            textPaint.TextSize = 9.5f * (float)scale;
            textPaint.Color = secondary;
            canvas.DrawText(Truncate($"{node.Model.Id} · {node.Model.DisplayVersion}", textPaint,
                (float)textWidth), textLeft, rect.Top + 41 * (float)scale, textPaint);

            textPaint.TextSize = 8.8f * (float)scale;
            textPaint.Color = muted;
            canvas.DrawText(Truncate($"{GraphPalette.SideText(node.Model.Side)} · " +
                                     $"{GraphPalette.LoaderText(node.Model.Loader)} · " +
                                     $"{GraphPalette.StatusText(node.Model.Status)}",
                textPaint, (float)textWidth), textLeft, rect.Top + 55 * (float)scale, textPaint);

            fillPaint.Color = statusColor;
            canvas.DrawCircle(rect.Right - 13 * (float)scale, rect.Top + 14 * (float)scale,
                4 * (float)scale, fillPaint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                            ?? throw new InvalidOperationException("PNG 编码失败。");
        using var output = File.Create(path);
        encoded.SaveTo(output);
        return path;
    }

    private static Dictionary<string, ExportPoint> BuildExportPositions(
        GraphScene scene,
        string? focusNodeId)
    {
        var positions = new Dictionary<string, ExportPoint>(StringComparer.OrdinalIgnoreCase);
        var nodes = scene.Nodes;
        if (nodes.Count == 0) return positions;

        var degree = nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var adjacency = nodes.ToDictionary(node => node.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var edge in scene.Edges)
        {
            if (!degree.ContainsKey(edge.From.Id) || !degree.ContainsKey(edge.To.Id) ||
                string.Equals(edge.From.Id, edge.To.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            degree[edge.From.Id]++;
            degree[edge.To.Id]++;
            adjacency[edge.From.Id].Add(edge.To.Id);
            adjacency[edge.To.Id].Add(edge.From.Id);
        }

        // A focused graph has a meaningful center; otherwise use the largest hub.
        var rootId = focusNodeId is not null && degree.ContainsKey(focusNodeId)
            ? focusNodeId
            : degree.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .First().Key;

        if (nodes.Count <= 140 && scene.Edges.Count > 0)
        {
            var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [rootId] = 0 };
            var queue = new Queue<string>();
            queue.Enqueue(rootId);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var next in adjacency[current])
                {
                    if (levels.ContainsKey(next)) continue;
                    levels[next] = levels[current] + 1;
                    queue.Enqueue(next);
                }
            }

            var groups = levels
                .GroupBy(pair => pair.Value)
                .OrderBy(group => group.Key)
                .ToDictionary(group => group.Key,
                    group => group.Select(pair => pair.Key)
                        .OrderByDescending(id => degree[id])
                        .ThenBy(id => id, StringComparer.Ordinal)
                        .ToList());

            positions[rootId] = new ExportPoint(0, 0);
            var maxRadius = 0.0;
            for (var level = 1; level <= groups.Keys.DefaultIfEmpty(0).Max(); level++)
            {
                if (!groups.TryGetValue(level, out var ids) || ids.Count == 0) continue;
                var circumferenceRadius = ids.Count * (NodeWidth + 20) / (Math.PI * 2);
                var radius = Math.Max(360 + (level - 1) * 290, circumferenceRadius + 40);
                maxRadius = Math.Max(maxRadius, radius);
                var start = -Math.PI / 2 + level * 0.35;
                for (var index = 0; index < ids.Count; index++)
                {
                    var angle = start + index * Math.PI * 2 / ids.Count;
                    positions[ids[index]] = new ExportPoint(
                        Math.Cos(angle) * radius, Math.Sin(angle) * radius);
                }
            }

            var unvisited = nodes.Where(node => !positions.ContainsKey(node.Id)).ToList();
            if (unvisited.Count > 0)
            {
                var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(unvisited.Count)));
                var startY = maxRadius + 310;
                for (var index = 0; index < unvisited.Count; index++)
                {
                    var row = index / columns;
                    var column = index % columns;
                    positions[unvisited[index].Id] = new ExportPoint(
                        (column - (columns - 1) / 2.0) * (NodeWidth + 40),
                        startY + row * (NodeHeight + 32));
                }
            }

            return positions;
        }

        // Very large filtered graphs: pack into a square grid rather than one long row.
        var gridColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(nodes.Count * 1.15)));
        var rows = (int)Math.Ceiling(nodes.Count / (double)gridColumns);
        for (var index = 0; index < nodes.Count; index++)
        {
            var row = index / gridColumns;
            var column = index % gridColumns;
            positions[nodes[index].Id] = new ExportPoint(
                (column - (gridColumns - 1) / 2.0) * (NodeWidth + 42),
                (row - (rows - 1) / 2.0) * (NodeHeight + 34));
        }

        return positions;
    }

    private static SKColor ToColor(Avalonia.Media.Color color) =>
        new(color.R, color.G, color.B, color.A);

    private static string Truncate(string text, SKPaint paint, float width)
    {
        if (string.IsNullOrEmpty(text) || paint.MeasureText(text) <= width) return text;
        const string ellipsis = "…";
        var result = text;
        while (result.Length > 1 && paint.MeasureText(result + ellipsis) > width)
            result = result[..^1];
        return result + ellipsis;
    }

    private readonly record struct ExportPoint(double X, double Y);
}
