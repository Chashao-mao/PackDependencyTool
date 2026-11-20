using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class StarLayoutTests
{
    [Fact]
    public void CentersSelectedNodeAndLimitsDepth()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "center", Name = "Center" },
                new ModNode { Id = "a", Name = "A" },
                new ModNode { Id = "b", Name = "B" },
                new ModNode { Id = "far", Name = "Far" },
            ],
            Edges =
            [
                new DependencyEdge { From = "center", To = "a", Type = EdgeType.Requires },
                new DependencyEdge { From = "a", To = "b", Type = EdgeType.Requires },
                new DependencyEdge { From = "b", To = "far", Type = EdgeType.Requires },
            ],
        };

        var positions = new GraphLayoutEngine().Compute(graph, new LayoutOptions
        {
            Algorithm = LayoutAlgorithm.Star,
            CenterNodeId = "center",
            StarDepth = 2,
        });

        Assert.Contains("center", positions.Keys);
        Assert.Contains("a", positions.Keys);
        Assert.Contains("b", positions.Keys);
        Assert.DoesNotContain("far", positions.Keys);
    }
}
