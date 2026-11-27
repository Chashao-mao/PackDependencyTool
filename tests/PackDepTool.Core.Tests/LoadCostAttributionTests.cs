using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class LoadCostAttributionTests
{
    [Fact]
    public void SplitsSharedPrerequisiteAcrossDependents()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "a", Name = "A" },
                new ModNode { Id = "b", Name = "B" },
                new ModNode { Id = "c", Name = "C" },
            ],
            Edges =
            [
                new DependencyEdge { From = "a", To = "c", Type = EdgeType.Requires },
                new DependencyEdge { From = "b", To = "c", Type = EdgeType.Requires },
            ],
        };
        var own = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = 10,
            ["b"] = 10,
            ["c"] = 100,
        };

        var report = LoadCostAttribution.Analyze(graph, own);
        var a = report.Costs.Single(row => row.ModId == "a");
        var c = report.Costs.Single(row => row.ModId == "c");

        Assert.Equal(60, a.EffectiveMilliseconds, 3);
        Assert.Equal(50, a.SharedPrerequisiteMilliseconds, 3);
        Assert.Equal(100, c.EffectiveMilliseconds, 3);
        Assert.Equal(2, c.Dependents);
    }

    [Fact]
    public void StopsAtDependencyCycles()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "a", Name = "A" },
                new ModNode { Id = "b", Name = "B" },
            ],
            Edges =
            [
                new DependencyEdge { From = "a", To = "b", Type = EdgeType.Requires },
                new DependencyEdge { From = "b", To = "a", Type = EdgeType.Requires },
            ],
        };
        var own = new Dictionary<string, double>
        {
            ["a"] = 10,
            ["b"] = 20,
        };

        var report = LoadCostAttribution.Analyze(graph, own);

        Assert.Equal(2, report.Costs.Count);
        Assert.All(report.Costs, row => Assert.True(row.EffectiveMilliseconds > 0));
    }
}
