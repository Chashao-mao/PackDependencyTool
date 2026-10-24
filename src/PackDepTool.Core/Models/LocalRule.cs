using System.Text.Json.Serialization;

namespace PackDepTool.Core.Models;

/// <summary>Local analysis rules, modelled after RimCrow's user rule workflow.</summary>
public enum LocalRuleType
{
    [JsonStringEnumMemberName("ignoreMissing")] IgnoreMissing,
    [JsonStringEnumMemberName("ignoreConflict")] IgnoreConflict,
    [JsonStringEnumMemberName("ignoreVersionMismatch")] IgnoreVersionMismatch,
    [JsonStringEnumMemberName("provideAlias")] ProvideAlias,
    [JsonStringEnumMemberName("overrideVersionRange")] OverrideVersionRange,
    [JsonStringEnumMemberName("excludeMod")] ExcludeMod,
}

/// <summary>
/// A single persisted user rule. Only affects analysis; it never edits the jars.
/// Semantics depend on <see cref="Type"/>:
/// ignoreMissing: To = missing mod (From optional, "*" = any dependent)
/// ignoreConflict / ignoreVersionMismatch: From + To pair (either direction)
/// provideAlias: From = missing/alias id, To = installed provider mod
/// overrideVersionRange: From + To pair, Value = new range
/// excludeMod: From = mod id to hide from the graph
/// </summary>
public sealed class LocalRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public LocalRuleType Type { get; set; } = LocalRuleType.IgnoreMissing;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? Note { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public LocalRule Clone() => (LocalRule)MemberwiseClone();
}

public sealed class LocalRuleFile
{
    public int Version { get; set; } = 1;
    public string? PackPath { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<LocalRule> Rules { get; set; } = [];
}

public sealed class LocalRuleImportResult
{
    public List<LocalRule> Rules { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public static class LocalRuleText
{
    public static string TypeName(LocalRuleType type) => type switch
    {
        LocalRuleType.IgnoreMissing => "忽略缺失依赖",
        LocalRuleType.IgnoreConflict => "忽略冲突",
        LocalRuleType.IgnoreVersionMismatch => "忽略版本不符",
        LocalRuleType.ProvideAlias => "别名提供",
        LocalRuleType.OverrideVersionRange => "覆盖版本范围",
        LocalRuleType.ExcludeMod => "排除模组",
        _ => type.ToString(),
    };

    public static string Describe(LocalRule rule)
    {
        var from = string.IsNullOrWhiteSpace(rule.From) || rule.From == "*" ? "任意" : rule.From;
        return rule.Type switch
        {
            LocalRuleType.IgnoreMissing => $"{from} → {rule.To}（缺失可忽略）",
            LocalRuleType.IgnoreConflict => $"{rule.From} ↔ {rule.To}（冲突可忽略）",
            LocalRuleType.IgnoreVersionMismatch => $"{rule.From} → {rule.To}（版本可忽略）",
            LocalRuleType.ProvideAlias => $"{rule.From} 由 {rule.To} 提供",
            LocalRuleType.OverrideVersionRange => $"{rule.From} → {rule.To} 使用 {rule.Value}",
            LocalRuleType.ExcludeMod => $"排除 {rule.From}",
            _ => rule.Type.ToString(),
        };
    }
}
