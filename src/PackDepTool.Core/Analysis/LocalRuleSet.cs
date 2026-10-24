using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

/// <summary>
/// Normalised, enabled-only rule view used by the builder and analyzer.
/// Higher <see cref="LocalRule.Priority"/> wins; later rules do not override earlier ones.
/// </summary>
public sealed class LocalRuleSet
{
    public static readonly LocalRuleSet Empty = new([]);

    private readonly List<LocalRule> _rules;

    public LocalRuleSet(IEnumerable<LocalRule>? rules)
    {
        _rules = (rules ?? [])
            .Where(rule => rule.Enabled)
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.CreatedAt)
            .Select(rule => rule.Clone())
            .ToList();
    }

    public IReadOnlyList<LocalRule> Rules => _rules;
    public int Count => _rules.Count;
    public bool IsEmpty => _rules.Count == 0;

    public bool IsMissingIgnored(string from, string to) =>
        _rules.Any(rule => rule.Type == LocalRuleType.IgnoreMissing && Pair(rule, from, to));

    public bool IsConflictIgnored(string from, string to) =>
        _rules.Any(rule => rule.Type == LocalRuleType.IgnoreConflict &&
                           (Pair(rule, from, to) || Pair(rule, to, from)));

    public bool IsVersionIgnored(string from, string to) =>
        _rules.Any(rule => rule.Type == LocalRuleType.IgnoreVersionMismatch &&
                           (Pair(rule, from, to) || Pair(rule, to, from)));

    public string? AliasProvider(string id)
    {
        var match = _rules.FirstOrDefault(rule =>
            rule.Type == LocalRuleType.ProvideAlias &&
            string.Equals(rule.From, id, StringComparison.OrdinalIgnoreCase));
        return match?.To;
    }

    public string? OverrideRange(string from, string to)
    {
        var match = _rules.FirstOrDefault(rule =>
            rule.Type == LocalRuleType.OverrideVersionRange &&
            Pair(rule, from, to) &&
            !string.IsNullOrWhiteSpace(rule.Value));
        return match?.Value;
    }

    public bool IsExcluded(string id) =>
        _rules.Any(rule => rule.Type == LocalRuleType.ExcludeMod &&
                           string.Equals(rule.From, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<LocalRule> Exclusions =>
        _rules.Where(rule => rule.Type == LocalRuleType.ExcludeMod).ToList();

    private static bool Pair(LocalRule rule, string from, string to) =>
        Matches(rule.From, from) && Matches(rule.To, to);

    private static bool Matches(string? pattern, string value) =>
        string.IsNullOrWhiteSpace(pattern) ||
        pattern == "*" ||
        string.Equals(pattern.Trim(), value, StringComparison.OrdinalIgnoreCase);
}
