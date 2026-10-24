using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public readonly record struct LayoutPoint(double X, double Y);

public enum LayoutAlgorithm
{
    Auto,
    ForceDirected,
    Layered,
}

public sealed class LayoutOptions
{
    public LayoutAlgorithm Algorithm { get; set; } = LayoutAlgorithm.Auto;
    public double NodeWidth { get; set; } = 190;
    public double NodeHeight { get; set; } = 64;
    public double HorizontalGap { get; set; } = 120;
    public double VerticalGap { get; set; } = 36;
    public double Margin { get; set; } = 80;
    public int Seed { get; set; } = 20240501;
    /// <summary>Kept for callers that explicitly request force layout; auto now prefers layered.</summary>
    public int ForceNodeLimit { get; set; } = 350;
}

/// <summary>
/// Deterministic graph layout. Layered (dependency flows left to right) for large graphs,
/// Barnes-Hut-free force directed for small/medium graphs, then overlap resolution.
/// </summary>
public sealed class GraphLayoutEngine
{
    public Dictionary<string, LayoutPoint> Compute(
        GraphDocument graph,
        LayoutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LayoutOptions();
        var nodes = graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        if (nodes.Count == 0) return new Dictionary<string, LayoutPoint>(StringComparer.OrdinalIgnoreCase);

        var algorithm = options.Algorithm;
        if (algorithm == LayoutAlgorithm.Auto)
            algorithm = LayoutAlgorithm.Layered;

        cancellationToken.ThrowIfCancellationRequested();

        var positions = algorithm == LayoutAlgorithm.Layered
            ? ComputeLayered(graph, nodes, options, cancellationToken)
            : ComputeForceDirected(graph, nodes, options, cancellationToken);

        if (algorithm == LayoutAlgorithm.ForceDirected)
            ResolveOverlaps(nodes, positions, options, cancellationToken);

        Normalize(positions, options.Margin);
        return positions;
    }

    private static Dictionary<string, LayoutPoint> ComputeLayered(
        GraphDocument graph,
        List<ModNode> nodes,
        LayoutOptions options,
        CancellationToken cancellationToken)
    {
        var nodeIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hierarchical = graph.Edges
            .Where(e => e.Type is EdgeType.Requires or EdgeType.Embedded)
            .Where(e => nodeIds.Contains(e.From) && nodeIds.Contains(e.To))
            .ToList();
        if (hierarchical.Count == 0)
        {
            hierarchical = graph.Edges
                .Where(e => e.Type != EdgeType.Incompatible && e.From != e.To)
                .Where(e => nodeIds.Contains(e.From) && nodeIds.Contains(e.To))
                .ToList();
        }

        var adjacency = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var indegree = nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in hierarchical)
        {
            if (adjacency[edge.From].Contains(edge.To, StringComparer.OrdinalIgnoreCase)) continue;
            adjacency[edge.From].Add(edge.To);
            indegree[edge.To]++;
            connected.Add(edge.From);
            connected.Add(edge.To);
        }

        var layers = nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var remaining = new Dictionary<string, int>(indegree, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(nodes.Where(n => remaining[n.Id] == 0).Select(n => n.Id));
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            processed.Add(current);
            foreach (var next in adjacency[current])
            {
                layers[next] = Math.Max(layers[next], layers[current] + 1);
                remaining[next]--;
                if (remaining[next] == 0) queue.Enqueue(next);
            }
        }

        // Nodes still in cycles: place them after their resolved predecessors.
        foreach (var node in nodes.Where(n => !processed.Contains(n.Id)))
        {
            var incoming = hierarchical.Where(e => e.To == node.Id && processed.Contains(e.From)).ToList();
            if (incoming.Count > 0)
                layers[node.Id] = incoming.Max(e => layers[e.From]) + 1;
        }

        var groups = nodes
            .Where(n => connected.Contains(n.Id))
            .GroupBy(n => layers[n.Id])
            .OrderBy(g => g.Key)
            .Select(g => g.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal).ToList())
            .ToList();

        var isolated = nodes.Where(n => !connected.Contains(n.Id)).Select(n => n.Id).ToList();

        // Barycenter ordering (a few sweeps) to reduce crossings.
        for (var sweep = 0; sweep < 4; sweep++)
        {
            for (var i = 1; i < groups.Count; i++)
            {
                var previous = groups[i - 1];
                var previousIndex = previous
                    .Select((id, idx) => (id, idx))
                    .ToDictionary(p => p.id, p => p.idx, StringComparer.OrdinalIgnoreCase);

                groups[i] = groups[i]
                    .OrderBy(id =>
                    {
                        var parents = hierarchical
                            .Where(e => e.To == id && previousIndex.ContainsKey(e.From))
                            .Select(e => previousIndex[e.From])
                            .ToList();
                        return parents.Count == 0 ? double.MaxValue : parents.Average();
                    })
                    .ThenBy(id => id, StringComparer.Ordinal)
                    .ToList();
            }
        }

        var positions = new Dictionary<string, LayoutPoint>(StringComparer.OrdinalIgnoreCase);
        var xStep = options.NodeWidth + options.HorizontalGap;
        var yStep = options.NodeHeight + options.VerticalGap;
        var subColumnStep = options.NodeWidth + options.HorizontalGap * 0.55;
        var maxRows = nodes.Count <= 30 ? 7 : 9;
        var maxY = 0.0;
        var layerX = 0.0;

        for (var layer = 0; layer < groups.Count; layer++)
        {
            var group = groups[layer];

            // Very wide layers make graphs tall and unreadable after fit-to-view.
            // Wrap each layer into a small grid of sub-columns instead.
            var subColumns = Math.Max(1, (int)Math.Ceiling(group.Count / (double)maxRows));
            var rows = (int)Math.Ceiling(group.Count / (double)subColumns);
            for (var index = 0; index < group.Count; index++)
            {
                var column = index / rows;
                var row = index % rows;
                positions[group[index]] = new LayoutPoint(
                    layerX + column * subColumnStep,
                    (row - (rows - 1) / 2.0) * yStep);
            }

            maxY = Math.Max(maxY, (rows - 1) / 2.0 * yStep);
            layerX += subColumns * subColumnStep + options.HorizontalGap;
        }

        if (isolated.Count > 0)
        {
            var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(isolated.Count)));
            var startY = maxY + options.VerticalGap * 2 + options.NodeHeight;
            for (var i = 0; i < isolated.Count; i++)
            {
                var row = i / columns;
                var column = i % columns;
                positions[isolated[i]] = new LayoutPoint(column * xStep, startY + row * yStep);
            }
        }

        return positions;
    }

    private static Dictionary<string, LayoutPoint> ComputeForceDirected(
        GraphDocument graph,
        List<ModNode> nodes,
        LayoutOptions options,
        CancellationToken cancellationToken)
    {
        var count = nodes.Count;
        var width = Math.Max(1200, Math.Sqrt(count) * (options.NodeWidth + options.HorizontalGap) * 1.5);
        var height = Math.Max(800, Math.Sqrt(count) * (options.NodeHeight + options.VerticalGap) * 2.2);
        var area = width * height;
        var k = Math.Sqrt(area / count);

        var random = new Random(options.Seed);
        var positions = new double[count, 2];
        for (var i = 0; i < count; i++)
        {
            // Golden-angle spiral gives a deterministic, non-degenerate start.
            var angle = i * 2.399963229728653;
            var radius = Math.Sqrt((i + 1) / (double)count) * Math.Min(width, height) * 0.42;
            positions[i, 0] = width / 2 + Math.Cos(angle) * radius + (random.NextDouble() - 0.5) * 8;
            positions[i, 1] = height / 2 + Math.Sin(angle) * radius + (random.NextDouble() - 0.5) * 8;
        }

        var index = nodes.Select((n, i) => (n.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.OrdinalIgnoreCase);
        var edges = graph.Edges
            .Where(e => index.ContainsKey(e.From) && index.ContainsKey(e.To) && e.From != e.To)
            .Select(e => (A: index[e.From], B: index[e.To]))
            .Distinct()
            .ToList();

        var iterations = count <= 100 ? 420 : count <= 220 ? 300 : 200;
        var displacement = new double[count, 2];
        var temperature = Math.Min(width, height) / 8.0;
        var cooling = temperature / (iterations + 1);

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if ((iteration & 7) == 0) cancellationToken.ThrowIfCancellationRequested();

            Array.Clear(displacement);

            // Repulsion (O(n^2), bounded by the layered fallback for very large graphs).
            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    var dx = positions[i, 0] - positions[j, 0];
                    var dy = positions[i, 1] - positions[j, 1];
                    var distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared < 0.01)
                    {
                        dx = (random.NextDouble() - 0.5) * 0.1;
                        dy = (random.NextDouble() - 0.5) * 0.1;
                        distanceSquared = dx * dx + dy * dy + 0.01;
                    }

                    var distance = Math.Sqrt(distanceSquared);
                    var force = k * k / distanceSquared;
                    var fx = dx / distance * force;
                    var fy = dy / distance * force;

                    displacement[i, 0] += fx;
                    displacement[i, 1] += fy;
                    displacement[j, 0] -= fx;
                    displacement[j, 1] -= fy;
                }
            }

            // Attraction along edges.
            foreach (var (a, b) in edges)
            {
                var dx = positions[a, 0] - positions[b, 0];
                var dy = positions[a, 1] - positions[b, 1];
                var distance = Math.Sqrt(dx * dx + dy * dy) + 0.01;
                var force = distance * distance / k;
                var fx = dx / distance * force;
                var fy = dy / distance * force;

                displacement[a, 0] -= fx;
                displacement[a, 1] -= fy;
                displacement[b, 0] += fx;
                displacement[b, 1] += fy;
            }

            // Gravity towards the centre keeps disconnected components together.
            for (var i = 0; i < count; i++)
            {
                displacement[i, 0] += (width / 2 - positions[i, 0]) * 0.012;
                displacement[i, 1] += (height / 2 - positions[i, 1]) * 0.012;

                var length = Math.Sqrt(displacement[i, 0] * displacement[i, 0] + displacement[i, 1] * displacement[i, 1]);
                if (length < 0.0001) continue;

                var limited = Math.Min(length, temperature);
                positions[i, 0] += displacement[i, 0] / length * limited;
                positions[i, 1] += displacement[i, 1] / length * limited;

                // Stay inside a generous bounding box.
                positions[i, 0] = Math.Clamp(positions[i, 0], 0, width);
                positions[i, 1] = Math.Clamp(positions[i, 1], 0, height);
            }

            temperature = Math.Max(0.1, temperature - cooling);
        }

        return nodes.Select((n, i) => (n.Id, Point: new LayoutPoint(positions[i, 0], positions[i, 1])))
            .ToDictionary(p => p.Id, p => p.Point, StringComparer.OrdinalIgnoreCase);
    }

    private static void ResolveOverlaps(
        List<ModNode> nodes,
        Dictionary<string, LayoutPoint> positions,
        LayoutOptions options,
        CancellationToken cancellationToken)
    {
        var minX = options.NodeWidth + 16;
        var minY = options.NodeHeight + 14;

        for (var iteration = 0; iteration < 40; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moved = false;

            for (var i = 0; i < nodes.Count; i++)
            {
                for (var j = i + 1; j < nodes.Count; j++)
                {
                    var a = nodes[i].Id;
                    var b = nodes[j].Id;
                    var pa = positions[a];
                    var pb = positions[b];
                    var dx = pb.X - pa.X;
                    var dy = pb.Y - pa.Y;

                    var overlapX = minX - Math.Abs(dx);
                    var overlapY = minY - Math.Abs(dy);
                    if (overlapX <= 0 || overlapY <= 0) continue;

                    moved = true;
                    if (overlapX < overlapY)
                    {
                        var shift = overlapX / 2 * (dx >= 0 ? 1 : -1);
                        positions[a] = new LayoutPoint(pa.X - shift, pa.Y);
                        positions[b] = new LayoutPoint(pb.X + shift, pb.Y);
                    }
                    else
                    {
                        var shift = overlapY / 2 * (dy >= 0 ? 1 : -1);
                        positions[a] = new LayoutPoint(pa.X, pa.Y - shift);
                        positions[b] = new LayoutPoint(pb.X, pb.Y + shift);
                    }
                }
            }

            if (!moved) break;
        }
    }

    private static void Normalize(Dictionary<string, LayoutPoint> positions, double margin)
    {
        if (positions.Count == 0) return;
        var minX = positions.Values.Min(p => p.X);
        var minY = positions.Values.Min(p => p.Y);
        foreach (var id in positions.Keys.ToList())
        {
            var point = positions[id];
            positions[id] = new LayoutPoint(point.X - minX + margin, point.Y - minY + margin);
        }
    }
}
