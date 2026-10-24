using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public class DependencyRailBuilderTests
{
    private static DependencyEdge Edge(string from, string to, EdgeType type) => new()
    {
        Id = $"{from}->{to}:{type}",
        From = from,
        To = to,
        Type = type,
    };

    [Fact]
    public void GroupsAllDependentsUnderOneParent()
    {
        var groups = DependencyRailBuilder.Build(
            ["a", "b", "c", "d"],
            [
                Edge("b", "a", EdgeType.Requires),
                Edge("c", "a", EdgeType.Requires),
            ],
            RailRelationMode.Required);

        var group = Assert.Single(groups);
        Assert.Equal("a", group.ParentId);
        Assert.Equal(0, group.ParentIndex);
        Assert.Equal([1, 2], group.ChildIndices);
        Assert.Equal(0, group.MinIndex);
        Assert.Equal(2, group.MaxIndex);
    }

    [Fact]
    public void ConflictIsSeparateAndAlwaysIncluded()
    {
        var groups = DependencyRailBuilder.Build(
            ["a", "b", "c", "d"],
            [
                Edge("b", "a", EdgeType.Requires),
                Edge("d", "b", EdgeType.Incompatible),
            ],
            RailRelationMode.Required);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, group => group.ParentId == "a" && !group.IsConflict);
        var conflict = Assert.Single(groups, group => group.IsConflict);
        Assert.Equal("b", conflict.ParentId);
        Assert.Equal([3], conflict.ChildIndices);
    }

    [Fact]
    public void OptionalEdgesRespectMode()
    {
        var edges = new[]
        {
            Edge("b", "a", EdgeType.Requires),
            Edge("c", "d", EdgeType.Optional),
        };

        var required = DependencyRailBuilder.Build(["a", "b", "c", "d"], edges, RailRelationMode.Required);
        var optional = DependencyRailBuilder.Build(["a", "b", "c", "d"], edges, RailRelationMode.RequiredOptional);

        Assert.Single(required);
        Assert.Equal(2, optional.Count);
        Assert.Contains(optional, group => group.ParentId == "d" && group.ChildIndices.SequenceEqual([2]));
    }

    [Fact]
    public void EmbeddedParentIsFromSide()
    {
        var groups = DependencyRailBuilder.Build(
            ["parent", "child"],
            [Edge("parent", "child", EdgeType.Embedded)],
            RailRelationMode.AllRelations);

        var group = Assert.Single(groups);
        Assert.Equal("parent", group.ParentId);
        Assert.Equal([1], group.ChildIndices);
    }

    [Fact]
    public void MissingVisibleNodeIsIgnored()
    {
        var groups = DependencyRailBuilder.Build(
            ["a", "b"],
            [Edge("x", "a", EdgeType.Requires)],
            RailRelationMode.Required);

        Assert.Empty(groups);
    }

    [Fact]
    public void OverlappingIntervalsUseDifferentLanes()
    {
        var groups = DependencyRailBuilder.Build(
            ["a", "b", "c", "d"],
            [
                Edge("c", "a", EdgeType.Requires),
                Edge("d", "b", EdgeType.Requires),
            ],
            RailRelationMode.Required);

        Assert.Equal(2, groups.Count);
        Assert.NotEqual(groups[0].VisualLane, groups[1].VisualLane);
    }
}
