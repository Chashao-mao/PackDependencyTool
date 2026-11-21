using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class StarGalaxyLayoutTests
{
    // 6 independent hub clusters: 1 hub + 40 leaves + 4 grandchildren = 45 nodes each.
    private static (List<ModNode> Nodes, List<DependencyEdge> Edges) BuildPack(int clusterCount = 6,
        int leavesPerCluster = 40, int grandchildrenPerCluster = 4)
    {
        var nodes = new List<ModNode>();
        var edges = new List<DependencyEdge>();
        for (var cluster = 0; cluster < clusterCount; cluster++)
        {
            var hub = new ModNode { Id = $"hub{cluster}", Name = $"Hub {cluster}", Status = ModStatus.Ok };
            nodes.Add(hub);
            var leaves = new List<ModNode>();
            for (var leaf = 0; leaf < leavesPerCluster; leaf++)
            {
                var node = new ModNode
                {
                    Id = $"c{cluster}-leaf{leaf}",
                    Name = $"Leaf {cluster}-{leaf}",
                    NameZh = $"叶子 {cluster}-{leaf}",
                    Status = ModStatus.Ok,
                };
                nodes.Add(node);
                leaves.Add(node);
                edges.Add(new DependencyEdge { From = node.Id, To = hub.Id, Type = EdgeType.Requires });
            }
            for (var child = 0; child < grandchildrenPerCluster; child++)
            {
                var node = new ModNode
                {
                    Id = $"c{cluster}-deep{child}",
                    Name = $"Deep {cluster}-{child}",
                    Status = ModStatus.Ok,
                };
                nodes.Add(node);
                edges.Add(new DependencyEdge
                {
                    From = node.Id,
                    To = leaves[child].Id,
                    Type = EdgeType.Requires,
                });
            }
        }

        // Realistic cross-galaxy requires edges.
        for (var i = 0; i < clusterCount; i++)
        {
            var other = (i + 1) % clusterCount;
            edges.Add(new DependencyEdge
            {
                From = $"hub{i}",
                To = $"c{other}-leaf0",
                Type = EdgeType.Requires,
            });
            edges.Add(new DependencyEdge
            {
                From = $"c{i}-leaf1",
                To = $"c{other}-leaf2",
                Type = EdgeType.Optional,
            });
        }

        return (nodes, edges);
    }

    [Fact]
    public void AllNodesAppearExactlyOnce()
    {
        var (nodes, edges) = BuildPack();
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);

        Assert.Equal(nodes.Count, layout.Nodes.Count);
        Assert.All(nodes, node => Assert.True(layout.Nodes.ContainsKey(node.Id)));

        var assigned = layout.Galaxies.SelectMany(galaxy => galaxy.NodeIds).ToList();
        Assert.Equal(nodes.Count, assigned.Count);
        Assert.Equal(nodes.Count, assigned.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ProducesAtLeastSixBalancedGalaxies()
    {
        var (nodes, edges) = BuildPack();
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);

        Assert.True(layout.Galaxies.Count >= 6, $"expected >= 6 galaxies, got {layout.Galaxies.Count}");
        Assert.True(layout.Galaxies.Count <= 10,
            $"over-fragmented into {layout.Galaxies.Count} galaxies");
        Assert.All(layout.Galaxies, galaxy => Assert.InRange(galaxy.NodeIds.Count, 1, 55));
    }

    [Fact]
    public void GalaxyHubsAreHighDegreeNodes()
    {
        var (nodes, edges) = BuildPack();
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);
        var degree = nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges)
        {
            if (!degree.ContainsKey(edge.From) || !degree.ContainsKey(edge.To)) continue;
            degree[edge.From]++;
            degree[edge.To]++;
        }

        foreach (var galaxy in layout.Galaxies)
        {
            var average = galaxy.NodeIds.Average(id => degree[id]);
            Assert.True(degree[galaxy.HubId] >= average,
                $"hub {galaxy.HubId} degree {degree[galaxy.HubId]} < average {average:F1}");
        }
    }

    [Fact]
    public void LayoutIsDeterministic()
    {
        var (nodes, edges) = BuildPack();
        var first = new StarGalaxyLayoutEngine().Compute(nodes, edges);
        var second = new StarGalaxyLayoutEngine().Compute(nodes, edges);

        Assert.Equal(first.Galaxies.Count, second.Galaxies.Count);
        foreach (var (id, placement) in first.Nodes)
        {
            var other = second.Nodes[id];
            Assert.Equal(placement.X, other.X);
            Assert.Equal(placement.Y, other.Y);
            Assert.Equal(placement.GalaxyIndex, other.GalaxyIndex);
            Assert.Equal(placement.Ring, other.Ring);
        }
    }

    [Fact]
    public void FocusNodeBecomesHubOfFirstGalaxy()
    {
        var (nodes, edges) = BuildPack();
        var focus = nodes.First(node => node.Id == "c3-leaf7");
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges, new StarGalaxyOptions
        {
            FocusNodeId = focus.Id,
        });

        Assert.Equal(focus.Id, layout.Galaxies[0].HubId);
        Assert.Contains(focus.Id, layout.Galaxies[0].NodeIds);
        Assert.True(layout.Galaxies[0].NodeIds.Count <= 55);
        Assert.Equal(nodes.Count, layout.Nodes.Count);
    }

    [Fact]
    public void IsolatedNodesAreNotDropped()
    {
        var nodes = Enumerable.Range(0, 40)
            .Select(index => new ModNode { Id = $"linked{index}", Name = $"Linked {index}" })
            .ToList();
        var edges = new List<DependencyEdge>();
        for (var i = 1; i < nodes.Count; i++)
            edges.Add(new DependencyEdge { From = nodes[i].Id, To = nodes[0].Id, Type = EdgeType.Requires });
        for (var i = 0; i < 10; i++)
            nodes.Add(new ModNode { Id = $"island{i}", Name = $"Island {i}" });

        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);

        Assert.Equal(nodes.Count, layout.Nodes.Count);
        Assert.Equal(nodes.Count, layout.Galaxies.SelectMany(galaxy => galaxy.NodeIds).Distinct().Count());
    }

    [Fact]
    public void CrossGalaxyLinksAreAggregatedAndComplete()
    {
        var (nodes, edges) = BuildPack();
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);
        var galaxyOf = layout.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value.GalaxyIndex,
            StringComparer.OrdinalIgnoreCase);
        var expectedCross = edges.Count(edge =>
            edge.Type != EdgeType.Incompatible &&
            galaxyOf.TryGetValue(edge.From, out var from) &&
            galaxyOf.TryGetValue(edge.To, out var to) && from != to);

        var actual = layout.CrossLinks.Sum(link => link.Count);
        Assert.Equal(expectedCross, actual);
        Assert.Equal(layout.CrossLinks.Count,
            layout.CrossLinks.Select(link =>
                link.FromGalaxy < link.ToGalaxy
                    ? (link.FromGalaxy, link.ToGalaxy)
                    : (link.ToGalaxy, link.FromGalaxy)).Distinct().Count());
        Assert.All(layout.CrossLinks, link =>
        {
            Assert.InRange(link.FromGalaxy, 0, layout.Galaxies.Count - 1);
            Assert.InRange(link.ToGalaxy, 0, layout.Galaxies.Count - 1);
            Assert.NotEqual(link.FromGalaxy, link.ToGalaxy);
        });
    }

    [Fact]
    public void GalaxyCentersHaveClearSpacing()
    {
        var (nodes, edges) = BuildPack();
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);
        for (var i = 0; i < layout.Galaxies.Count; i++)
        {
            for (var j = i + 1; j < layout.Galaxies.Count; j++)
            {
                var a = layout.Galaxies[i];
                var b = layout.Galaxies[j];
                var distance = Math.Sqrt(Math.Pow(a.CenterX - b.CenterX, 2) + Math.Pow(a.CenterY - b.CenterY, 2));
                Assert.True(distance > a.Radius + b.Radius,
                    $"galaxies {i}/{j} overlap: d={distance:F1} r={a.Radius:F1}+{b.Radius:F1}");
            }
        }
    }

    [Fact]
    public void NodesInsideAGalaxyKeepMinimumSpacing()
    {
        var (nodes, edges) = BuildPack();
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);
        foreach (var galaxy in layout.Galaxies)
        {
            for (var i = 0; i < galaxy.NodeIds.Count; i++)
            {
                for (var j = i + 1; j < galaxy.NodeIds.Count; j++)
                {
                    var a = layout.Nodes[galaxy.NodeIds[i]];
                    var b = layout.Nodes[galaxy.NodeIds[j]];
                    var distance = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    Assert.True(distance >= 60, $"nodes {a.Id}/{b.Id} too close: {distance:F1}");
                }
            }
        }
    }

    [Fact]
    public void VisibleLabelsDoNotOverlapNodesOrEachOther()
    {
        var (nodes, edges) = BuildPack();
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);
        var visible = layout.Nodes.Values.Where(node => node.LabelVisible).ToList();
        Assert.NotEmpty(visible);

        for (var i = 0; i < visible.Count; i++)
        {
            for (var j = i + 1; j < visible.Count; j++)
                Assert.False(visible[i].LabelRect.IntersectsWith(visible[j].LabelRect),
                    $"labels overlap: {visible[i].Id}/{visible[j].Id}");
        }

        foreach (var label in visible)
        {
            foreach (var node in layout.Nodes.Values)
            {
                if (string.Equals(label.Id, node.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var nodeRect = LayoutRect.FromCenter(node.X, node.Y, 48, 48);
                Assert.False(label.LabelRect.IntersectsWith(nodeRect),
                    $"label {label.Id} covers node {node.Id}");
            }
        }
    }

    [Fact]
    public void BoundsContainEveryNode()
    {
        var (nodes, edges) = BuildPack();
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);
        Assert.True(layout.Bounds.Width > 0);
        Assert.True(layout.Bounds.Height > 0);
        Assert.All(layout.Nodes.Values, node =>
        {
            Assert.InRange(node.X, layout.Bounds.X, layout.Bounds.Right);
            Assert.InRange(node.Y, layout.Bounds.Y, layout.Bounds.Bottom);
        });
    }

    [Fact]
    public void LayoutOfThreeHundredNodesStaysFast()
    {
        var (nodes, edges) = BuildPack(8, 30, 3);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);
        stopwatch.Stop();

        Assert.Equal(nodes.Count, layout.Nodes.Count);
        Assert.True(stopwatch.ElapsedMilliseconds < 250,
            $"layout took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void CameraZoomKeepsWorldPointUnderCursor()
    {
        var camera = new StarGalaxyCamera(1.4, 120, -40);
        var before = camera.ScreenToWorld(320, 240);
        var zoomed = camera.ZoomAt(320, 240, 1.5);
        var after = zoomed.ScreenToWorld(320, 240);

        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
        Assert.True(zoomed.Zoom > camera.Zoom);
    }

    [Fact]
    public void CameraPanMovesWorldByDeltaOverZoom()
    {
        var camera = new StarGalaxyCamera(2.0, 10, 20);
        var before = camera.ScreenToWorld(100, 100);
        var after = camera.Panned(30, -10).ScreenToWorld(100, 100);

        Assert.Equal(before.X - 15, after.X, 6);
        Assert.Equal(before.Y + 5, after.Y, 6);
    }

    [Fact]
    public void CameraFitContainsWorldBounds()
    {
        var world = new LayoutRect(-1200, -800, 2400, 1600);
        var camera = StarGalaxyCamera.Fit(world, 900, 600, 30);
        var topLeft = camera.WorldToScreen(world.X, world.Y);
        var bottomRight = camera.WorldToScreen(world.Right, world.Bottom);

        Assert.InRange(topLeft.X, 0, 900);
        Assert.InRange(topLeft.Y, 0, 600);
        Assert.InRange(bottomRight.X, 0, 900);
        Assert.InRange(bottomRight.Y, 0, 600);
        Assert.True(zoomValid(camera.Zoom));

        static bool zoomValid(double zoom) => zoom is > 0 and <= StarGalaxyCamera.MaxZoom;
    }
}
