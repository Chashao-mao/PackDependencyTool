using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public sealed record ModLoadCost(
    string ModId,
    string Name,
    double OwnMilliseconds,
    double SharedPrerequisiteMilliseconds,
    double EffectiveMilliseconds,
    int Dependents);

public sealed class LoadCostReport
{
    public IReadOnlyList<ModLoadCost> Costs { get; init; } = [];
    public double TotalOwnMilliseconds { get; init; }
    public double SlowestMilliseconds { get; init; }
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// Attribute profiler load times across required dependency edges. A shared
/// prerequisite's cost is split by its installed dependents, so a library used
/// by ten mods does not appear ten times in each mod's cost.
/// </summary>
public static class LoadCostAttribution
{
    public static LoadCostReport Analyze(
        GraphDocument graph,
        IReadOnlyDictionary<string, double> ownMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var installed = graph.Nodes
            .Where(node => !node.IsSynthetic && !node.IsEmbedded)
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var dependents = graph.Edges
            .Where(edge => edge.Type == EdgeType.Requires &&
                           installed.ContainsKey(edge.From) && installed.ContainsKey(edge.To))
            .GroupBy(edge => edge.To, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(edge => edge.From).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var prerequisites = graph.Edges
            .Where(edge => edge.Type == EdgeType.Requires &&
                           installed.ContainsKey(edge.From) && installed.ContainsKey(edge.To))
            .GroupBy(edge => edge.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(edge => edge.To).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var cache = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double Effective(string id, HashSet<string> stack)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cache.TryGetValue(id, out var cached)) return cached;
            if (!stack.Add(id)) return 0; // cycle: stop instead of recursing forever
            var own = ownMilliseconds.GetValueOrDefault(id);
            var shared = 0.0;
            foreach (var prerequisite in prerequisites.GetValueOrDefault(id) ?? [])
            {
                var fanout = Math.Max(1, dependents.GetValueOrDefault(prerequisite)?.Count ?? 1);
                shared += Effective(prerequisite, stack) / fanout;
            }
            stack.Remove(id);
            var value = own + shared;
            cache[id] = value;
            return value;
        }

        var rows = new List<ModLoadCost>(installed.Count);
        foreach (var node in installed.Values)
        {
            var own = Math.Max(0, ownMilliseconds.GetValueOrDefault(node.Id));
            var effective = Effective(node.Id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var shared = effective - own;
            rows.Add(new ModLoadCost(node.Id, node.Name, own, shared, effective,
                dependents.GetValueOrDefault(node.Id)?.Count ?? 0));
        }

        var ordered = rows.OrderByDescending(row => row.EffectiveMilliseconds).ToList();
        var total = ordered.Sum(row => row.OwnMilliseconds);
        return new LoadCostReport
        {
            Costs = ordered,
            TotalOwnMilliseconds = total,
            SlowestMilliseconds = ordered.FirstOrDefault()?.EffectiveMilliseconds ?? 0,
            Summary = ordered.Count == 0
                ? "没有可归因的加载耗时数据。"
                : $"归因 {ordered.Count} 个模组；最慢 {ordered[0].Name} " +
                  $"({ordered[0].EffectiveMilliseconds:0.#} ms，含共享前置 {ordered[0].SharedPrerequisiteMilliseconds:0.#} ms)。",
        };
    }
}
