using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class BisectionPlannerTests
{
    private static GraphDocument Graph() => new()
    {
        Nodes =
        [
            new ModNode { Id = "a", Name = "A", FilePath = "mods/a.jar" },
            new ModNode { Id = "b", Name = "B", FilePath = "mods/b.jar" },
            new ModNode { Id = "dep", Name = "Dep", FilePath = "mods/dep.jar" },
        ],
        Edges =
        [
            new DependencyEdge { Id = "e", From = "a", To = "dep", Type = EdgeType.Requires },
        ],
    };

    [Fact]
    public void IncludesRequiredDependenciesInTestGroup()
    {
        var planner = new BisectionPlanner(Graph());
        Assert.Contains(planner.Current.RequiredDependencies, node => node.Id == "dep");
        Assert.DoesNotContain(planner.Current.Candidates, node => node.Id == "dep");
    }

    [Fact]
    public void CrashNarrowsToTestHalfAndWorksNarrowsToOtherHalf()
    {
        var crashed = new BisectionPlanner(Graph());
        crashed.ReportCrashed();
        Assert.Single(crashed.Current.Candidates);

        var works = new BisectionPlanner(Graph());
        works.ReportWorks();
        Assert.Single(works.Current.Candidates);

        Assert.NotEqual(crashed.Current.Candidates[0].Id, works.Current.Candidates[0].Id);
    }
}
