using PackDepTool.Core.Analysis;
using Avalonia;
using Avalonia.Media;
using PackDepTool.Core.Models;

namespace PackDepTool.App.ViewModels;

public static class GraphPalette
{
    public static readonly Color Accent = Color.Parse("#3B82F6");
    public static readonly Color AccentSoft = Color.Parse("#172A46");
    public static readonly Color TextPrimary = Color.Parse("#E9EFF8");
    public static readonly Color TextSecondary = Color.Parse("#94A3B8");
    public static readonly Color TextMuted = Color.Parse("#64748B");
    public static readonly Color NodeFill = Color.Parse("#172433");
    public static readonly Color NodeFillLight = Color.Parse("#FFFFFF");
    public static readonly Color NodeBorder = Color.Parse("#2E4258");
    public static readonly Color EdgeRequires = Color.Parse("#3B82F6");
    public static readonly Color EdgeOptional = Color.Parse("#94A3B8");
    public static readonly Color EdgeIncompatible = Color.Parse("#F87171");
    public static readonly Color EdgeEmbedded = Color.Parse("#A78BFA");
    public static readonly Color EdgeRecommends = Color.Parse("#34D399");
    public static readonly Color EdgeManual = Color.Parse("#F5C542");
    public static readonly Color Danger = Color.Parse("#F87171");
    public static readonly Color Warning = Color.Parse("#FBBF24");
    public static readonly Color Success = Color.Parse("#34D399");
    public static readonly Color Select = Color.Parse("#E9EFF8");

    public static Color LoaderColor(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => Color.Parse("#60A5FA"),
        LoaderKind.Quilt => Color.Parse("#B36BE0"),
        LoaderKind.Forge => Color.Parse("#E06C3C"),
        LoaderKind.NeoForge => Color.Parse("#D9A21B"),
        _ => Color.Parse("#8A94A6"),
    };

    public static Color StatusColor(ModStatus status) => status switch
    {
        ModStatus.Missing => Danger,
        ModStatus.Conflict => Danger,
        ModStatus.VersionMismatch => Warning,
        ModStatus.Unknown => Color.Parse("#8A94A6"),
        _ => Success,
    };

    public static Color EdgeColor(EdgeType type, bool manual) => manual ? EdgeManual : type switch
    {
        EdgeType.Requires => EdgeRequires,
        EdgeType.Optional => EdgeOptional,
        EdgeType.Incompatible => EdgeIncompatible,
        EdgeType.Embedded => EdgeEmbedded,
        EdgeType.Recommends => EdgeRecommends,
        EdgeType.Manual => EdgeManual,
        _ => EdgeOptional,
    };

    public static IBrush StatusBrush(ModStatus status) => new SolidColorBrush(StatusColor(status));

    public static string StatusText(ModStatus status) => status switch
    {
        ModStatus.Ok => "正常",
        ModStatus.Missing => "缺失",
        ModStatus.VersionMismatch => "版本不匹配",
        ModStatus.Conflict => "冲突",
        ModStatus.Unknown => "未知",
        _ => status.ToString(),
    };

    public static string LoaderText(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "未知",
    };

    public static string EdgeText(EdgeType type) => type switch
    {
        EdgeType.Requires => "必需",
        EdgeType.Optional => "可选",
        EdgeType.Incompatible => "不兼容",
        EdgeType.Embedded => "内嵌",
        EdgeType.Recommends => "推荐",
        EdgeType.Manual => "手动",
        _ => type.ToString(),
    };

    public static string SideText(ModSide side) => side switch
    {
        ModSide.Both => "双端",
        ModSide.Client => "客户端",
        ModSide.Server => "服务端",
        _ => "未知",
    };
}

/// <summary>Render-ready graph node. X/Y are world coordinates.</summary>
public sealed class GraphNodeVm
{
    public const double Width = 212;
    public const double Height = 70;
    public const double HalfWidth = Width / 2;
    public const double HalfHeight = Height / 2;

    public GraphNodeVm(ModNode model, double x, double y)
    {
        Model = model;
        X = x;
        Y = y;
    }

    public ModNode Model { get; }
    public double X { get; set; }
    public double Y { get; set; }
    public bool IsSearchMatch { get; set; } = true;
    public bool IsHovered { get; set; }
    public int EmbeddedCount { get; set; }
    public int HubInDegree { get; set; }

    public string Id => Model.Id;
    public string Title => !string.IsNullOrWhiteSpace(Model.NameZh)
        ? Model.NameZh!
        : (string.IsNullOrWhiteSpace(Model.Name) ? Model.Id : Model.Name);
    public string Subtitle => !string.IsNullOrWhiteSpace(Model.NameZh)
        ? $"{Model.Name} · {Model.DisplayVersion}"
        : $"{Model.Id} · {Model.DisplayVersion}";
    public bool IsProblem => Model.Status is ModStatus.Missing or ModStatus.Conflict or ModStatus.VersionMismatch;
    public Rect Bounds => new(X - HalfWidth, Y - HalfHeight, Width, Height);
}

public sealed class GraphEdgeVm
{
    public GraphEdgeVm(DependencyEdge model, GraphNodeVm from, GraphNodeVm to, int parallelIndex)
    {
        Model = model;
        From = from;
        To = to;
        ParallelIndex = parallelIndex;
        IsInactiveConflict = model.Type == Core.Models.EdgeType.Incompatible &&
            (to.Model.Version.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
             VersionRange.Satisfies(model.VersionRange, to.Model.Version) != true);
    }

    public DependencyEdge Model { get; }
    public GraphNodeVm From { get; }
    public GraphNodeVm To { get; }
    public int ParallelIndex { get; }
    public bool IsInactiveConflict { get; }
    public bool HasMissingEndpoint => From.Model.Status == ModStatus.Missing ||
                                       To.Model.Status == ModStatus.Missing;
    public bool IsSearchMatch { get; set; } = true;
    public bool IsHighlighted { get; set; }
    public string Id => Model.Id;
}

/// <summary>Immutable snapshot handed to <c>DependencyGraphView</c>.</summary>
public sealed class GraphScene
{
    public bool FocusEdgesOnly { get; set; } = true;

    public IReadOnlyList<GraphNodeVm> Nodes { get; init; } = [];
    public IReadOnlyList<GraphEdgeVm> Edges { get; init; } = [];
    public string SearchTerm { get; init; } = string.Empty;
    public bool ShowLabels { get; init; } = true;

    public Rect ContentBounds
    {
        get
        {
            if (Nodes.Count == 0) return new Rect(0, 0, 0, 0);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var node in Nodes)
            {
                minX = Math.Min(minX, node.X - GraphNodeVm.HalfWidth);
                minY = Math.Min(minY, node.Y - GraphNodeVm.HalfHeight);
                maxX = Math.Max(maxX, node.X + GraphNodeVm.HalfWidth);
                maxY = Math.Max(maxY, node.Y + GraphNodeVm.HalfHeight);
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }
}
