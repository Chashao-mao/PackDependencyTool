using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

/// <summary>
/// Detects missing dependencies, conflicts, version / Minecraft version mismatches and
/// require-cycles. Only reports a mismatch when the range is understood; unknown ranges
/// are never turned into fabricated findings.
/// </summary>
public sealed class GraphAnalyzer
{
    private const int MaxReportedCycles = 50;

    public List<AnalysisIssue> Analyze(
        GraphDocument graph,
        PackProfile profile,
        LocalRuleSet? rules = null)
    {
        var issues = new List<AnalysisIssue>();
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        AnalyzeMissingAndVersions(graph, nodes, issues, rules);
        AnalyzeConflicts(graph, nodes, issues, rules);
        AnalyzeMinecraftVersions(graph, profile, issues, rules);
        AnalyzeCycles(graph, nodes, issues);

        return issues;
    }

    private static void AnalyzeMissingAndVersions(
        GraphDocument graph,
        IReadOnlyDictionary<string, ModNode> nodes,
        List<AnalysisIssue> issues,
        LocalRuleSet? rules)
    {
        foreach (var edge in graph.Edges)
        {
            if (edge.Type is not (EdgeType.Requires or EdgeType.Optional or EdgeType.Recommends or EdgeType.Embedded))
                continue;

            if (!nodes.TryGetValue(edge.From, out var from) || !nodes.TryGetValue(edge.To, out var to))
                continue;

            if (to.IsSynthetic)
            {
                if (edge.Type == EdgeType.Requires)
                {
                    if (rules?.IsMissingIgnored(from.Id, to.Id) == true)
                    {
                        to.Status = ModStatus.Unknown;
                        to.StatusReason = "缺失（已按本地规则忽略）";
                        continue;
                    }

                    to.Status = ModStatus.Missing;
                    to.StatusReason = "未安装";
                    issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Error,
                        Kind = IssueKind.MissingDependency,
                        Title = "缺失必需依赖",
                        Message = $"{from.Name}（{from.Id}）需要 {to.Id}" +
                                  (string.IsNullOrWhiteSpace(edge.VersionRange) ? "，但未安装。" : $" {edge.VersionRange}，但未安装。"),
                        NodeId = to.Id,
                        RelatedNodeId = from.Id,
                        EdgeId = edge.Id,
                    });
                }
                else if (edge.Type is EdgeType.Optional or EdgeType.Recommends)
                {
                    issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Info,
                        Kind = IssueKind.OptionalMissing,
                        Title = "可选依赖未安装",
                        Message = $"{from.Name}（{from.Id}）推荐安装 {to.Id}，当前未安装。",
                        NodeId = to.Id,
                        RelatedNodeId = from.Id,
                        EdgeId = edge.Id,
                    });
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(edge.VersionRange) || to.Version == "unknown")
                continue;

            if (rules?.IsVersionIgnored(from.Id, to.Id) == true)
                continue;

            var satisfied = VersionRange.Satisfies(edge.VersionRange, to.Version);
            if (satisfied != false) continue;

            var severity = edge.Type == EdgeType.Requires ? IssueSeverity.Warning : IssueSeverity.Info;
            issues.Add(new AnalysisIssue
            {
                Severity = severity,
                Kind = IssueKind.VersionMismatch,
                Title = "依赖版本不匹配",
                Message = $"{from.Name}（{from.Id}）需要 {to.Name} {edge.VersionRange}，已安装 {to.Version}。",
                NodeId = to.Id,
                RelatedNodeId = from.Id,
                EdgeId = edge.Id,
            });

            if (to.Status == ModStatus.Ok)
            {
                to.Status = ModStatus.VersionMismatch;
                to.StatusReason = $"需要 {edge.VersionRange}，已安装 {to.Version}";
            }
        }
    }

    private static void AnalyzeConflicts(
        GraphDocument graph,
        IReadOnlyDictionary<string, ModNode> nodes,
        List<AnalysisIssue> issues,
        LocalRuleSet? rules)
    {
        foreach (var edge in graph.Edges.Where(e => e.Type == EdgeType.Incompatible))
        {
            if (!nodes.TryGetValue(edge.From, out var from) || !nodes.TryGetValue(edge.To, out var to))
                continue;
            if (from.IsSynthetic || to.IsSynthetic) continue;
            if (rules?.IsConflictIgnored(from.Id, to.Id) == true) continue;

            // `breaks` is a declaration, often scoped to a file version. A declaration
            // for a different installed version is not a conflict for this pack.
            var declaredForInstalledVersion = VersionRange.Satisfies(edge.VersionRange, to.Version);
            if (declaredForInstalledVersion == false) continue;

            if (declaredForInstalledVersion == true)
            {
                if (from.Status == ModStatus.Ok)
                {
                    from.Status = ModStatus.Conflict;
                    from.StatusReason = $"与 {to.Name} 声明不兼容";
                }
                if (to.Status == ModStatus.Ok)
                {
                    to.Status = ModStatus.Conflict;
                    to.StatusReason = $"与 {from.Name} 声明不兼容";
                }
            }

            issues.Add(new AnalysisIssue
            {
                Severity = IssueSeverity.Warning,
                Kind = IssueKind.Incompatible,
                Title = "声明不兼容",
                Message = declaredForInstalledVersion == true
                    ? $"{from.Name}（{from.Id}）声明与 {to.Name}（{to.Id}）不兼容；这是模组元数据声明，不一定是运行时实际冲突。"
                    : $"{from.Name}（{from.Id}）声明与 {to.Name}（{to.Id}）的某个版本不兼容，但当前版本无法确认匹配范围。",
                NodeId = from.Id,
                RelatedNodeId = to.Id,
                EdgeId = edge.Id,
            });
        }
    }

    private static void AnalyzeMinecraftVersions(
        GraphDocument graph,
        PackProfile profile,
        List<AnalysisIssue> issues,
        LocalRuleSet? rules)
    {
        if (string.IsNullOrWhiteSpace(profile.McVersion) || profile.McVersion == "unknown")
            return;

        foreach (var node in graph.Nodes)
        {
            if (node.IsSynthetic || node.Loader == LoaderKind.Unknown) continue;
            if (rules?.IsVersionIgnored(node.Id, "minecraft") == true) continue;

            if (node.McVersions.Count > 0)
            {
                var matches = node.McVersions.Any(mc => MatchesPack(profile.McVersion, mc));
                if (!matches)
                {
                    issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Kind = IssueKind.McVersionMismatch,
                        Title = "Minecraft 版本不匹配",
                        Message = $"{node.Name}（{node.Id}）标注支持 {string.Join(", ", node.McVersions)}，" +
                                  $"当前整合包为 {profile.McVersion}。",
                        NodeId = node.Id,
                    });

                    Escalate(node, $"标注支持 {string.Join(", ", node.McVersions)}，整合包为 {profile.McVersion}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(node.McVersionRange))
            {
                var satisfied = VersionRange.SatisfiesMinecraft(node.McVersionRange, profile.McVersion);
                if (satisfied == false)
                {
                    issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Kind = IssueKind.McVersionMismatch,
                        Title = "Minecraft 版本不匹配",
                        Message = $"{node.Name}（{node.Id}）声明 Minecraft {node.McVersionRange}，" +
                                  $"当前整合包为 {profile.McVersion}。",
                        NodeId = node.Id,
                    });

                    Escalate(node, $"需要 Minecraft {node.McVersionRange}，整合包为 {profile.McVersion}");
                }
            }
        }
    }

    private static void AnalyzeCycles(
        GraphDocument graph,
        IReadOnlyDictionary<string, ModNode> nodes,
        List<AnalysisIssue> issues)
    {
        var adjacency = graph.Edges
            .Where(e => e.Type == EdgeType.Requires)
            .GroupBy(e => e.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.To).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        var pathIndexes = new List<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reported = 0;

        foreach (var start in graph.Nodes.Select(n => n.Id).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (reported >= MaxReportedCycles) break;
            if (state.GetValueOrDefault(start) != 0 || !adjacency.ContainsKey(start)) continue;

            state[start] = 1;
            path.Add(start);
            pathIndexes.Add(0);

            while (path.Count > 0)
            {
                var current = path[^1];
                var index = pathIndexes[^1];
                var neighbours = adjacency.GetValueOrDefault(current);

                if (neighbours is null || index >= neighbours.Count)
                {
                    state[current] = 2;
                    path.RemoveAt(path.Count - 1);
                    pathIndexes.RemoveAt(pathIndexes.Count - 1);
                    continue;
                }

                pathIndexes[^1] = index + 1;
                var next = neighbours[index];
                if (!nodes.ContainsKey(next)) continue;

                var nextState = state.GetValueOrDefault(next);
                if (nextState == 1)
                {
                    var cycleStart = path.FindIndex(id => string.Equals(id, next, StringComparison.OrdinalIgnoreCase));
                    if (cycleStart < 0) continue;

                    var cycle = path.Skip(cycleStart).ToList();
                    var canonical = Canonicalize(cycle);
                    if (!seen.Add(canonical)) continue;

                    var names = cycle.Select(id => nodes.TryGetValue(id, out var n) ? n.Name : id);
                    issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Kind = IssueKind.Cycle,
                        Title = "检测到循环依赖",
                        Message = string.Join(" → ", names) + " → " + (nodes.TryGetValue(cycle[0], out var first) ? first.Name : cycle[0]),
                        NodeId = cycle[0],
                    });

                    reported++;
                    if (reported >= MaxReportedCycles) break;
                }
                else if (nextState == 0)
                {
                    state[next] = 1;
                    path.Add(next);
                    pathIndexes.Add(0);
                }
            }
        }
    }

    private static string Canonicalize(List<string> cycle)
    {
        if (cycle.Count == 0) return string.Empty;
        var min = cycle.Min(StringComparer.OrdinalIgnoreCase)!;
        var index = cycle.FindIndex(id => string.Equals(id, min, StringComparison.OrdinalIgnoreCase));
        var rotated = cycle.Skip(index).Concat(cycle.Take(index));
        return string.Join("\u0000", rotated);
    }

    private static bool MatchesPack(string packVersion, string declared)
    {
        if (string.Equals(packVersion, declared, StringComparison.OrdinalIgnoreCase)) return true;
        return packVersion.StartsWith(declared + ".", StringComparison.OrdinalIgnoreCase) ||
               declared.StartsWith(packVersion + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static void Escalate(ModNode node, string reason)
    {
        if (node.Status == ModStatus.Ok)
        {
            node.Status = ModStatus.VersionMismatch;
            node.StatusReason = reason;
        }
    }
}
