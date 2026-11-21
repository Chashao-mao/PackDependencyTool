using Avalonia.Media;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;

namespace PackDepTool.App.ViewModels;

/// <summary>
/// Immutable render snapshot for <c>StarGalaxyView</c>. World positions come from
/// <see cref="StarGalaxyLayoutEngine"/> once per data change; only <see cref="GalaxyNodeVm.X"/>,
/// <see cref="GalaxyNodeVm.Y"/> and <see cref="GalaxyNodeVm.LabelRect"/> change while dragging.
/// </summary>
public sealed class GalaxyScene
{
    public static readonly Color[] GalaxyColors =
    [
        Color.Parse("#F5C542"),
        Color.Parse("#60A5FA"),
        Color.Parse("#34D399"),
        Color.Parse("#A78BFA"),
        Color.Parse("#F472B6"),
        Color.Parse("#FB923C"),
        Color.Parse("#22D3EE"),
        Color.Parse("#F87171"),
    ];

    public required LayoutRect Bounds { get; init; }
    public IReadOnlyList<GalaxyNodeVm> Nodes { get; init; } = [];
    public IReadOnlyList<GalaxyInfoVm> Galaxies { get; init; } = [];
    public IReadOnlyList<GalaxyEdgeVm> Edges { get; init; } = [];
    public IReadOnlyList<StarGalaxyCrossLink> CrossLinks { get; init; } = [];
    public IReadOnlyDictionary<string, GalaxyNodeVm> ById { get; init; } =
        new Dictionary<string, GalaxyNodeVm>(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Nodes.Count == 0;
    public int HiddenCount { get; init; }

    public static GalaxyScene From(
        StarGalaxyLayout layout,
        IReadOnlyList<ModNode> nodes,
        IReadOnlyList<DependencyEdge> edges,
        StarGalaxyOptions options)
    {
        var byId = new Dictionary<string, ModNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id)) continue;
            byId.TryAdd(node.Id, node);
        }

        var optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompatible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges)
        {
            if (edge.Type is EdgeType.Optional or EdgeType.Recommends)
            {
                optional.Add(edge.From);
                optional.Add(edge.To);
            }
            else if (edge.Type == EdgeType.Incompatible)
            {
                incompatible.Add(edge.From);
                incompatible.Add(edge.To);
            }
        }

        var labelRanks = layout.Nodes.Values
            .GroupBy(placement => placement.GalaxyIndex)
            .SelectMany(group => group
                .OrderBy(placement => placement.IsHub ? 0 : 1)
                .ThenByDescending(placement => placement.Degree)
                .ThenBy(placement => placement.Id, StringComparer.OrdinalIgnoreCase)
                .Select((placement, rank) => (placement.Id, Rank: rank)))
            .ToDictionary(pair => pair.Id, pair => pair.Rank, StringComparer.OrdinalIgnoreCase);

        var nodeVms = new List<GalaxyNodeVm>(layout.Nodes.Count);
        var byVm = new Dictionary<string, GalaxyNodeVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var placement in layout.Nodes.Values
                     .OrderBy(node => node.GalaxyIndex)
                     .ThenBy(node => node.Ring)
                     .ThenBy(node => node.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!byId.TryGetValue(placement.Id, out var model)) continue;
            var vm = new GalaxyNodeVm
            {
                Model = model,
                GalaxyIndex = placement.GalaxyIndex,
                Ring = placement.Ring,
                IsHub = placement.IsHub,
                Degree = placement.Degree,
                Radius = placement.IsHub ? options.HubRadius : options.NodeRadius,
                X = placement.X,
                Y = placement.Y,
                ShowLabel = placement.LabelVisible,
                LabelRect = placement.LabelRect,
                IsOptionalRelated = optional.Contains(model.Id) && !placement.IsHub,
                IsIncompatible = incompatible.Contains(model.Id),
                IsIsolatedGalaxy = layout.Galaxies[placement.GalaxyIndex].IsIsolated,
                LabelRank = labelRanks.GetValueOrDefault(model.Id, 99),
            };
            nodeVms.Add(vm);
            byVm[model.Id] = vm;
        }

        var edgeVms = new List<GalaxyEdgeVm>(edges.Count);
        foreach (var edge in edges)
        {
            if (!byVm.TryGetValue(edge.From, out var from) || !byVm.TryGetValue(edge.To, out var to))
                continue;
            edgeVms.Add(new GalaxyEdgeVm
            {
                Model = edge,
                From = from,
                To = to,
                Type = edge.Type,
                Manual = edge.ManualOverride,
                CrossGalaxy = from.GalaxyIndex != to.GalaxyIndex,
            });
        }

        var galaxies = layout.Galaxies
            .Select(galaxy => new GalaxyInfoVm
            {
                Index = galaxy.Index,
                HubId = galaxy.HubId,
                HubTitle = byVm.TryGetValue(galaxy.HubId, out var hub) ? hub.Title : galaxy.HubId,
                CenterX = galaxy.CenterX,
                CenterY = galaxy.CenterY,
                Radius = galaxy.Radius,
                NodeCount = galaxy.NodeIds.Count,
                CrossEdges = galaxy.CrossEdges,
                IsIsolated = galaxy.IsIsolated,
                Color = galaxy.IsIsolated
                    ? Color.Parse("#64748B")
                    : GalaxyColors[galaxy.Index % GalaxyColors.Length],
            })
            .ToList();

        return new GalaxyScene
        {
            Bounds = layout.Bounds,
            Nodes = nodeVms,
            Galaxies = galaxies,
            Edges = edgeVms,
            CrossLinks = layout.CrossLinks,
            ById = byVm,
        };
    }
}

public sealed class GalaxyNodeVm
{
    public required ModNode Model { get; init; }
    public required int GalaxyIndex { get; init; }
    public required int Ring { get; init; }
    public required bool IsHub { get; init; }
    public required int Degree { get; init; }
    public required double Radius { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public required bool ShowLabel { get; init; }
    public required LayoutRect LabelRect { get; set; }
    public bool IsOptionalRelated { get; init; }
    public bool IsIncompatible { get; init; }
    public bool IsIsolatedGalaxy { get; init; }

    /// <summary>0 = hub, then by degree inside the galaxy. Used for overview-level labels.</summary>
    public int LabelRank { get; init; } = 99;

    public string Id => Model.Id;
    public string Title => !string.IsNullOrWhiteSpace(Model.NameZh)
        ? Model.NameZh!
        : (!string.IsNullOrWhiteSpace(Model.Name) ? Model.Name : Model.Id);
    public string Subtitle => $"{Model.Id} · {Model.DisplayVersion}";
    public Color StatusColor => GraphPalette.StatusColor(Model.Status);
    public Color LoaderColor => GraphPalette.LoaderColor(Model.Loader);

    public Color FillColor => IsIsolatedGalaxy
        ? Blend(Color.Parse("#0E1118"), LoaderColor, 0.12)
        : IsHub
            ? Blend(Color.Parse("#17120A"), Color.Parse("#F5C542"), 0.18)
            : Blend(Color.Parse("#0E1118"), LoaderColor, Ring == 1 ? 0.26 : 0.16);

    public Color BorderColor => IsIsolatedGalaxy
        ? Color.Parse("#475569")
        : IsIncompatible
            ? Color.Parse("#F87171")
            : IsDisabled
                ? Color.Parse("#64748B")
                : IsHub
                    ? Color.Parse("#F5C542")
                    : IsOptionalRelated
                        ? Color.Parse("#34D399")
                        : Ring == 1
                            ? Color.Parse("#3B82F6")
                            : Color.Parse("#64748B");

    public bool IsDisabled => Model.Status == ModStatus.Missing;

    private static Color Blend(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(255,
            (byte)(from.R * (1 - t) + to.R * t),
            (byte)(from.G * (1 - t) + to.G * t),
            (byte)(from.B * (1 - t) + to.B * t));
    }
}

public sealed class GalaxyInfoVm
{
    public required int Index { get; init; }
    public required string HubId { get; init; }
    public required string HubTitle { get; init; }
    public required double CenterX { get; init; }
    public required double CenterY { get; init; }
    public required double Radius { get; init; }
    public required int NodeCount { get; init; }
    public required int CrossEdges { get; init; }
    public required Color Color { get; init; }
    public bool IsIsolated { get; init; }
    public string ShortTitle => IsIsolated ? "未连接" : $"G{Index + 1}";
    public string OverviewTitle => IsIsolated ? "未" : $"G{Index + 1}";
}

public sealed class GalaxyEdgeVm
{
    public required DependencyEdge Model { get; init; }
    public required GalaxyNodeVm From { get; init; }
    public required GalaxyNodeVm To { get; init; }
    public required EdgeType Type { get; init; }
    public required bool CrossGalaxy { get; init; }
    public required bool Manual { get; init; }
    public Color Color => GraphPalette.EdgeColor(Type, Manual);
}
