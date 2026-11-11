using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class RepairAdvisorTests
{
    private static GraphDocument Graph(params (string From, string To)[] requires)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in requires)
        {
            ids.Add(from);
            ids.Add(to);
        }
        ids.UnionWith(["isolated", "dependent", "prerequisite", "orphan"]);
        return new GraphDocument
        {
            Nodes = ids.Select(id => new ModNode { Id = id, Name = id }).ToList(),
            Edges = requires.Select((pair, index) => new DependencyEdge
            {
                Id = $"e{index}",
                From = pair.From,
                To = pair.To,
                Type = EdgeType.Requires,
            }).ToList(),
        };
    }

    [Fact]
    public void IsolatedDisableIsAutomatic()
    {
        var graph = Graph();
        var assessment = RepairAdvisor.AssessDisable(graph, graph.FindNode("isolated")!);
        Assert.Equal(RepairLevel.Auto, assessment.Level);
    }

    [Fact]
    public void DisableWithDependentRequiresConfirmation()
    {
        var graph = Graph(("dependent", "prerequisite"));
        var assessment = RepairAdvisor.AssessDisable(graph, graph.FindNode("prerequisite")!);
        Assert.Equal(RepairLevel.Confirm, assessment.Level);
        Assert.Contains("依赖", assessment.Reason);
    }

    [Fact]
    public void DisableThatOrphansAPrerequisiteIsSuggested()
    {
        var graph = Graph(("dependent", "orphan"));
        var assessment = RepairAdvisor.AssessDisable(graph, graph.FindNode("dependent")!);
        Assert.Equal(RepairLevel.Suggest, assessment.Level);
    }

    [Fact]
    public void PermanentDeleteAlwaysRequiresConfirmation()
    {
        var graph = Graph();
        var assessment = RepairAdvisor.AssessDelete(graph, graph.FindNode("isolated")!);
        Assert.Equal(RepairLevel.Confirm, assessment.Level);
    }

    [Fact]
    public void BatchOfMultipleModsRequiresConfirmation()
    {
        var graph = Graph();
        var assessment = RepairAdvisor.AssessBatch(graph,
            [graph.FindNode("isolated")!, graph.FindNode("dependent")!]);
        Assert.Equal(RepairLevel.Confirm, assessment.Level);
    }
}
