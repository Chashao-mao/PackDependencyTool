namespace PackDepTool.Core.Analysis;

/// <summary>One selectable version of a mod and the constraints it adds.</summary>
public sealed record SolverVersion(
    string ModId,
    string Version,
    IReadOnlyList<SolverConstraint> Requires,
    IReadOnlyList<string>? IncompatibleWith = null)
{
    public string Display => $"{ModId} {Version}";
}

public sealed record SolverConstraint(
    string ModId,
    string VersionRange,
    bool Optional = false,
    string? RequiredBy = null);

public sealed class VersionSolverRequest
{
    public IReadOnlyDictionary<string, IReadOnlyList<SolverVersion>> Candidates { get; init; } =
        new Dictionary<string, IReadOnlyList<SolverVersion>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Force these mods to a specific version when possible.</summary>
    public IReadOnlyDictionary<string, string> Pinned { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Versions currently installed; used as a soft preference.</summary>
    public IReadOnlyDictionary<string, string> Preferred { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SolverConstraint> Roots { get; init; } = [];
    public int MaxSteps { get; init; } = 100_000;
}

public sealed class VersionSolverResult
{
    public bool Solved { get; init; }
    public IReadOnlyDictionary<string, SolverVersion> Selected { get; init; } =
        new Dictionary<string, SolverVersion>(StringComparer.OrdinalIgnoreCase);
    public string Explanation { get; init; } = string.Empty;
    public int Steps { get; init; }
}

/// <summary>
/// Phase 2 of the self-made version solver: a small backtracking solver over
/// candidate versions with forward checking. It deliberately does not fetch
/// metadata; callers supply candidates from installed jars or Modrinth/CurseForge.
/// </summary>
public static class VersionCandidateSolver
{
    public static VersionSolverResult Solve(
        VersionSolverRequest request,
        CancellationToken cancellationToken = default)
    {
        var state = new State(
            request.Candidates.ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Where(version => !string.IsNullOrWhiteSpace(version.Version))
                    .OrderByDescending(version => version.Version, VersionComparer.Instance)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(request.Pinned, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(request.Preferred, StringComparer.OrdinalIgnoreCase));

        foreach (var root in request.Roots)
            state.AddConstraint(root);

        var failure = string.Empty;
        var steps = 0;
        var solved = Search(state, ref failure, ref steps, request.MaxSteps, cancellationToken);
        return new VersionSolverResult
        {
            Solved = solved,
            Selected = state.Assigned,
            Explanation = solved ? "已找到满足全部约束的版本组合。" : failure,
            Steps = steps,
        };
    }

    private static bool Search(
        State state,
        ref string failure,
        ref int steps,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++steps > maxSteps)
        {
            failure = "求解步数超过上限，已停止；请减少候选版本或拆小依赖图。";
            return false;
        }

        // Optional constraints on absent mods are simply skipped.
        foreach (var (modId, list) in state.Constraints.ToList())
        {
            if (state.Assigned.ContainsKey(modId) || list.Any(item => !item.Optional)) continue;
            if (state.ViableCandidates(modId).Count == 0)
            {
                state.Constraints.Remove(modId);
                foreach (var item in list)
                    state.DropConstraint(item);
            }
        }

        var pending = state.Constraints.Keys
            .Where(modId => !state.Assigned.ContainsKey(modId))
            .ToList();
        if (pending.Count == 0) return true;

        var variable = pending
            .Select(modId => (ModId: modId, Options: state.ViableCandidates(modId)))
            .OrderBy(item => item.Options.Count)
            .ThenBy(item => item.ModId, StringComparer.OrdinalIgnoreCase)
            .First();
        if (variable.Options.Count == 0)
        {
            failure = state.ExplainNoCandidate(variable.ModId);
            return false;
        }

        foreach (var candidate in variable.Options)
        {
            var clone = state.Clone();
            var conflict = clone.Assign(candidate, out var reason);
            if (conflict) continue;
            if (Search(clone, ref failure, ref steps, maxSteps, cancellationToken))
            {
                state.CopyFrom(clone);
                return true;
            }
            if (string.IsNullOrWhiteSpace(failure))
                failure = reason;
        }

        if (string.IsNullOrWhiteSpace(failure))
            failure = $"{variable.ModId} 的所有候选版本都会产生新冲突。";
        return false;
    }

    private sealed class State
    {
        private readonly Dictionary<string, List<SolverVersion>> _candidates;
        private readonly Dictionary<string, string> _pinned;
        private readonly Dictionary<string, string> _preferred;

        public State(
            Dictionary<string, List<SolverVersion>> candidates,
            Dictionary<string, string> pinned,
            Dictionary<string, string> preferred)
        {
            _candidates = candidates;
            _pinned = pinned;
            _preferred = preferred;
            Assigned = new Dictionary<string, SolverVersion>(StringComparer.OrdinalIgnoreCase);
            Constraints = new Dictionary<string, List<SolverConstraint>>(StringComparer.OrdinalIgnoreCase);
            Banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, SolverVersion> Assigned { get; }
        public Dictionary<string, List<SolverConstraint>> Constraints { get; }
        public HashSet<string> Banned { get; }

        public void AddConstraint(SolverConstraint constraint)
        {
            if (!Constraints.TryGetValue(constraint.ModId, out var list))
                Constraints[constraint.ModId] = list = [];
            if (!list.Any(item => item.VersionRange == constraint.VersionRange &&
                                  item.RequiredBy == constraint.RequiredBy))
                list.Add(constraint);
        }

        public void DropConstraint(SolverConstraint constraint)
        {
            if (!Constraints.TryGetValue(constraint.ModId, out var list)) return;
            list.Remove(constraint);
            if (list.Count == 0) Constraints.Remove(constraint.ModId);
        }

        public List<SolverVersion> ViableCandidates(string modId)
        {
            if (Banned.Contains(modId) || !_candidates.TryGetValue(modId, out var all))
                return [];

            var constraints = Constraints.GetValueOrDefault(modId) ?? [];
            var pinned = _pinned.GetValueOrDefault(modId);
            return all
                .Where(candidate => pinned is null ||
                                    string.Equals(candidate.Version, pinned, StringComparison.OrdinalIgnoreCase))
                .Where(candidate => constraints.All(constraint =>
                    VersionRange.Satisfies(constraint.VersionRange, candidate.Version) != false))
                .OrderByDescending(candidate => IsPreferred(candidate))
                .ThenByDescending(candidate => candidate.Version, VersionComparer.Instance)
                .ToList();
        }

        private bool IsPreferred(SolverVersion candidate) =>
            _preferred.TryGetValue(candidate.ModId, out var preferred) &&
            string.Equals(candidate.Version, preferred, StringComparison.OrdinalIgnoreCase);

        public bool Assign(SolverVersion candidate, out string reason)
        {
            reason = string.Empty;
            if (Assigned.TryGetValue(candidate.ModId, out var existing))
            {
                if (string.Equals(existing.Version, candidate.Version, StringComparison.OrdinalIgnoreCase))
                    return false;
                reason = $"{candidate.ModId} 同时被要求为 {existing.Version} 和 {candidate.Version}。";
                return true;
            }

            Assigned[candidate.ModId] = candidate;
            foreach (var other in candidate.IncompatibleWith ?? [])
            {
                Banned.Add(other);
                if (Assigned.ContainsKey(other))
                {
                    reason = $"{candidate.Display} 声明与 {other} 不兼容。";
                    return true;
                }
            }

            foreach (var requirement in candidate.Requires)
            {
                AddConstraint(requirement with { RequiredBy = candidate.Display });
                if (Assigned.TryGetValue(requirement.ModId, out var assigned) &&
                    VersionRange.Satisfies(requirement.VersionRange, assigned.Version) == false)
                {
                    reason = $"{candidate.Display} 要求 {requirement.ModId} {requirement.VersionRange}，" +
                             $"但已选择 {assigned.Version}。";
                    return true;
                }
            }

            return false;
        }

        public string ExplainNoCandidate(string modId)
        {
            if (Banned.Contains(modId))
                return $"{modId} 被其他模组声明为不兼容，没有可选版本。";
            if (!_candidates.TryGetValue(modId, out var all) || all.Count == 0)
                return $"没有找到 {modId} 的任何候选版本。";

            var constraints = Constraints.GetValueOrDefault(modId) ?? [];
            if (constraints.Count == 0)
                return $"{modId} 没有可用候选版本。";

            var details = constraints.Select(item =>
                $"{item.RequiredBy ?? "根要求"} 要求 {item.VersionRange}" +
                (item.Optional ? "（可选）" : ""));
            return $"{modId} 没有满足以下约束的版本：{string.Join("；", details)}。";
        }

        public void CopyFrom(State other)
        {
            Assigned.Clear();
            foreach (var (key, value) in other.Assigned) Assigned[key] = value;
            Constraints.Clear();
            foreach (var (key, value) in other.Constraints) Constraints[key] = [.. value];
            Banned.Clear();
            foreach (var item in other.Banned) Banned.Add(item);
        }

        public State Clone()
        {
            var clone = new State(_candidates, _pinned, _preferred);
            clone.CopyFrom(this);
            return clone;
        }
    }

    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (string.IsNullOrWhiteSpace(x)) return string.IsNullOrWhiteSpace(y) ? 0 : -1;
            if (string.IsNullOrWhiteSpace(y)) return 1;
            var a = x.Split('-', 2);
            var b = y.Split('-', 2);
            var numeric = CompareParts(a[0], b[0]);
            if (numeric != 0) return numeric;
            // A release is newer than its prerelease.
            if (a.Length == 1 && b.Length > 1) return 1;
            if (a.Length > 1 && b.Length == 1) return -1;
            return string.Compare(a.ElementAtOrDefault(1), b.ElementAtOrDefault(1),
                StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareParts(string a, string b)
        {
            var left = a.Split('.');
            var right = b.Split('.');
            var length = Math.Max(left.Length, right.Length);
            for (var i = 0; i < length; i++)
            {
                var l = i < left.Length && int.TryParse(left[i], out var lv) ? lv : 0;
                var r = i < right.Length && int.TryParse(right[i], out var rv) ? rv : 0;
                if (l != r) return l.CompareTo(r);
            }
            return 0;
        }
    }
}
