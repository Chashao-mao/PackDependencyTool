using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class VersionSetSolverTests
{
    [Fact]
    public void ReportsRangeMismatchAndMissingDependency()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "a", Name = "A", Version = "1.0" },
                new ModNode { Id = "b", Name = "B", Version = "1.0" },
            ],
            Edges =
            [
                new DependencyEdge { From = "a", To = "b", Type = EdgeType.Requires, VersionRange = "[2.0,3.0)" },
                new DependencyEdge { From = "a", To = "missing", Type = EdgeType.Requires, VersionRange = "1.0" },
            ],
        };

        var result = VersionSetSolver.Solve(graph);

        Assert.False(result.IsSatisfiable);
        Assert.Contains(result.Issues, issue => issue.RuleId == "required-range");
        Assert.Contains(result.Issues, issue => issue.RuleId == "missing-required");
    }

    [Fact]
    public void AcceptsSatisfiedSet()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "a", Name = "A", Version = "1.0" },
                new ModNode { Id = "b", Name = "B", Version = "2.5" },
            ],
            Edges =
            [
                new DependencyEdge { From = "a", To = "b", Type = EdgeType.Requires, VersionRange = "[2.0,3.0)" },
            ],
        };

        var result = VersionSetSolver.Solve(graph);

        Assert.True(result.IsSatisfiable);
        Assert.Empty(result.Issues);
    }
}
