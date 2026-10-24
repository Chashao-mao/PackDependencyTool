using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;

namespace PackDepTool.Core.History;

/// <summary>
/// Records a diff of the installed mod set after each scan (RimCrow-style history).
/// Rules/manual-link changes do not create history entries; only actual scans do.
/// </summary>
public sealed class ModHistoryRecorder
{
    private readonly ModHistoryStore _store;

    public ModHistoryRecorder(ModHistoryStore store) => _store = store;

    public async Task<ModHistoryFile> RecordAsync(
        ScanResult scan,
        GraphDocument graph,
        string reason = "扫描",
        CancellationToken cancellationToken = default)
    {
        var file = await _store.LoadAsync(scan.RootPath, cancellationToken).ConfigureAwait(false);
        var current = new Dictionary<string, ModHistoryMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            if (node.IsSynthetic || string.IsNullOrWhiteSpace(node.FilePath)) continue;
            current[node.Id] = new ModHistoryMod
            {
                Id = node.Id,
                Name = node.Name,
                Version = node.DisplayVersion,
                FilePath = node.FilePath,
                Status = node.Status.ToString(),
            };
        }

        var disabled = ListDisabledFiles(scan.ModsDirectory);
        var previous = file.Entries.LastOrDefault();
        var changes = new List<ModHistoryChange>();

        if (previous is not null)
        {
            foreach (var (id, mod) in current)
            {
                if (!previous.Mods.TryGetValue(id, out var old))
                {
                    changes.Add(new ModHistoryChange
                    {
                        Kind = ModHistoryChangeKind.Added,
                        ModId = id,
                        Name = mod.Name,
                        Detail = $"{mod.Name} {mod.Version}",
                    });
                }
                else if (!string.Equals(old.Version, mod.Version, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(new ModHistoryChange
                    {
                        Kind = ModHistoryChangeKind.VersionChanged,
                        ModId = id,
                        Name = mod.Name,
                        Detail = $"{mod.Name}: {old.Version} → {mod.Version}",
                    });
                }
            }

            foreach (var (id, old) in previous.Mods)
            {
                if (current.ContainsKey(id)) continue;
                changes.Add(new ModHistoryChange
                {
                    Kind = ModHistoryChangeKind.Removed,
                    ModId = id,
                    Name = old.Name,
                    Detail = $"{old.Name} {old.Version}",
                });
            }

            foreach (var disabledFile in disabled)
            {
                if (!previous.DisabledFiles.Contains(disabledFile, StringComparer.OrdinalIgnoreCase))
                    changes.Add(new ModHistoryChange
                    {
                        Kind = ModHistoryChangeKind.Disabled,
                        ModId = disabledFile,
                        Name = disabledFile,
                        Detail = $"已禁用 {disabledFile}",
                    });
            }

            foreach (var oldDisabled in previous.DisabledFiles)
            {
                if (!disabled.Contains(oldDisabled, StringComparer.OrdinalIgnoreCase))
                    changes.Add(new ModHistoryChange
                    {
                        Kind = ModHistoryChangeKind.Enabled,
                        ModId = oldDisabled,
                        Name = oldDisabled,
                        Detail = $"已启用 {oldDisabled}",
                    });
            }
        }

        if (previous is not null && changes.Count == 0)
            return file;

        var entry = new ModHistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Reason = previous is null ? "首次扫描" : reason,
            TotalMods = current.Count,
            DisabledMods = disabled.Count,
            Changes = changes,
            Mods = current,
            DisabledFiles = disabled,
        };
        file.Entries.Add(entry);
        await _store.SaveAsync(scan.RootPath, file, cancellationToken).ConfigureAwait(false);
        return file;
    }

    private static List<string> ListDisabledFiles(string modsDirectory)
    {
        try
        {
            if (!Directory.Exists(modsDirectory)) return [];
            return Directory.EnumerateFiles(modsDirectory, "*.jar.disabled", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
