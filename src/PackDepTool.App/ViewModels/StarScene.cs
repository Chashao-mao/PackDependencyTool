using Avalonia.Media;
using PackDepTool.Core.Models;

namespace PackDepTool.App.ViewModels;

/// <summary>
/// Dedicated star-graph model. Positions are deliberately not stored: the
/// StarGraphView lays the rings out against its current Bounds so text stays
/// readable instead of relying on a zoom-to-fit pass.
/// </summary>
public sealed class StarScene
{
    public required StarNodeVm Center { get; init; }
    public IReadOnlyList<StarNodeVm> Nodes { get; init; } = [];
    public IReadOnlyList<StarEdgeVm> Edges { get; init; } = [];
    public int HiddenCount { get; init; }
    public bool IsEmpty => Nodes.Count == 0;
}

public sealed class StarNodeVm
{
    public required ModNode Model { get; init; }
    public int Ring { get; init; }
    public string Title => !string.IsNullOrWhiteSpace(Model.NameZh) ? Model.NameZh! :
        (string.IsNullOrWhiteSpace(Model.Name) ? Model.Id : Model.Name);
    public string Subtitle => $"{Model.Id} · {Model.DisplayVersion}";
    public Color StatusColor => GraphPalette.StatusColor(Model.Status);
    public Color LoaderColor => GraphPalette.LoaderColor(Model.Loader);
    public string? IconDataUrl => Model.IconData is { Length: > 0 }
        ? "data:image/png;base64," + Convert.ToBase64String(Model.IconData)
        : null;
}

public sealed class StarEdgeVm
{
    public required StarNodeVm From { get; init; }
    public required StarNodeVm To { get; init; }
    public required EdgeType Type { get; init; }
    public bool Manual { get; init; }
    public Color Color => Manual ? GraphPalette.EdgeManual : GraphPalette.EdgeColor(Type, Manual);
}
