using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public sealed class BisectionGroup
{
    public List<ModNode> Candidates { get; init; } = [];
    public List<ModNode> RequiredDependencies { get; init; } = [];
    public int Step { get; init; }
    public bool Solved => Candidates.Count == 1;
    public string Instruction => Solved
        ? $"定位到 {Candidates[0].Name}（{Candidates[0].Id}）"
        : $"测试组 {Candidates.Count} 个模组，同时保留 {RequiredDependencies.Count} 个必需依赖。";
}

/// <summary>
/// Plans dependency-aware binary search steps without touching any files.
/// The user enables the test group plus required dependencies, launches the game,
/// and reports whether it crashed. Required dependencies are never treated as
/// candidates, so a missing prerequisite is not mistaken for the culprit.
/// </summary>
public sealed class BisectionPlanner
{
    private readonly GraphDocument _graph;
    private readonly List<ModNode> _allCandidates;
    private List<ModNode> _remaining;
    private int _step;

    public BisectionPlanner(GraphDocument graph)
    {
        _graph = graph;
        _allCandidates = graph.Nodes
            .Where(node => !node.IsSynthetic && !node.IsEmbedded && !string.IsNullOrWhiteSpace(node.FilePath))
            .OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _remaining = _allCandidates.ToList();
        Current = BuildStep();
    }

    public BisectionGroup Current { get; private set; }

    public void ReportCrashed()
    {
        if (Current.Solved) return;
        _remaining = Current.Candidates.ToList();
        _step++;
        Current = BuildStep();
    }

    public void ReportWorks()
    {
        if (Current.Solved) return;
        var tested = Current.Candidates.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _remaining = _remaining.Where(node => !tested.Contains(node.Id)).ToList();
        _step++;
        Current = BuildStep();
    }

    public void Reset()
    {
        _remaining = _allCandidates.ToList();
        _step = 0;
        Current = BuildStep();
    }

    private BisectionGroup BuildStep()
    {
        if (_remaining.Count == 0)
            return new BisectionGroup { Step = _step };

        var take = Math.Max(1, (_remaining.Count + 1) / 2);
        var test = _remaining.Take(take).ToList();
        var testIds = test.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var support = RequiredClosure(test, testIds);
        return new BisectionGroup
        {
            Candidates = test,
            RequiredDependencies = support,
            Step = _step,
        };
    }

    private List<ModNode> RequiredClosure(IEnumerable<ModNode> start, ISet<string> candidateIds)
    {
        var support = new Dictionary<string, ModNode>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ModNode>(start);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var edge in _graph.Edges.Where(edge =>
                         edge.Type == EdgeType.Requires &&
                         string.Equals(edge.From, node.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var dependency = _graph.FindNode(edge.To);
                if (dependency is null || dependency.IsSynthetic) continue;
                if (candidateIds.Contains(dependency.Id)) continue;
                if (support.ContainsKey(dependency.Id)) continue;
                support[dependency.Id] = dependency;
                queue.Enqueue(dependency);
            }
        }
        return support.Values.OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
