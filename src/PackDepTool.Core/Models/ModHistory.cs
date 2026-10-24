namespace PackDepTool.Core.Models;

public enum ModHistoryChangeKind
{
    Added,
    Removed,
    VersionChanged,
    Disabled,
    Enabled,
    StatusChanged,
}

public sealed class ModHistoryChange
{
    public ModHistoryChangeKind Kind { get; set; }
    public string ModId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class ModHistoryMod
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    public string Label => $"{Name} ({Id}) · {Version}";
}

public sealed class ModHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Reason { get; set; } = "扫描";
    public int TotalMods { get; set; }
    public int DisabledMods { get; set; }
    public List<ModHistoryChange> Changes { get; set; } = [];
    public Dictionary<string, ModHistoryMod> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DisabledFiles { get; set; } = [];

    public bool HasChanges => Changes.Count > 0;

    public string Summary
    {
        get
        {
            if (Changes.Count == 0) return $"首次记录 · {TotalMods} 个模组";
            var added = Changes.Count(change => change.Kind == ModHistoryChangeKind.Added);
            var removed = Changes.Count(change => change.Kind == ModHistoryChangeKind.Removed);
            var changed = Changes.Count(change => change.Kind == ModHistoryChangeKind.VersionChanged);
            var disabled = Changes.Count(change => change.Kind is ModHistoryChangeKind.Disabled or ModHistoryChangeKind.Enabled);
            var parts = new List<string>();
            if (added > 0) parts.Add($"新增 {added}");
            if (removed > 0) parts.Add($"移除 {removed}");
            if (changed > 0) parts.Add($"版本 {changed}");
            if (disabled > 0) parts.Add($"禁用/启用 {disabled}");
            return parts.Count == 0 ? $"记录 · {TotalMods} 个模组" : string.Join(" · ", parts);
        }
    }
}

public sealed class ModHistoryFile
{
    public int Version { get; set; } = 1;
    public string PackPath { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ModHistoryEntry> Entries { get; set; } = [];
}
