using SkiaSharp;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Models;

namespace PackDepTool.App.Services;

/// <summary>
/// Renders the currently visible relationship graph to a large PNG without
/// depending on the on-screen viewport or zoom level.
/// </summary>
public static class GraphPngExporter
{
    private const double MaxWorldScale = 2.0;
    private const double MinWorldScale = 0.45;
    private const int MaxDimension = 18_000;
    private const long MaxPixels = 60_000_000;

    public static string Export(
        GraphScene scene,
        string path,
        string title,
        bool lightTheme)
    {
        if (scene.Nodes.Count == 0)
            throw new InvalidOperationException("当前关系图没有可导出的节点。");

        var minX = scene.Nodes.Min(node => node.Bounds.X);
        var minY = scene.Nodes.Min(node => node.Bounds.Y);
        var maxX = scene.Nodes.Max(node => node.Bounds.Right);
        var maxY = scene.Nodes.Max(node => node.Bounds.Bottom);
        const double margin = 80;
        var worldWidth = Math.Max(320, maxX - minX + margin * 2);
        var worldHeight = Math.Max(200, maxY - minY + margin * 2);

        var scale = MaxWorldScale;
        while (worldWidth * scale > MaxDimension || worldHeight * scale > MaxDimension ||
               worldWidth * scale * worldHeight * scale > MaxPixels)
        {
            scale -= 0.1;
            if (scale < MinWorldScale) break;
        }

        scale = Math.Max(MinWorldScale, scale);
        var width = Math.Clamp((int)Math.Ceiling(worldWidth * scale), 1, MaxDimension);
        var height = Math.Clamp((int)Math.Ceiling(worldHeight * scale), 1, MaxDimension);

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(lightTheme ? new SKColor(246, 249, 253) : new SKColor(15, 20, 27));

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
        float X(float x) => (float)(x * scale + offsetX);
        float Y(float y) => (float)(y * scale + offsetY);

        var background = lightTheme ? SKColors.White : new SKColor(23, 36, 51);
        var border = lightTheme ? new SKColor(201, 213, 226) : new SKColor(46, 66, 88);
        var primary = lightTheme ? new SKColor(30, 41, 59) : new SKColor(233, 239, 248);
        var secondary = lightTheme ? new SKColor(71, 85, 105) : new SKColor(148, 163, 184);
        var muted = lightTheme ? new SKColor(100, 116, 139) : new SKColor(100, 116, 139);

        // A small caption keeps very large exports identifiable when opened alone.
        textPaint.TextSize = (float)(14 * Math.Max(1, scale));
        textPaint.Color = secondary;
        canvas.DrawText(title, (float)(margin * scale), (float)(32 * scale), textPaint);

        foreach (var edge in scene.Edges)
        {
            var from = edge.From;
            var to = edge.To;
            var forward = to.X >= from.X;
            var fromX = X((float)(forward ? from.Bounds.Right - 4 : from.Bounds.X + 4));
            var toX = X((float)(forward ? to.Bounds.X + 4 : to.Bounds.Right - 4));
            var fromY = Y((float)from.Y);
            var toY = Y((float)to.Y);
            var color = ToColor(GraphPalette.EdgeColor(edge.Model.Type,
                edge.Model.ManualOverride || edge.Model.Type == EdgeType.Manual));
            edgePaint.Color = color;
            edgePaint.StrokeWidth = (float)((edge.Model.Type == EdgeType.Incompatible ? 2.2 : 1.4) *
                                            Math.Max(0.7, scale));
            edgePaint.PathEffect = edge.Model.Type is EdgeType.Optional or EdgeType.Recommends
                ? SKPathEffect.CreateDash([6f * (float)scale, 5f * (float)scale], 0)
                : null;

            using var curve = new SKPath();
            curve.MoveTo(fromX, fromY);
            var controlDistance = Math.Max(40, Math.Abs(toX - fromX) * 0.45f);
            curve.CubicTo(fromX + (forward ? controlDistance : -controlDistance), fromY,
                toX + (forward ? -controlDistance : controlDistance), toY,
                toX, toY);
            canvas.DrawPath(curve, edgePaint);
            edgePaint.PathEffect?.Dispose();
            edgePaint.PathEffect = null;

            // Arrowhead at the dependency side.
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
            var rect = new SKRect(
                X((float)node.Bounds.X), Y((float)node.Bounds.Y),
                X((float)node.Bounds.Right), Y((float)node.Bounds.Bottom));
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
            fillPaint.Color = loaderColor.WithAlpha(38);
            canvas.DrawRoundRect(new SKRect(iconLeft, iconTop, iconLeft + iconSize, iconTop + iconSize),
                5f * (float)scale, 5f * (float)scale, fillPaint);
            textPaint.TextSize = 12f * (float)scale;
            textPaint.Color = loaderColor;
            textPaint.FakeBoldText = true;
            var initial = string.IsNullOrWhiteSpace(node.Title) ? "?" : node.Title.TrimStart()[..1].ToUpperInvariant();
            canvas.DrawText(initial, iconLeft + iconSize / 2 - textPaint.MeasureText(initial) / 2,
                iconTop + iconSize * 0.7f, textPaint);
            textPaint.FakeBoldText = false;

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
            canvas.DrawText(Truncate($"{GraphPalette.SideText(node.Model.Side)}{(node.Model.SideInferred ? "*" : string.Empty)} · " +
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
}
