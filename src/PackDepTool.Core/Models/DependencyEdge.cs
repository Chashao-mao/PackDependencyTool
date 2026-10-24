namespace PackDepTool.Core.Models;

/// <summary>A directed dependency edge. From depends on / conflicts with To.</summary>
public sealed class DependencyEdge
{
    public string Id { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public EdgeType Type { get; set; } = EdgeType.Requires;
    public string? VersionRange { get; set; }
    public ModSide Side { get; set; } = ModSide.Unknown;
    public EdgeSource Source { get; set; } = EdgeSource.Local;
    /// <summary>0..1. Local jar metadata is 1.0, online project-level data is lower.</summary>
    public double Confidence { get; set; } = 1.0;
    public string? Note { get; set; }
    public bool ManualOverride { get; set; }
    /// <summary>Set when the edge was materialised from a persisted manual link.</summary>
    public string? ManualLinkId { get; set; }

    public string PairKey => $"{From}\u0000{To}";
}
