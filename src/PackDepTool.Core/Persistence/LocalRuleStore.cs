using PackDepTool.Core.Models;

namespace PackDepTool.Core.Persistence;

/// <summary>Per-pack local rule persistence. Same atomic JSON strategy as manual links.</summary>
public sealed class LocalRuleStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalRuleStore(AppPaths paths) => _paths = paths;

    public string GetStorePath(string packRoot) =>
        Path.Combine(_paths.RulesDirectory, $"{ManualLinkStore.PackKey(packRoot)}.json");

    public Task<List<LocalRule>> LoadAsync(string packRoot, CancellationToken cancellationToken = default)
    {
        var text = AtomicFile.TryReadText(GetStorePath(packRoot));
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(new List<LocalRule>());

        try
        {
            var file = JsonDefaults.Deserialize<LocalRuleFile>(text);
            return Task.FromResult(Normalize(file?.Rules ?? []));
        }
        catch (Exception)
        {
            return Task.FromResult(new List<LocalRule>());
        }
    }

    public async Task SaveAsync(
        string packRoot,
        IEnumerable<LocalRule> rules,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            var file = new LocalRuleFile
            {
                Version = 1,
                PackPath = packRoot,
                UpdatedAt = DateTimeOffset.UtcNow,
                Rules = Normalize(rules),
            };

            await AtomicFile.WriteTextAsync(
                    GetStorePath(packRoot), JsonDefaults.Serialize(file), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExportAsync(
        string exportPath,
        string packRoot,
        IEnumerable<LocalRule> rules,
        CancellationToken cancellationToken = default)
    {
        var file = new LocalRuleFile
        {
            Version = 1,
            PackPath = packRoot,
            UpdatedAt = DateTimeOffset.UtcNow,
            Rules = Normalize(rules),
        };

        await AtomicFile.WriteTextAsync(exportPath, JsonDefaults.Serialize(file), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LocalRuleImportResult> ImportAsync(
        string importPath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(importPath, cancellationToken).ConfigureAwait(false);
        var result = new LocalRuleImportResult();

        List<LocalRule>? rules = null;
        try
        {
            rules = JsonDefaults.Deserialize<LocalRuleFile>(text)?.Rules;
        }
        catch (Exception)
        {
            // Try a bare array.
        }

        if (rules is null)
        {
            try
            {
                rules = JsonDefaults.Deserialize<List<LocalRule>>(text);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"无法解析 JSON：{ex.Message}");
                return result;
            }
        }

        if (rules is null)
        {
            result.Errors.Add("文件中没有 rules 数组。");
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                result.Errors.Add("存在 null 规则项。");
                continue;
            }

            rule.From = NormalizeId(rule.From);
            rule.To = NormalizeId(rule.To);

            if (string.IsNullOrWhiteSpace(rule.Id) || !seen.Add(rule.Id))
            {
                rule.Id = Guid.NewGuid().ToString("N");
                seen.Add(rule.Id);
            }

            var error = Validate(rule);
            if (error is not null)
            {
                result.Errors.Add($"{LocalRuleText.TypeName(rule.Type)}：{error}");
                continue;
            }

            rule.UpdatedAt = DateTimeOffset.UtcNow;
            if (rule.CreatedAt == default) rule.CreatedAt = rule.UpdatedAt;
            result.Rules.Add(rule);
        }

        return result;
    }

    public static string? Validate(LocalRule rule)
    {
        switch (rule.Type)
        {
            case LocalRuleType.ExcludeMod:
                return string.IsNullOrWhiteSpace(rule.From) ? "必须填写要排除的 modId。" : null;
            case LocalRuleType.IgnoreMissing:
                return string.IsNullOrWhiteSpace(rule.To) ? "必须填写缺失的 modId。" : null;
            case LocalRuleType.ProvideAlias:
                if (string.IsNullOrWhiteSpace(rule.From) || string.IsNullOrWhiteSpace(rule.To))
                    return "别名与提供者 modId 都不能为空。";
                return string.Equals(rule.From, rule.To, StringComparison.OrdinalIgnoreCase)
                    ? "别名与提供者不能相同。"
                    : null;
            case LocalRuleType.OverrideVersionRange:
                if (string.IsNullOrWhiteSpace(rule.From) || string.IsNullOrWhiteSpace(rule.To))
                    return "From 与 To 都不能为空。";
                return string.IsNullOrWhiteSpace(rule.Value) ? "必须填写新的版本范围。" : null;
            default:
                if (string.IsNullOrWhiteSpace(rule.From) || string.IsNullOrWhiteSpace(rule.To))
                    return "From 与 To 都不能为空。";
                return string.Equals(rule.From, rule.To, StringComparison.OrdinalIgnoreCase)
                    ? "From 与 To 不能相同。"
                    : null;
        }
    }

    private static List<LocalRule> Normalize(IEnumerable<LocalRule> rules)
    {
        var result = new List<LocalRule>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (rule is null) continue;
            rule.From = NormalizeId(rule.From);
            rule.To = NormalizeId(rule.To);
            if (rule.Type == LocalRuleType.ExcludeMod)
                rule.To = string.Empty; // not used for exclusions

            if (string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id))
            {
                rule.Id = Guid.NewGuid().ToString("N");
                ids.Add(rule.Id);
            }

            if (Validate(rule) is not null) continue;
            result.Add(rule.Clone());
        }

        return result;
    }

    private static string NormalizeId(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
