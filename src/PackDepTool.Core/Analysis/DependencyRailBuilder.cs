using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public enum RailRelationMode
{
    Required,
    RequiredOptional,
    AllRelations,
}

/// <summary>
/// A dependency-source line group used by the mod-list rail. One group is one
/// parent mod (the dependency source) and all of its visible dependents.
/// </summary>
public sealed class DependencyRailGroup
{
    public required string ParentId { get; init; }
    public required int ParentIndex { get; init; }
    public required IReadOnlyList<int> ChildIndices { get; init; }
    public required int MinIndex { get; init; }
    public required int MaxIndex { get; init; }
    public required bool IsConflict { get; init; }
    public int LogicalLane { get; set; }
    public int VisualLane { get; set; }
}

/// <summary>
/// Groups dependency edges by their source mod and assigns non-overlapping lanes.
/// UI code only needs to draw the returned groups; no Avalonia types are involved.
/// </summary>
public static class DependencyRailBuilder
{
    public const int VisualLaneCount = 4;

    public static IReadOnlyList<DependencyRailGroup> Build(
        IReadOnlyList<string> orderedIds,
        IEnumerable<DependencyEdge> edges,
        RailRelationMode mode)
    {
        if (orderedIds.Count < 2) return [];

        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < orderedIds.Count; index++)
            indexById.TryAdd(orderedIds[index], index);

        var groups = new Dictionary<string, GroupBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges)
        {
            if (!Include(edge.Type, mode)) continue;

            var embedded = edge.Type == EdgeType.Embedded;
            var parentId = embedded ? edge.From : edge.To;
            var childId = embedded ? edge.To : edge.From;
            if (string.Equals(parentId, childId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!indexById.TryGetValue(parentId, out var parentIndex) ||
                !indexById.TryGetValue(childId, out var childIndex))
                continue;

            var isConflict = edge.Type == EdgeType.Incompatible;
            var key = parentId + (isConflict ? "\0conflict" : "\0dependency");
            if (!groups.TryGetValue(key, out var group))
            {
                group = new GroupBuilder(parentId, parentIndex, isConflict);
                groups[key] = group;
            }
            group.ChildIndices.Add(childIndex);
        }

        var result = groups.Values
            .Where(group => group.ChildIndices.Count > 0)
            .Select(group =>
            {
                group.ChildIndices.Remove(group.ParentIndex);
                var min = Math.Min(group.ParentIndex, group.ChildIndices.Min());
                var max = Math.Max(group.ParentIndex, group.ChildIndices.Max());
                return new DependencyRailGroup
                {
                    ParentId = group.ParentId,
                    ParentIndex = group.ParentIndex,
                    ChildIndices = group.ChildIndices.OrderBy(index => index).ToArray(),
                    MinIndex = min,
                    MaxIndex = max,
                    IsConflict = group.IsConflict,
                };
            })
            .Where(group => group.ChildIndices.Count > 0)
            .OrderBy(group => group.MinIndex)
            .ThenByDescending(group => group.MaxIndex - group.MinIndex)
            .ToList();

        AssignLanes(result);
        return result;
    }

    public static bool Include(EdgeType type, RailRelationMode mode)
    {
        // A declared conflict is always visible and is drawn in red; relation
        // mode only controls dependency-like edges.
        if (type == EdgeType.Incompatible) return true;

        return mode switch
        {
            RailRelationMode.Required => type == EdgeType.Requires,
            RailRelationMode.RequiredOptional =>
                type is EdgeType.Requires or EdgeType.Optional or EdgeType.Recommends,
            _ => true,
        };
    }

    private static void AssignLanes(IReadOnlyList<DependencyRailGroup> groups)
    {
        var laneEnds = new List<int>();
        foreach (var group in groups)
        {
            var lane = -1;
            for (var index = 0; index < laneEnds.Count; index++)
            {
                if (laneEnds[index] < group.MinIndex)
                {
                    lane = index;
                    break;
                }
            }

            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(group.MaxIndex);
            }
            else
            {
                laneEnds[lane] = group.MaxIndex;
            }

            group.LogicalLane = lane;
            group.VisualLane = lane % VisualLaneCount;
        }
    }

    private sealed class GroupBuilder(string parentId, int parentIndex, bool isConflict)
    {
        public string ParentId { get; } = parentId;
        public int ParentIndex { get; } = parentIndex;
        public bool IsConflict { get; } = isConflict;
        public HashSet<int> ChildIndices { get; } = [];
    }
}
