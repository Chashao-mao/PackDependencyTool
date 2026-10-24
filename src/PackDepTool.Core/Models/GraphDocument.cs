using System.Text.Json.Serialization;

namespace PackDepTool.Core.Models;

/// <summary>Stable graph document: { nodes, edges, meta }.</summary>
public sealed class GraphDocument
{
    public List<ModNode> Nodes { get; set; } = [];
    public List<DependencyEdge> Edges { get; set; } = [];
    public GraphMeta Meta { get; set; } = new();

    public ModNode? FindNode(string? id) =>
        string.IsNullOrEmpty(id)
            ? null
            : Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));

    public int CountByStatus(ModStatus status) => Nodes.Count(n => n.Status == status);
}

public sealed class GraphMeta
{
    public string McVersion { get; set; } = "unknown";
    public string Loader { get; set; } = "unknown";
    public string GeneratedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonIgnore] public string? PackName { get; set; }
    [JsonIgnore] public string? RootPath { get; set; }
    [JsonIgnore] public string? LoaderVersion { get; set; }
}
