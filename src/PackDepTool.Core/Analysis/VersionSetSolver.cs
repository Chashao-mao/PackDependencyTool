using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public sealed record SolverIssue(
    string RuleId,
    DiagnosticSeverity Severity,
    string Message,
    string Explanation,
    IReadOnlyList<string> RelatedModIds);

public sealed class VersionSetSolverReport
{
    public bool IsSatisfiable { get; init; }
    public List<SolverIssue> Issues { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// Phase 1 of the self-made solver: validate the currently installed set against
/// declared required version ranges, incompatible edges and required cycles.
/// It never invents a version that is not installed; candidate selection comes in
/// a later phase.
/// </summary>
public static class VersionSetSolver
{
    public static VersionSetSolverReport Solve(GraphDocument graph, CancellationToken cancellationToken = default)
    {
        var installed = graph.Nodes
            .Where(node => !node.IsSynthetic)
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var issues = new List<SolverIssue>();

        foreach (var edge in graph.Edges.Where(edge => edge.Type == EdgeType.Requires))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!installed.TryGetValue(edge.From, out var from)) continue;
            if (!installed.TryGetValue(edge.To, out var target))
            {
                issues.Add(new SolverIssue(
                    "missing-required", DiagnosticSeverity.Error,
                    $"{from.Name} 缺少必需前置 {edge.To}",
                    $"{from.Name} {from.DisplayVersion} 声明依赖 {edge.To} {edge.VersionRange}，当前实例没有安装。",
                    [from.Id, edge.To]));
                continue;
            }

            var satisfies = VersionRange.Satisfies(edge.VersionRange, target.Version);
            if (satisfies == false)
            {
                issues.Add(new SolverIssue(
                    "required-range", DiagnosticSeverity.Error,
                    $"{from.Name} 要求 {target.Name} {edge.VersionRange}",
                    $"当前安装 {target.Name} {target.DisplayVersion}，不满足 {from.Name} 声明的范围 {edge.VersionRange}。",
                    [from.Id, target.Id]));
            }
        }

        foreach (var edge in graph.Edges.Where(edge => edge.Type == EdgeType.Incompatible))
        {
            if (!installed.ContainsKey(edge.From) || !installed.ContainsKey(edge.To)) continue;
            var target = installed[edge.To];
            if (VersionRange.Satisfies(edge.VersionRange, target.Version) != true) continue;
            issues.Add(new SolverIssue(
                "incompatible", DiagnosticSeverity.Error,
                $"{installed[edge.From].Name} 与 {target.Name} 冲突",
                $"冲突声明范围 {edge.VersionRange} 命中当前 {target.Name} {target.DisplayVersion}。",
                [edge.From, edge.To]));
        }

        var cycle = FindRequiredCycle(graph, installed, cancellationToken);
        if (cycle.Count > 0)
        {
            issues.Add(new SolverIssue(
                "required-cycle", DiagnosticSeverity.Warning,
                "必需依赖存在循环",
                string.Join(" → ", cycle) + " → " + cycle[0],
                cycle));
        }

        var errors = issues.Count(issue => issue.Severity == DiagnosticSeverity.Error);
        return new VersionSetSolverReport
        {
            IsSatisfiable = errors == 0,
            Issues = issues,
            Summary = errors == 0
                ? $"当前组合可满足：{installed.Count} 个已安装模组，{issues.Count} 条提示。"
                : $"当前组合不可满足：{errors} 条错误，{issues.Count - errors} 条警告。",
        };
    }

    private static List<string> FindRequiredCycle(
        GraphDocument graph,
        IReadOnlyDictionary<string, ModNode> installed,
        CancellationToken cancellationToken)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        var adjacency = graph.Edges
            .Where(edge => edge.Type == EdgeType.Requires &&
                           installed.ContainsKey(edge.From) && installed.ContainsKey(edge.To))
            .GroupBy(edge => edge.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(edge => edge.To).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        List<string>? Visit(string id)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visiting.Contains(id))
            {
                var start = path.FindIndex(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
                return start < 0 ? [id] : [.. path[start..]];
            }
            if (!visited.Add(id)) return null;
            visiting.Add(id);
            path.Add(id);
            if (adjacency.TryGetValue(id, out var children))
            {
                foreach (var child in children)
                {
                    var found = Visit(child);
                    if (found is not null) return found;
                }
            }
            path.RemoveAt(path.Count - 1);
            visiting.Remove(id);
            return null;
        }

        foreach (var id in installed.Keys)
        {
            var found = Visit(id);
            if (found is not null) return found;
        }
        return [];
    }
}
