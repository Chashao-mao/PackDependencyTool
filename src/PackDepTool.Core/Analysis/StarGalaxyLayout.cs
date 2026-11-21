using System.Text;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public LayoutPoint Center => new(X + Width / 2, Y + Height / 2);

    public bool Contains(LayoutPoint point) =>
        point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

    public bool IntersectsWith(LayoutRect other) =>
        X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;

    public LayoutRect Inflate(double amount) =>
        new(X - amount, Y - amount, Math.Max(0, Width + amount * 2), Math.Max(0, Height + amount * 2));

    public static LayoutRect FromCenter(double centerX, double centerY, double width, double height) =>
        new(centerX - width / 2, centerY - height / 2, width, height);

    public static LayoutRect FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
}

public sealed class StarGalaxyOptions
{
    /// <summary>Used to derive the number of galaxies: ceil(nodeCount / TargetPerGalaxy).</summary>
    public int TargetPerGalaxy { get; set; } = 48;

    /// <summary>Hard cap for a single galaxy. Larger galaxies are split by a secondary hub.</summary>
    public int MaxGalaxyNodes { get; set; } = 55;

    public int MinGalaxyNodes { get; set; } = 8;

    /// <summary>Clusters smaller than this are folded into a nearby cluster while capacity allows.</summary>
    public int MergeBelowNodes { get; set; } = 24;

    /// <summary>Maximum nodes per visual ring; larger depths are split into concentric sub-rings.</summary>
    public int SubRingCapacity { get; set; } = 20;

    public double NodeRadius { get; set; } = 22;
    public double HubRadius { get; set; } = 30;
    public double BaseRingRadius { get; set; } = 250;
    public double RingGap { get; set; } = 140;
    public double MinNodeSpacing { get; set; } = 74;
    public double GalaxyGap { get; set; } = 420;
    public double LabelFontSize { get; set; } = 11;
    public double LabelHeight { get; set; } = 16;
    public int MaxDepthRings { get; set; } = 6;
    public int MaxGalaxies { get; set; } = 8;

    /// <summary>Keep fully disconnected mods out of the spatial layout (UI panel instead).</summary>
    public bool SeparateUnconnected { get; set; }

    /// <summary>When set (double click), this node becomes the hub of galaxy 0 and its neighborhood is kept together.</summary>
    public string? FocusNodeId { get; set; }

    public int Seed { get; set; } = 20261120;
}

public sealed class StarGalaxyCluster
{
    public required int Index { get; init; }
    public required string HubId { get; init; }
    public required double CenterX { get; init; }
    public required double CenterY { get; init; }
    public required double Radius { get; init; }
    public required IReadOnlyList<string> NodeIds { get; init; }
    public required int InternalEdges { get; init; }
    public required int CrossEdges { get; init; }

    /// <summary>True for the explicit "no dependency edges" group.</summary>
    public bool IsIsolated { get; init; }
}

public sealed class StarGalaxyNodePlacement
{
    public required string Id { get; init; }
    public required int GalaxyIndex { get; init; }
    public required int Ring { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required bool IsHub { get; init; }
    public required int Degree { get; init; }
    public required bool LabelVisible { get; init; }
    public required LayoutRect LabelRect { get; init; }
}

/// <summary>Aggregated, undirected cross-galaxy relationship (one entry per galaxy pair).</summary>
public sealed class StarGalaxyCrossLink
{
    public required int FromGalaxy { get; init; }
    public required int ToGalaxy { get; init; }
    public required int Count { get; init; }
    public required int RequiresCount { get; init; }
}

public sealed class StarGalaxyLayout
{
    public IReadOnlyList<StarGalaxyCluster> Galaxies { get; init; } = [];
    public IReadOnlyDictionary<string, StarGalaxyNodePlacement> Nodes { get; init; } =
        new Dictionary<string, StarGalaxyNodePlacement>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<StarGalaxyCrossLink> CrossLinks { get; init; } = [];
    public IReadOnlyList<string> UnconnectedIds { get; init; } = [];
    public LayoutRect Bounds { get; init; }

    public static StarGalaxyLayout Empty { get; } = new();
}

/// <summary>
/// Deterministic, one-shot galaxy layout for the native star view:
/// hubs -> balanced clusters -> depth rings -> collision / label avoidance.
/// Deliberately independent of Avalonia so it can be unit tested and computed off the render path.
/// </summary>
public sealed class StarGalaxyLayoutEngine
{
    private const int WeightRequires = 3;
    private const int WeightEmbedded = 2;
    private const int WeightOther = 1;

    public StarGalaxyLayout Compute(
        IReadOnlyList<ModNode> nodes,
        IReadOnlyList<DependencyEdge> edges,
        StarGalaxyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opt = options ?? new StarGalaxyOptions();
        opt.MaxGalaxyNodes = Math.Max(1, opt.MaxGalaxyNodes);
        opt.MinGalaxyNodes = Math.Clamp(opt.MinGalaxyNodes, 1, opt.MaxGalaxyNodes);
        opt.MaxDepthRings = Math.Max(1, opt.MaxDepthRings);

        var orderedNodes = Deduplicate(nodes);
        if (orderedNodes.Count == 0) return StarGalaxyLayout.Empty;

        var byId = orderedNodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var adjacency = BuildAdjacency(orderedNodes, edges, byId, out var score, out var degree);
        var allIds = orderedNodes.Select(node => node.Id).ToList();
        var target = Math.Clamp(
            (int)Math.Ceiling(allIds.Count / (double)Math.Max(1, opt.TargetPerGalaxy)),
            1,
            Math.Max(1, opt.MaxGalaxies));

        cancellationToken.ThrowIfCancellationRequested();

        var focusId = opt.FocusNodeId;
        if (focusId is not null && !byId.ContainsKey(focusId)) focusId = null;

        var clusters = new List<MutableCluster>();
        var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (focusId is not null)
        {
            var focusCluster = new MutableCluster(0, focusId);
            foreach (var id in BfsOrder(focusId, adjacency, opt.MaxGalaxyNodes))
            {
                focusCluster.Add(id);
                owner[id] = 0;
            }
            clusters.Add(focusCluster);
        }

        var remainingIds = allIds.Where(id => !owner.ContainsKey(id)).ToList();
        var seedTarget = focusId is null ? target : Math.Max(0, target - 1);
        seedTarget = Math.Min(seedTarget, remainingIds.Count);
        var seeds = PickSeeds(remainingIds, seedTarget, adjacency, score, degree);
        var offset = clusters.Count;
        for (var i = 0; i < seeds.Count; i++)
        {
            var cluster = new MutableCluster(offset + i, seeds[i]);
            cluster.Add(seeds[i]);
            clusters.Add(cluster);
        }

        var seedQueue = new Queue<string>(seeds);
        for (var i = 0; i < seeds.Count; i++) owner[seeds[i]] = offset + i;
        while (seedQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = seedQueue.Dequeue();
            var currentOwner = owner[current];
            foreach (var next in adjacency[current])
            {
                if (owner.ContainsKey(next)) continue;
                owner[next] = currentOwner;
                clusters[currentOwner].Add(next);
                seedQueue.Enqueue(next);
            }
        }

        var isolated = allIds.Where(id => !owner.ContainsKey(id)).ToList();
        if (clusters.Count == 0)
        {
            for (var start = 0; start < isolated.Count; start += opt.MaxGalaxyNodes)
            {
                var chunk = isolated.Skip(start).Take(opt.MaxGalaxyNodes).ToList();
                var cluster = new MutableCluster(clusters.Count, chunk[0]);
                foreach (var id in chunk)
                {
                    cluster.Add(id);
                    owner[id] = cluster.Index;
                }
                clusters.Add(cluster);
            }
        }
        else
        {
            foreach (var id in isolated)
            {
                var targetCluster = clusters
                    .OrderBy(cluster => cluster.NodeIds.Count)
                    .ThenBy(cluster => cluster.Index)
                    .First();
                owner[id] = targetCluster.Index;
                targetCluster.Add(id);
            }
        }

        EnsureCapacity(clusters, adjacency, score, degree, opt, cancellationToken);
        MergeTinyClusters(clusters, opt, focusId);
        MergeSparseClusters(clusters, edges, opt, focusId, target);

        // Balanced splitting is geometry-first; this pass is relationship-first:
        // every node moves toward the galaxy it actually shares the most
        // dependency weight with, and fully disconnected mods are separated
        // into an explicit "unconnected" galaxy instead of polluting a hub.
        var weightedAdjacency = BuildWeightedAdjacency(orderedNodes, edges, byId);
        RefineByAffinity(clusters, weightedAdjacency, score, opt, focusId, cancellationToken);

        var unconnectedIds = new List<string>();
        if (opt.SeparateUnconnected)
        {
            foreach (var cluster in clusters.Where(cluster => cluster.IsIsolated).ToList())
            {
                unconnectedIds.AddRange(cluster.NodeIds);
                clusters.Remove(cluster);
            }
        }

        clusters = OrderClusters(clusters, focusId, score);
        for (var i = 0; i < clusters.Count; i++) clusters[i].Index = i;

        // Cluster membership is the single source of truth after ordering/merging.
        owner.Clear();
        for (var i = 0; i < clusters.Count; i++)
            foreach (var id in clusters[i].NodeIds)
                owner[id] = i;

        var placements = new Dictionary<string, StarGalaxyNodePlacement>(StringComparer.OrdinalIgnoreCase);
        var clusterInfos = new List<ClusterLayout>(clusters.Count);

        foreach (var cluster in clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clusterInfos.Add(BuildLocalPlacement(cluster, adjacency, score, opt));
        }

        var affinity = BuildGalaxyAffinity(clusterInfos, edges, owner);
        var centers = PlaceGalaxyCenters(clusterInfos, affinity, opt);

        var world = new Dictionary<string, LayoutPoint>(StringComparer.OrdinalIgnoreCase);
        var nodeMeta = new Dictionary<string, (int Galaxy, int Ring, bool Hub)>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < clusterInfos.Count; index++)
        {
            var info = clusterInfos[index];
            var center = centers[index];
            foreach (var id in info.Cluster.NodeIds)
            {
                var local = info.Local.GetValueOrDefault(id);
                world[id] = new LayoutPoint(center.X + local.X, center.Y + local.Y);
                nodeMeta[id] = (index, info.Rings.GetValueOrDefault(id, 0),
                    string.Equals(id, info.Cluster.HubId, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Circular bounding box per galaxy (used by the overview focus hit-testing).
        var galaxyRadii = new double[clusterInfos.Count];
        foreach (var (id, point) in world)
        {
            var meta = nodeMeta[id];
            var center = centers[meta.Galaxy];
            var radius = meta.Hub ? opt.HubRadius : opt.NodeRadius;
            var distance = Math.Sqrt(Math.Pow(point.X - center.X, 2) + Math.Pow(point.Y - center.Y, 2)) + radius;
            galaxyRadii[meta.Galaxy] = Math.Max(galaxyRadii[meta.Galaxy], distance);
        }
        for (var i = 0; i < galaxyRadii.Length; i++) galaxyRadii[i] += 34;

        var spatialNodes = orderedNodes.Where(node => world.ContainsKey(node.Id)).ToList();
        var labels = BuildLabels(spatialNodes, world, nodeMeta, score, degree, opt);
        var bounds = ComputeBounds(world, nodeMeta, labels, opt);
        var crossLinks = BuildCrossLinks(edges, byId, owner);

        var clusterInfoList = new List<StarGalaxyCluster>(clusterInfos.Count);
        for (var i = 0; i < clusterInfos.Count; i++)
        {
            var info = clusterInfos[i];
            var cluster = info.Cluster;
            var rings = info.Rings;
            var internalEdges = CountInternalEdges(cluster, edges);
            var crossEdges = edges.Count(edge =>
                owner.TryGetValue(edge.From, out var from) && owner.TryGetValue(edge.To, out var to) &&
                from == i && to != i);
            var nodeIds = cluster.NodeIds
                .OrderBy(id => rings.GetValueOrDefault(id, 0))
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            clusterInfoList.Add(new StarGalaxyCluster
            {
                Index = i,
                HubId = cluster.HubId,
                CenterX = centers[i].X,
                CenterY = centers[i].Y,
                Radius = galaxyRadii[i],
                NodeIds = nodeIds,
                InternalEdges = internalEdges,
                CrossEdges = crossEdges,
                IsIsolated = cluster.IsIsolated,
            });
        }

        foreach (var node in spatialNodes)
        {
            var meta = nodeMeta[node.Id];
            var point = world[node.Id];
            var label = labels.GetValueOrDefault(node.Id);
            placements[node.Id] = new StarGalaxyNodePlacement
            {
                Id = node.Id,
                GalaxyIndex = meta.Galaxy,
                Ring = meta.Ring,
                X = point.X,
                Y = point.Y,
                IsHub = meta.Hub,
                Degree = degree.GetValueOrDefault(node.Id),
                LabelVisible = label.Visible,
                LabelRect = label.Rect,
            };
        }

        return new StarGalaxyLayout
        {
            Galaxies = clusterInfoList,
            Nodes = placements,
            CrossLinks = crossLinks,
            UnconnectedIds = unconnectedIds,
            Bounds = bounds,
        };
    }

    // ------------------------------------------------------------------ graph prep

    private static List<ModNode> Deduplicate(IReadOnlyList<ModNode> nodes)
    {
        var result = new List<ModNode>(nodes.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id)) continue;
            if (!seen.Add(node.Id)) continue;
            result.Add(node);
        }
        return result;
    }

    private static Dictionary<string, List<string>> BuildAdjacency(
        IReadOnlyList<ModNode> nodes,
        IReadOnlyList<DependencyEdge> edges,
        IReadOnlyDictionary<string, ModNode> byId,
        out Dictionary<string, int> score,
        out Dictionary<string, int> degree)
    {
        var adjacency = nodes.ToDictionary(node => node.Id,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        var localScore = nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var localDegree = nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            if (edge.Type == EdgeType.Incompatible) continue;
            if (string.IsNullOrWhiteSpace(edge.From) || string.IsNullOrWhiteSpace(edge.To)) continue;
            if (string.Equals(edge.From, edge.To, StringComparison.OrdinalIgnoreCase)) continue;
            if (!byId.ContainsKey(edge.From) || !byId.ContainsKey(edge.To)) continue;

            var weight = edge.Type switch
            {
                EdgeType.Requires => WeightRequires,
                EdgeType.Embedded => WeightEmbedded,
                _ => WeightOther,
            };

            if (adjacency[edge.From].Contains(edge.To, StringComparer.OrdinalIgnoreCase)) continue;
            adjacency[edge.From].Add(edge.To);
            adjacency[edge.To].Add(edge.From);
            localScore[edge.From] += weight;
            localScore[edge.To] += weight;
            localDegree[edge.From]++;
            localDegree[edge.To]++;
        }

        foreach (var id in adjacency.Keys.ToList())
        {
            adjacency[id].Sort((left, right) =>
            {
                var byScore = localScore[right].CompareTo(localScore[left]);
                if (byScore != 0) return byScore;
                var byDegree = localDegree[right].CompareTo(localDegree[left]);
                return byDegree != 0
                    ? byDegree
                    : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            });
        }

        score = localScore;
        degree = localDegree;
        return adjacency;
    }

    private static List<string> PickSeeds(
        IReadOnlyList<string> candidates,
        int target,
        Dictionary<string, List<string>> adjacency,
        IReadOnlyDictionary<string, int> score,
        IReadOnlyDictionary<string, int> degree)
    {
        var result = new List<string>();
        if (target <= 0 || candidates.Count == 0) return result;

        var candidateSet = candidates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = candidates
            .OrderByDescending(id => score.GetValueOrDefault(id))
            .ThenByDescending(id => degree.GetValueOrDefault(id))
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var pass = 0; pass < 2 && result.Count < target; pass++)
        {
            foreach (var id in ordered)
            {
                if (result.Count >= target) break;
                if (!candidateSet.Contains(id)) continue;
                if (result.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                if (pass == 0 && result.Count > 0 && HopDistance(id, result, adjacency) < 2) continue;
                result.Add(id);
            }
        }

        return result;
    }

    private static int HopDistance(
        string start,
        IReadOnlyCollection<string> targets,
        Dictionary<string, List<string>> adjacency,
        int maxHops = 2)
    {
        if (targets.Contains(start, StringComparer.OrdinalIgnoreCase)) return 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var frontier = new List<string> { start };
        for (var hop = 1; hop <= maxHops; hop++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                if (!adjacency.TryGetValue(current, out var neighbors)) continue;
                foreach (var neighbor in neighbors)
                {
                    if (!visited.Add(neighbor)) continue;
                    if (targets.Contains(neighbor, StringComparer.OrdinalIgnoreCase)) return hop;
                    next.Add(neighbor);
                }
            }
            if (next.Count == 0) break;
            frontier = next;
        }
        return maxHops + 1;
    }

    private static List<string> BfsOrder(string root, Dictionary<string, List<string>> adjacency, int limit)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0 && result.Count < limit)
        {
            var current = queue.Dequeue();
            result.Add(current);
            if (!adjacency.TryGetValue(current, out var neighbors)) continue;
            foreach (var neighbor in neighbors)
                if (visited.Add(neighbor)) queue.Enqueue(neighbor);
        }
        return result;
    }

    private static Dictionary<string, int> BfsDepths(
        HashSet<string> allowed,
        string root,
        Dictionary<string, List<string>> adjacency)
    {
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [root] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var neighbors)) continue;
            foreach (var neighbor in neighbors)
            {
                if (!allowed.Contains(neighbor) || depths.ContainsKey(neighbor)) continue;
                depths[neighbor] = depths[current] + 1;
                queue.Enqueue(neighbor);
            }
        }
        return depths;
    }

    // ------------------------------------------------------------------ balancing

    private static void EnsureCapacity(
        List<MutableCluster> clusters,
        Dictionary<string, List<string>> adjacency,
        IReadOnlyDictionary<string, int> score,
        IReadOnlyDictionary<string, int> degree,
        StarGalaxyOptions opt,
        CancellationToken cancellationToken)
    {
        for (var guard = 0; guard < 4096; guard++)
        {
            var oversized = clusters
                .Where(cluster => cluster.NodeIds.Count > opt.MaxGalaxyNodes)
                .OrderByDescending(cluster => cluster.NodeIds.Count)
                .ThenBy(cluster => cluster.Index)
                .FirstOrDefault();
            if (oversized is null) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (!SplitCluster(oversized, clusters, adjacency, score, opt)) return;
        }
    }

    private static bool SplitCluster(
        MutableCluster cluster,
        List<MutableCluster> clusters,
        Dictionary<string, List<string>> adjacency,
        IReadOnlyDictionary<string, int> score,
        StarGalaxyOptions opt)
    {
        if (cluster.NodeIds.Count <= opt.MaxGalaxyNodes) return false;
        var hub = cluster.HubId;
        var internalDegree = cluster.NodeIds.ToDictionary(
            id => id,
            id => adjacency[id].Count(cluster.Set.Contains),
            StringComparer.OrdinalIgnoreCase);

        var secondary = cluster.NodeIds
            .Where(id => !string.Equals(id, hub, StringComparison.OrdinalIgnoreCase))
            .Where(id => internalDegree.GetValueOrDefault(id) >= 2)
            .OrderByDescending(id => internalDegree.GetValueOrDefault(id))
            .ThenByDescending(id => score.GetValueOrDefault(id))
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        secondary ??= cluster.NodeIds
            .Where(id => !string.Equals(id, hub, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(id => score.GetValueOrDefault(id))
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (secondary is null) return false;

        var primaryDepths = BfsDepths(cluster.Set, hub, adjacency);
        var secondaryDepths = BfsDepths(cluster.Set, secondary, adjacency);

        // Balanced split: the new cluster takes half of the nodes, ordered by
        // how close they are to the secondary hub. This avoids the old behavior
        // where a star-shaped hub peeled off one leaf at a time and produced a
        // tail of 2-node "galaxies".
        var keepAtLeast = Math.Min(opt.MinGalaxyNodes, cluster.NodeIds.Count - 1);
        var take = Math.Clamp(cluster.NodeIds.Count / 2, 1,
            Math.Max(1, cluster.NodeIds.Count - keepAtLeast));
        var stolen = new List<string> { secondary };
        foreach (var id in cluster.NodeIds
                     .Where(id => !string.Equals(id, hub, StringComparison.OrdinalIgnoreCase))
                     .Where(id => !string.Equals(id, secondary, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(id => secondaryDepths.GetValueOrDefault(id, int.MaxValue))
                     .ThenBy(id => primaryDepths.GetValueOrDefault(id, int.MaxValue))
                     .ThenByDescending(id => score.GetValueOrDefault(id))
                     .ThenBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            if (stolen.Count >= take) break;
            stolen.Add(id);
        }

        if (stolen.Count == 0) return false;

        var newCluster = new MutableCluster(clusters.Count, secondary);
        foreach (var id in stolen)
        {
            cluster.Remove(id);
            newCluster.Add(id);
        }

        if (newCluster.NodeIds.Count == 0 || cluster.NodeIds.Count == 0) return false;
        clusters.Add(newCluster);
        return true;
    }

    private static void MergeTinyClusters(List<MutableCluster> clusters, StarGalaxyOptions opt, string? focusId)
    {
        for (var index = clusters.Count - 1; index >= 0 && clusters.Count > 1; index--)
        {
            var cluster = clusters[index];
            if (cluster.NodeIds.Count >= opt.MinGalaxyNodes) continue;
            if (focusId is not null &&
                string.Equals(cluster.HubId, focusId, StringComparison.OrdinalIgnoreCase)) continue;

            var destination = clusters
                .Where(other => !ReferenceEquals(other, cluster))
                .Where(other => other.NodeIds.Count + cluster.NodeIds.Count <= opt.MaxGalaxyNodes)
                .OrderBy(other => other.NodeIds.Count)
                .ThenBy(other => other.Index)
                .FirstOrDefault();
            if (destination is null) continue;

            var moved = cluster.NodeIds.ToList();
            foreach (var id in moved)
            {
                cluster.Remove(id);
                destination.Add(id);
            }
            clusters.RemoveAt(index);
        }
    }

    private static void MergeSparseClusters(
        List<MutableCluster> clusters,
        IReadOnlyList<DependencyEdge> edges,
        StarGalaxyOptions opt,
        string? focusId,
        int targetGalaxies)
    {
        var minimum = Math.Max(1, targetGalaxies);
        for (var guard = 0; guard < 256 && clusters.Count > minimum; guard++)
        {
            var sources = clusters
                .Where(cluster => cluster.NodeIds.Count < opt.MergeBelowNodes)
                .Where(cluster => focusId is null ||
                                  !string.Equals(cluster.HubId, focusId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(cluster => cluster.NodeIds.Count)
                .ThenBy(cluster => cluster.HubId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var merged = false;
            foreach (var source in sources)
            {
                var destination = clusters
                    .Where(other => !ReferenceEquals(other, source))
                    .Where(other => other.NodeIds.Count + source.NodeIds.Count <= opt.MaxGalaxyNodes)
                    .OrderByDescending(other => Affinity(source, other, edges))
                    .ThenBy(other => other.NodeIds.Count)
                    .ThenBy(other => other.HubId, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (destination is null) continue;

                var moved = source.NodeIds.ToList();
                foreach (var id in moved)
                {
                    source.Remove(id);
                    destination.Add(id);
                }
                clusters.Remove(source);
                merged = true;
                break;
            }

            if (!merged) break;
        }
    }

    private static Dictionary<string, Dictionary<string, int>> BuildWeightedAdjacency(
        IReadOnlyList<ModNode> nodes,
        IReadOnlyList<DependencyEdge> edges,
        IReadOnlyDictionary<string, ModNode> byId)
    {
        var result = nodes.ToDictionary(
            node => node.Id,
            _ => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            if (edge.Type == EdgeType.Incompatible) continue;
            if (string.IsNullOrWhiteSpace(edge.From) || string.IsNullOrWhiteSpace(edge.To)) continue;
            if (string.Equals(edge.From, edge.To, StringComparison.OrdinalIgnoreCase)) continue;
            if (!byId.ContainsKey(edge.From) || !byId.ContainsKey(edge.To)) continue;

            var weight = edge.Type switch
            {
                EdgeType.Requires => 3,
                EdgeType.Embedded => 2,
                _ => 1,
            };
            Add(edge.From, edge.To, weight);
            Add(edge.To, edge.From, weight);

            void Add(string from, string to, int value)
            {
                var neighbours = result[from];
                if (!neighbours.TryGetValue(to, out var existing) || value > existing)
                    neighbours[to] = value;
            }
        }
        return result;
    }

    private static void RefineByAffinity(
        List<MutableCluster> clusters,
        Dictionary<string, Dictionary<string, int>> adjacency,
        IReadOnlyDictionary<string, int> score,
        StarGalaxyOptions opt,
        string? focusId,
        CancellationToken cancellationToken)
    {
        // 1. Fully disconnected mods get their own galaxy. Mixing them into a
        // dependency hub was the main reason galaxies contained unrelated mods.
        var isolated = adjacency
            .Where(pair => pair.Value.Count == 0)
            .Select(pair => pair.Key)
            .Where(id => focusId is null ||
                         !string.Equals(id, focusId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (isolated.Count > 0)
        {
            foreach (var cluster in clusters)
                foreach (var id in isolated.Where(cluster.Set.Contains).ToList())
                    cluster.Remove(id);
            clusters.RemoveAll(cluster => cluster.NodeIds.Count == 0);

            for (var start = 0; start < isolated.Count; start += opt.MaxGalaxyNodes)
            {
                var chunk = isolated.Skip(start).Take(opt.MaxGalaxyNodes).ToList();
                var misc = new MutableCluster(clusters.Count, chunk[0]) { IsIsolated = true };
                foreach (var id in chunk) misc.Add(id);
                clusters.Add(misc);
            }
        }

        if (clusters.Count == 0) return;

        var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < clusters.Count; index++)
            foreach (var id in clusters[index].NodeIds)
                owner[id] = index;

        var affinity = new int[clusters.Count];
        var ordered = adjacency.Keys
            .OrderByDescending(id => adjacency[id].Values.Sum())
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var pass = 0; pass < 10; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changed = false;
            foreach (var id in ordered)
            {
                if (focusId is not null &&
                    string.Equals(id, focusId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!owner.TryGetValue(id, out var current) || current >= clusters.Count) continue;
                if (adjacency[id].Count == 0) continue;

                Array.Clear(affinity);
                foreach (var (neighbour, weight) in adjacency[id])
                {
                    if (!owner.TryGetValue(neighbour, out var galaxy)) continue;
                    if (galaxy < affinity.Length) affinity[galaxy] += weight;
                }

                var best = current;
                var bestWeight = affinity[current];
                for (var galaxy = 0; galaxy < affinity.Length; galaxy++)
                {
                    if (galaxy == current) continue;
                    if (affinity[galaxy] <= bestWeight) continue;
                    if (clusters[galaxy].NodeIds.Count >= opt.MaxGalaxyNodes) continue;
                    best = galaxy;
                    bestWeight = affinity[galaxy];
                }

                if (best == current) continue;
                clusters[current].Remove(id);
                clusters[best].Add(id);
                owner[id] = best;
                changed = true;
            }

            if (!changed) break;
        }

        clusters.RemoveAll(cluster => cluster.NodeIds.Count == 0);

        // 2. A capacity limit can leave a few connected mods with no room in
        // their true galaxy. Do not leave them inside an unrelated hub: move
        // them to the explicit "unconnected/other" group instead.
        CleanUnrelatedMembers(clusters, adjacency, opt, focusId);

        // 3. Re-elect the hub from actual internal relationships, not from the
        // seed that happened to own the region.
        foreach (var cluster in clusters)
        {
            if (focusId is not null &&
                cluster.Set.Contains(focusId))
            {
                cluster.HubId = focusId;
                continue;
            }

            var hub = cluster.NodeIds
                .OrderByDescending(id => cluster.Set
                    .Where(neighbour => adjacency[id].ContainsKey(neighbour))
                    .Sum(neighbour => adjacency[id][neighbour]))
                .ThenByDescending(id => score.GetValueOrDefault(id))
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .First();
            cluster.HubId = hub;
            cluster.IsIsolated = !cluster.NodeIds.Any(id =>
                adjacency[id].Keys.Any(cluster.Set.Contains));
        }

        MergeIsolatedClusters(clusters);
    }

    private static void MergeIsolatedClusters(List<MutableCluster> clusters)
    {
        var isolated = clusters.Where(cluster => cluster.IsIsolated).ToList();
        if (isolated.Count <= 1) return;

        // These mods have no relationship at all, so splitting them into
        // multiple "galaxies" is pure noise. Keep one compact gray grid.
        var ids = isolated
            .SelectMany(cluster => cluster.NodeIds)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var cluster in isolated) clusters.Remove(cluster);
        if (ids.Count == 0) return;

        var misc = new MutableCluster(clusters.Count, ids[0]) { IsIsolated = true };
        foreach (var id in ids) misc.Add(id);
        clusters.Add(misc);
    }

    private static void CleanUnrelatedMembers(
        List<MutableCluster> clusters,
        Dictionary<string, Dictionary<string, int>> adjacency,
        StarGalaxyOptions opt,
        string? focusId)
    {
        MutableCluster? misc = clusters.FirstOrDefault(cluster => cluster.IsIsolated);

        MutableCluster GetMisc(string firstId)
        {
            if (misc is not null && misc.NodeIds.Count < opt.MaxGalaxyNodes) return misc;
            misc = new MutableCluster(clusters.Count, firstId) { IsIsolated = true };
            clusters.Add(misc);
            return misc;
        }

        foreach (var cluster in clusters.Where(cluster => !cluster.IsIsolated).ToList())
        {
            if (focusId is not null && cluster.Set.Contains(focusId)) continue;
            var unrelated = cluster.NodeIds
                .Where(id => adjacency[id].Count > 0 &&
                             !cluster.Set.Any(neighbour =>
                                 !string.Equals(neighbour, id, StringComparison.OrdinalIgnoreCase) &&
                                 adjacency[id].ContainsKey(neighbour)))
                .ToList();
            foreach (var id in unrelated)
            {
                cluster.Remove(id);
                GetMisc(id).Add(id);
            }

            var hasInternalEdge = cluster.NodeIds.Any(id =>
                adjacency[id].Keys.Any(cluster.Set.Contains));
            if (hasInternalEdge) continue;

            // Degenerate cluster: only leftovers. Fold it into the misc group.
            var leftover = cluster.NodeIds.ToList();
            foreach (var id in leftover)
            {
                cluster.Remove(id);
                GetMisc(id).Add(id);
            }
            clusters.Remove(cluster);
        }
    }

    private static int Affinity(MutableCluster source, MutableCluster destination,
        IReadOnlyList<DependencyEdge> edges)
    {
        var score = 0;
        foreach (var edge in edges)
        {
            if (edge.Type == EdgeType.Incompatible) continue;
            var forward = source.Set.Contains(edge.From) && destination.Set.Contains(edge.To);
            var backward = destination.Set.Contains(edge.From) && source.Set.Contains(edge.To);
            if (!forward && !backward) continue;
            score += edge.Type == EdgeType.Requires ? 2 : 1;
        }
        return score;
    }

    private static List<MutableCluster> OrderClusters(
        List<MutableCluster> clusters,
        string? focusId,
        IReadOnlyDictionary<string, int> score)
    {
        if (clusters.Count == 0) return clusters;
        var ordered = new List<MutableCluster>(clusters.Count);
        if (focusId is not null)
        {
            var focusCluster = clusters.FirstOrDefault(cluster =>
                string.Equals(cluster.HubId, focusId, StringComparison.OrdinalIgnoreCase));
            if (focusCluster is not null) ordered.Add(focusCluster);
        }
        foreach (var cluster in clusters
                     .Where(cluster => !ordered.Contains(cluster))
                     .OrderByDescending(cluster => score.GetValueOrDefault(cluster.HubId))
                     .ThenBy(cluster => cluster.HubId, StringComparer.OrdinalIgnoreCase))
            ordered.Add(cluster);
        return ordered;
    }

    // ------------------------------------------------------------------ galaxy constellation

    private static double[,] BuildGalaxyAffinity(
        IReadOnlyList<ClusterLayout> clusters,
        IReadOnlyList<DependencyEdge> edges,
        IReadOnlyDictionary<string, int> owner)
    {
        var affinity = new double[clusters.Count, clusters.Count];
        foreach (var edge in edges)
        {
            if (edge.Type == EdgeType.Incompatible) continue;
            if (!owner.TryGetValue(edge.From, out var from) ||
                !owner.TryGetValue(edge.To, out var to) || from == to) continue;
            var weight = edge.Type switch
            {
                EdgeType.Requires => 3,
                EdgeType.Embedded => 2,
                _ => 1,
            };
            affinity[from, to] += weight;
            affinity[to, from] += weight;
        }
        return affinity;
    }

    private static LayoutPoint[] PlaceGalaxyCenters(
        IReadOnlyList<ClusterLayout> clusters,
        double[,] affinity,
        StarGalaxyOptions opt)
    {
        var count = clusters.Count;
        if (count == 0) return [];
        if (count == 1) return [new LayoutPoint(0, 0)];

        var radii = clusters.Select(cluster => cluster.Radius).ToArray();
        var positions = new LayoutPoint[count];
        var initialRadius = Math.Max(600, radii.Sum() * 0.42);
        for (var i = 0; i < count; i++)
        {
            var angle = -Math.PI / 2 + 2 * Math.PI * i / count;
            positions[i] = new LayoutPoint(Math.Cos(angle) * initialRadius * 1.45,
                Math.Sin(angle) * initialRadius);
        }

        var gap = Math.Max(80, opt.GalaxyGap);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var forceX = new double[count];
            var forceY = new double[count];
            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    var dx = positions[j].X - positions[i].X;
                    var dy = positions[j].Y - positions[i].Y;
                    var distance = Math.Max(0.001, Math.Sqrt(dx * dx + dy * dy));
                    var ux = dx / distance;
                    var uy = dy / distance;

                    var minimum = radii[i] + radii[j] + gap * 0.85;
                    if (distance < minimum)
                    {
                        var push = (minimum - distance) * 0.5;
                        forceX[i] -= ux * push;
                        forceY[i] -= uy * push;
                        forceX[j] += ux * push;
                        forceY[j] += uy * push;
                    }

                    var weight = affinity[i, j];
                    if (weight <= 0) continue;
                    var desired = radii[i] + radii[j] + gap * 1.08;
                    var pull = (distance - desired) * 0.007 *
                               Math.Min(2.2, 0.7 + weight * 0.1);
                    forceX[i] += ux * pull;
                    forceY[i] += uy * pull;
                    forceX[j] -= ux * pull;
                    forceY[j] -= uy * pull;
                }
            }

            for (var i = 0; i < count; i++)
            {
                // Vertical gravity is stronger so the constellation fills a
                // typical 16:9 window instead of collapsing into a square blob.
                forceX[i] += -positions[i].X * 0.0007;
                forceY[i] += -positions[i].Y * 0.0016;
                var stepX = Math.Clamp(forceX[i], -40, 40);
                var stepY = Math.Clamp(forceY[i], -40, 40);
                positions[i] = new LayoutPoint(positions[i].X + stepX, positions[i].Y + stepY);
            }
        }

        // Guarantee the promised clear separation even for dense constellations.
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var moved = false;
            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    var dx = positions[j].X - positions[i].X;
                    var dy = positions[j].Y - positions[i].Y;
                    var distance = Math.Max(0.001, Math.Sqrt(dx * dx + dy * dy));
                    var minimum = radii[i] + radii[j] + gap * 0.85;
                    if (distance >= minimum) continue;
                    var push = (minimum - distance) * 0.52;
                    var ux = dx / distance;
                    var uy = dy / distance;
                    positions[i] = new LayoutPoint(positions[i].X - ux * push,
                        positions[i].Y - uy * push);
                    positions[j] = new LayoutPoint(positions[j].X + ux * push,
                        positions[j].Y + uy * push);
                    moved = true;
                }
            }
            if (!moved) break;
        }

        var minX = positions.Min(point => point.X);
        var maxX = positions.Max(point => point.X);
        var minY = positions.Min(point => point.Y);
        var maxY = positions.Max(point => point.Y);
        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;

        // Desktop windows are wide, but only stretch a composition that came
        // out too square. With many galaxies the force layout is already wide;
        // forcing 1.55x there produced huge side gutters.
        var spanX = Math.Max(1, maxX - minX);
        var spanY = Math.Max(1, maxY - minY);
        var horizontalSpread = Math.Clamp(1.55 * spanY / spanX, 1.0, 1.55);
        for (var i = 0; i < count; i++)
            positions[i] = new LayoutPoint(
                (positions[i].X - centerX) * horizontalSpread,
                positions[i].Y - centerY);
        return positions;
    }

    // ------------------------------------------------------------------ local placement

    private static ClusterLayout BuildLocalPlacement(
        MutableCluster cluster,
        Dictionary<string, List<string>> adjacency,
        IReadOnlyDictionary<string, int> score,
        StarGalaxyOptions opt)
    {
        if (cluster.IsIsolated) return BuildIsolatedGrid(cluster, opt);

        var depths = BfsDepths(cluster.Set, cluster.HubId, adjacency);
        var maxDepth = Math.Max(1, depths.Values.Where(depth => depth > 0).DefaultIfEmpty(1).Max());
        foreach (var id in cluster.NodeIds)
            if (!depths.ContainsKey(id)) depths[id] = maxDepth + 1;

        var rings = cluster.NodeIds.ToDictionary(
            id => id,
            id => Math.Min(depths.GetValueOrDefault(id, maxDepth + 1), opt.MaxDepthRings),
            StringComparer.OrdinalIgnoreCase);

        var angles = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var positions = new Dictionary<string, LayoutPoint>(StringComparer.OrdinalIgnoreCase)
        {
            [cluster.HubId] = new LayoutPoint(0, 0),
        };
        angles[cluster.HubId] = -Math.PI / 2;

        var maxRing = rings.Values.DefaultIfEmpty(0).Max();
        var placedBands = 0;
        var previousRadius = 0.0;

        for (var ring = 1; ring <= maxRing; ring++)
        {
            var nodes = cluster.NodeIds
                .Where(id => rings.GetValueOrDefault(id) == ring &&
                             !string.Equals(id, cluster.HubId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (nodes.Count == 0) continue;

            var ordered = ring == 1
                ? nodes.OrderByDescending(id => score.GetValueOrDefault(id))
                    .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                : nodes.OrderBy(id => ParentAngle(id, ring, adjacency, rings, angles))
                    .ThenBy(id => id, StringComparer.OrdinalIgnoreCase);

            var list = ordered.ToList();

            // Wide dependency layers are folded into concentric sub-rings so a
            // hub with 50 direct children becomes a round spiral band instead of
            // one gigantic, sparse circle.
            var subRingCount = Math.Max(1,
                (int)Math.Ceiling(list.Count / (double)Math.Max(1, opt.SubRingCapacity)));
            var perSubRing = (int)Math.Ceiling(list.Count / (double)subRingCount);
            for (var subRing = 0; subRing < subRingCount; subRing++)
            {
                var chunk = list.Skip(subRing * perSubRing).Take(perSubRing).ToList();
                if (chunk.Count == 0) continue;

                var fit = chunk.Count <= 1
                    ? 0
                    : opt.MinNodeSpacing / (2 * Math.Sin(Math.PI / chunk.Count));
                var baseRadius = opt.BaseRingRadius + placedBands * opt.RingGap;
                var radius = Math.Max(baseRadius, fit);
                if (previousRadius > 0) radius = Math.Max(radius, previousRadius + opt.RingGap);

                var phase = opt.Seed * 0.0001 + cluster.Index * 0.41 + placedBands * 0.17;
                for (var i = 0; i < chunk.Count; i++)
                {
                    var angle = -Math.PI / 2 + 2 * Math.PI * i / chunk.Count + phase;
                    angles[chunk[i]] = angle;
                    positions[chunk[i]] = new LayoutPoint(Math.Cos(angle) * radius,
                        Math.Sin(angle) * radius);
                }
                previousRadius = radius;
                placedBands++;
            }
        }

        RelaxCollisions(cluster, positions, opt);
        // 72px of breathing room covers the labels that extend past the node
        // circles; otherwise neighboring galaxies visually touch their captions.
        var galaxyRadius = positions.Values
            .Select(point => Math.Sqrt(point.X * point.X + point.Y * point.Y))
            .DefaultIfEmpty(0)
            .Max() + opt.NodeRadius + 72;
        return new ClusterLayout(cluster, angles, rings, positions, galaxyRadius);
    }

    private static ClusterLayout BuildIsolatedGrid(MutableCluster cluster, StarGalaxyOptions opt)
    {
        var ids = cluster.NodeIds
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(ids.Count)));
        var rows = Math.Max(1, (int)Math.Ceiling(ids.Count / (double)columns));
        var spacing = opt.MinNodeSpacing;

        var angles = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var rings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var positions = new Dictionary<string, LayoutPoint>(StringComparer.OrdinalIgnoreCase);
        var maxExtent = 0.0;
        for (var i = 0; i < ids.Count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            var x = (column - (columns - 1) / 2.0) * spacing;
            var y = (row - (rows - 1) / 2.0) * spacing;
            positions[ids[i]] = new LayoutPoint(x, y);
            angles[ids[i]] = 0;
            rings[ids[i]] = 1;
            maxExtent = Math.Max(maxExtent, Math.Max(Math.Abs(x), Math.Abs(y)));
        }

        return new ClusterLayout(cluster, angles, rings, positions,
            maxExtent + opt.NodeRadius + 72);
    }

    private sealed record ClusterLayout(
        MutableCluster Cluster,
        Dictionary<string, double> Angles,
        Dictionary<string, int> Rings,
        Dictionary<string, LayoutPoint> Local,
        double Radius);

    private static double ParentAngle(
        string id,
        int ring,
        Dictionary<string, List<string>> adjacency,
        IReadOnlyDictionary<string, int> rings,
        IReadOnlyDictionary<string, double> angles)
    {
        foreach (var neighbor in adjacency[id])
        {
            if (rings.GetValueOrDefault(neighbor) != ring - 1) continue;
            if (angles.TryGetValue(neighbor, out var angle)) return angle;
        }
        return Math.PI * 2; // sorts after known parents
    }

    private static void RelaxCollisions(
        MutableCluster cluster,
        Dictionary<string, LayoutPoint> positions,
        StarGalaxyOptions opt)
    {
        var ordered = cluster.NodeIds
            .OrderBy(id => string.Equals(id, cluster.HubId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var moved = false;
            for (var i = 0; i < ordered.Count; i++)
            {
                for (var j = i + 1; j < ordered.Count; j++)
                {
                    var leftId = ordered[i];
                    var rightId = ordered[j];
                    var left = positions[leftId];
                    var right = positions[rightId];
                    var dx = right.X - left.X;
                    var dy = right.Y - left.Y;
                    var distance = Math.Sqrt(dx * dx + dy * dy);
                    if (distance >= opt.MinNodeSpacing || distance < 0.001) continue;

                    var push = (opt.MinNodeSpacing - distance) / 2;
                    var ux = dx / distance;
                    var uy = dy / distance;
                    if (!string.Equals(leftId, cluster.HubId, StringComparison.OrdinalIgnoreCase))
                    {
                        positions[leftId] = new LayoutPoint(left.X - ux * push, left.Y - uy * push);
                        moved = true;
                    }
                    if (!string.Equals(rightId, cluster.HubId, StringComparison.OrdinalIgnoreCase))
                    {
                        positions[rightId] = new LayoutPoint(right.X + ux * push, right.Y + uy * push);
                        moved = true;
                    }
                }
            }
            if (!moved) break;
        }
    }

    // ------------------------------------------------------------------ labels

    private readonly record struct LabelInfo(bool Visible, LayoutRect Rect);

    private static Dictionary<string, LabelInfo> BuildLabels(
        IReadOnlyList<ModNode> nodes,
        IReadOnlyDictionary<string, LayoutPoint> world,
        IReadOnlyDictionary<string, (int Galaxy, int Ring, bool Hub)> meta,
        IReadOnlyDictionary<string, int> score,
        IReadOnlyDictionary<string, int> degree,
        StarGalaxyOptions opt)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var nodeRects = new Dictionary<string, LayoutRect>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var point = world[node.Id];
            var radius = meta[node.Id].Hub ? opt.HubRadius : opt.NodeRadius;
            nodeRects[node.Id] = LayoutRect.FromCenter(point.X, point.Y,
                radius * 2 + 4, radius * 2 + 4);
        }

        var candidates = nodes
            .Select(node => node.Id)
            .OrderByDescending(id => meta[id].Hub ? 1 : 0)
            .ThenByDescending(id => degree.GetValueOrDefault(id))
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var placed = new List<LayoutRect>();
        var result = new Dictionary<string, LabelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in candidates)
        {
            var node = byId[id];
            var point = world[id];
            var info = meta[id];
            var radius = info.Hub ? opt.HubRadius : opt.NodeRadius;
            var title = DisplayTitle(node);
            var width = EstimateTextWidth(title, info.Hub ? opt.LabelFontSize + 1 : opt.LabelFontSize,
                info.Hub ? 190 : 172);
            var height = opt.LabelHeight;

            var below = LayoutRect.FromCenter(point.X, point.Y + radius + 5 + height / 2, width, height);
            var above = LayoutRect.FromCenter(point.X, point.Y - radius - 5 - height / 2, width, height);
            var right = LayoutRect.FromCenter(point.X + radius + 6 + width / 2, point.Y, width, height);
            var left = LayoutRect.FromCenter(point.X - radius - 6 - width / 2, point.Y, width, height);

            var chosen = default(LayoutRect);
            var visible = false;
            foreach (var candidate in new[] { below, above, right, left })
            {
                var inflated = candidate.Inflate(2);
                var blocked = nodeRects.Any(pair =>
                    !string.Equals(pair.Key, id, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value.Inflate(2).IntersectsWith(inflated));
                if (blocked) continue;
                if (placed.Any(other => other.Inflate(2).IntersectsWith(inflated))) continue;
                chosen = candidate;
                visible = true;
                break;
            }

            if (visible) placed.Add(chosen);
            result[id] = new LabelInfo(visible, chosen);
        }
        return result;
    }

    private static string DisplayTitle(ModNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.NameZh)) return node.NameZh!;
        if (!string.IsNullOrWhiteSpace(node.Name)) return node.Name;
        return node.Id;
    }

    private static double EstimateTextWidth(string text, double fontSize, double maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return Math.Min(maxWidth, 44);
        var width = 0.0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var wide = value >= 0x1100 && (
                (value <= 0x115F) || (value >= 0x2E80 && value <= 0xA4CF) ||
                (value >= 0xAC00 && value <= 0xD7A3) || (value >= 0xF900 && value <= 0xFAFF) ||
                (value >= 0xFE30 && value <= 0xFE4F) || (value >= 0xFF00 && value <= 0xFF60) ||
                (value >= 0xFFE0 && value <= 0xFFE6));
            width += wide ? fontSize : fontSize * 0.56;
        }
        return Math.Clamp(width + 8, 44, maxWidth);
    }

    // ------------------------------------------------------------------ result helpers

    private static LayoutRect ComputeBounds(
        IReadOnlyDictionary<string, LayoutPoint> world,
        IReadOnlyDictionary<string, (int Galaxy, int Ring, bool Hub)> meta,
        IReadOnlyDictionary<string, LabelInfo> labels,
        StarGalaxyOptions opt)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (id, point) in world)
        {
            var radius = meta[id].Hub ? opt.HubRadius : opt.NodeRadius;
            minX = Math.Min(minX, point.X - radius);
            minY = Math.Min(minY, point.Y - radius);
            maxX = Math.Max(maxX, point.X + radius);
            maxY = Math.Max(maxY, point.Y + radius);
            if (labels.TryGetValue(id, out var label) && label.Visible)
            {
                minX = Math.Min(minX, label.Rect.X);
                minY = Math.Min(minY, label.Rect.Y);
                maxX = Math.Max(maxX, label.Rect.Right);
                maxY = Math.Max(maxY, label.Rect.Bottom);
            }
        }
        return minX > maxX ? new LayoutRect() : LayoutRect.FromEdges(minX, minY, maxX, maxY);
    }

    private static List<StarGalaxyCrossLink> BuildCrossLinks(
        IReadOnlyList<DependencyEdge> edges,
        IReadOnlyDictionary<string, ModNode> byId,
        IReadOnlyDictionary<string, int> owner)
    {
        var aggregates = new Dictionary<(int From, int To), (int Count, int Requires)>();
        foreach (var edge in edges)
        {
            if (!byId.ContainsKey(edge.From) || !byId.ContainsKey(edge.To)) continue;
            if (edge.Type == EdgeType.Incompatible) continue;
            if (!owner.TryGetValue(edge.From, out var from) || !owner.TryGetValue(edge.To, out var to)) continue;
            if (from == to) continue;
            var key = from < to ? (from, to) : (to, from);
            aggregates.TryGetValue(key, out var current);
            aggregates[key] = (current.Count + 1,
                current.Requires + (edge.Type == EdgeType.Requires ? 1 : 0));
        }
        return aggregates
            .OrderBy(pair => pair.Key.From)
            .ThenBy(pair => pair.Key.To)
            .Select(pair => new StarGalaxyCrossLink
            {
                FromGalaxy = pair.Key.From,
                ToGalaxy = pair.Key.To,
                Count = pair.Value.Count,
                RequiresCount = pair.Value.Requires,
            })
            .ToList();
    }

    private static int CountInternalEdges(MutableCluster cluster, IReadOnlyList<DependencyEdge> edges)
    {
        var count = 0;
        foreach (var edge in edges)
        {
            if (edge.Type == EdgeType.Incompatible) continue;
            if (string.Equals(edge.From, edge.To, StringComparison.OrdinalIgnoreCase)) continue;
            if (cluster.Set.Contains(edge.From) && cluster.Set.Contains(edge.To)) count++;
        }
        return count;
    }

    private sealed class MutableCluster(int index, string hubId)
    {
        public int Index { get; set; } = index;
        public string HubId { get; set; } = hubId;
        public bool IsIsolated { get; set; }
        public List<string> NodeIds { get; } = [];
        public HashSet<string> Set { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string id)
        {
            if (Set.Add(id)) NodeIds.Add(id);
        }

        public void Remove(string id)
        {
            if (Set.Remove(id)) NodeIds.Remove(id);
        }
    }
}
