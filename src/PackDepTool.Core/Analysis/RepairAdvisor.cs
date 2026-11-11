using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public enum RepairLevel
{
    Auto,
    Confirm,
    Suggest,
}

public sealed record RepairAssessment(RepairLevel Level, string Reason)
{
    public string Label => Level switch
    {
        RepairLevel.Auto => "自动",
        RepairLevel.Confirm => "确认",
        _ => "建议",
    };
}

/// <summary>
/// Classifies repairs by safety before the UI asks for confirmation.
/// Auto: isolated and reversible; Confirm: cascades or permanent deletion;
/// Suggest: would leave an installed prerequisite orphaned.
/// </summary>
public static class RepairAdvisor
{
    public static RepairAssessment AssessDisable(GraphDocument graph, ModNode node)
    {
        var dependents = CountInstalledDependents(graph, node.Id);
        if (dependents > 0)
            return new RepairAssessment(RepairLevel.Confirm,
                $"有 {dependents} 个已安装模组依赖它，需要级联处理");

        var orphans = CountOrphanPrerequisites(graph, node.Id);
        if (orphans > 0)
            return new RepairAssessment(RepairLevel.Suggest,
                $"禁用后会产生 {orphans} 个孤立前置，建议一并检查");

        return new RepairAssessment(RepairLevel.Auto,
            "单模组、无级联依赖，可通过快照撤销");
    }

    public static RepairAssessment AssessDelete(GraphDocument graph, ModNode node)
    {
        var dependents = CountInstalledDependents(graph, node.Id);
        return new RepairAssessment(RepairLevel.Confirm,
            dependents > 0
                ? $"永久删除不可撤销，且有 {dependents} 个模组依赖它"
                : "永久删除不可撤销，执行前会创建快照");
    }

    public static RepairAssessment AssessBatch(GraphDocument graph, IReadOnlyList<ModNode> nodes)
    {
        var dependents = nodes.Sum(node => CountInstalledDependents(graph, node.Id));
        var orphans = nodes.Sum(node => CountOrphanPrerequisites(graph, node.Id));
        if (dependents > 0 || nodes.Count > 1)
            return new RepairAssessment(RepairLevel.Confirm,
                $"批量操作涉及 {nodes.Count} 个模组" +
                (dependents > 0 ? $"，并级联 {dependents} 个依赖" : string.Empty));
        if (orphans > 0)
            return new RepairAssessment(RepairLevel.Suggest,
                $"会产生 {orphans} 个孤立前置，建议检查");
        return new RepairAssessment(RepairLevel.Auto, "单模组、无级联依赖");
    }

    private static int CountInstalledDependents(GraphDocument graph, string id) =>
        graph.Edges
            .Where(edge => edge.Type == EdgeType.Requires &&
                           string.Equals(edge.To, id, StringComparison.OrdinalIgnoreCase))
            .Select(edge => graph.FindNode(edge.From))
            .Where(node => node is not null && !node.IsSynthetic && !node.IsEmbedded)
            .Select(node => node!.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static int CountOrphanPrerequisites(GraphDocument graph, string id)
    {
        var prerequisites = graph.Edges
            .Where(edge => edge.Type == EdgeType.Requires &&
                           string.Equals(edge.From, id, StringComparison.OrdinalIgnoreCase))
            .Select(edge => graph.FindNode(edge.To))
            .Where(node => node is not null && !node.IsSynthetic && !node.IsEmbedded)
            .ToList();

        return prerequisites.Count(prerequisite => !graph.Edges.Any(edge =>
            edge.Type == EdgeType.Requires &&
            string.Equals(edge.To, prerequisite!.Id, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(edge.From, id, StringComparison.OrdinalIgnoreCase) &&
            graph.FindNode(edge.From) is { } dependent &&
            !dependent.IsSynthetic && !dependent.IsEmbedded));
    }
}
