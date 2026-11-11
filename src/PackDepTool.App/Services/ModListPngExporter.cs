using PackDepTool.Core.Models;
using PackDepTool.App.ViewModels;
using SkiaSharp;

namespace PackDepTool.App.Services;

/// <summary>
/// Renders the complete mod list, not just the visible viewport, into one PNG.
/// Rows follow the list order; dependency edges are drawn in the left gutter.
/// </summary>
public static class ModListPngExporter
{
    public static string Export(
        IReadOnlyList<NodeItemViewModel> items,
        GraphDocument graph,
        string path,
        string title,
        bool lightTheme)
    {
        if (items.Count == 0) throw new InvalidOperationException("当前列表没有可导出的模组。");

        var rowHeight = items.Count > 800 ? 28 : items.Count > 400 ? 32 : 40;
        var width = 1600;
        var headerHeight = 92;
        var height = Math.Min(32000, headerHeight + items.Count * rowHeight + 34);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        var background = lightTheme ? new SKColor(246, 249, 253) : new SKColor(15, 20, 27);
        var panel = lightTheme ? SKColors.White : new SKColor(24, 31, 42);
        var text = lightTheme ? new SKColor(30, 38, 50) : new SKColor(225, 231, 240);
        var muted = lightTheme ? new SKColor(105, 116, 135) : new SKColor(150, 163, 181);
        var border = lightTheme ? new SKColor(220, 227, 237) : new SKColor(48, 60, 78);
        canvas.Clear(background);

        var typeface = SKTypeface.FromFamilyName("Microsoft YaHei") ?? SKTypeface.Default;
        using var titlePaint = new SKPaint { IsAntialias = true, Typeface = typeface, Color = text, TextSize = 20 };
        using var textPaint = new SKPaint { IsAntialias = true, Typeface = typeface, Color = text, TextSize = 12 };
        using var smallPaint = new SKPaint { IsAntialias = true, Typeface = typeface, Color = muted, TextSize = 10 };
        using var linePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f };
        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = border, StrokeWidth = 1 };

        canvas.DrawText(title, 24, 35, titlePaint);
        canvas.DrawText($"共 {items.Count} 个模组 · {graph.Edges.Count(e => e.Type != EdgeType.Manual)} 条关系 · PackDependencyTool",
            24, 58, smallPaint);
        canvas.DrawLine(0, 74, width, 74, stroke);

        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++) indexById[items[i].Id] = i;

        var railColors = new Dictionary<EdgeType, SKColor>
        {
            [EdgeType.Requires] = new SKColor(48, 140, 235),
            [EdgeType.Optional] = new SKColor(90, 180, 170),
            [EdgeType.Recommends] = new SKColor(150, 130, 220),
            [EdgeType.Embedded] = new SKColor(235, 170, 70),
            [EdgeType.Incompatible] = new SKColor(225, 85, 85),
            [EdgeType.Manual] = new SKColor(140, 150, 165),
        };

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var y = headerHeight + i * rowHeight;
            var rowCenter = y + rowHeight / 2f;
            fillPaint.Color = i % 2 == 0 ? panel : background;
            canvas.DrawRect(0, y, width, rowHeight, fillPaint);
            canvas.DrawLine(0, y + rowHeight, width, y + rowHeight, stroke);

            var model = item.Model;
            var color = model.Status switch
            {
                ModStatus.Missing => new SKColor(225, 85, 85),
                ModStatus.Conflict => new SKColor(225, 140, 70),
                ModStatus.VersionMismatch => new SKColor(220, 180, 60),
                _ => new SKColor(80, 175, 120),
            };
            fillPaint.Color = color;
            canvas.DrawCircle(28, rowCenter, 5, fillPaint);

            canvas.DrawText((i + 1).ToString(), 48, rowCenter + 4, smallPaint);
            var iconRect = new SKRect(72, rowCenter - 13, 98, rowCenter + 13);
            using (var jarIcon = model.IconData is { Length: > 0 } iconBytes
                       ? SKBitmap.Decode(iconBytes)
                       : null)
            {
                if (jarIcon is not null)
                {
                    canvas.Save();
                    canvas.ClipRoundRect(new SKRoundRect(iconRect, 5));
                    canvas.DrawBitmap(jarIcon, iconRect);
                    canvas.Restore();
                }
                else
                {
                    canvas.DrawText(item.Initial, 78, rowCenter + 4, textPaint);
                }
            }
            canvas.DrawText(Trim(item.Name, 46), 108, rowCenter + 4, textPaint);
            canvas.DrawText(item.Subtitle, 108, rowCenter + 17, smallPaint);
            canvas.DrawText(item.LoaderText, 760, rowCenter + 4, smallPaint);
            canvas.DrawText(Trim(item.Version, 24), 900, rowCenter + 4, smallPaint);
            canvas.DrawText(Trim(item.DependencySummary, 28), 1080, rowCenter + 4, smallPaint);
            canvas.DrawText(item.SideText, 1320, rowCenter + 4, smallPaint);
            canvas.DrawText(item.StatusText, 1420, rowCenter + 4, smallPaint);

            var edgeIndex = 0;
            foreach (var edge in graph.Edges)
            {
                if (edge.Type == EdgeType.Manual) continue;
                if (!string.Equals(edge.From, item.Id, StringComparison.OrdinalIgnoreCase)) continue;
                if (!indexById.TryGetValue(edge.To, out var targetIndex)) continue;
                var targetY = headerHeight + targetIndex * rowHeight + rowHeight / 2f;
                var x = 14 + (edgeIndex++ % 5) * 3f;
                linePaint.Color = railColors.GetValueOrDefault(edge.Type, muted);
                using var edgePath = new SKPath();
                edgePath.MoveTo(x, rowCenter);
                var mid = (rowCenter + targetY) / 2;
                edgePath.CubicTo(x, mid, x, mid, x, targetY);
                canvas.DrawPath(edgePath, linePaint);
            }
        }

        using var legendPaint = new SKPaint { IsAntialias = true, Typeface = typeface, Color = muted, TextSize = 10 };
        canvas.DrawText("关系线：蓝=必需  青=可选  紫=推荐  橙=内嵌  红=冲突", 24, height - 14, legendPaint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
        data.SaveTo(stream);
        return path;
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..Math.Max(1, max - 1)] + "…";
}
