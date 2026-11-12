using PackDepTool.Core.Persistence;

namespace PackDepTool.Core.Analysis;

public sealed class ModListRestoreResult
{
    public int Restored { get; init; }
    public int Removed { get; init; }
    public List<string> Missing { get; init; } = [];
    public List<ModListSnapshotEntry> MissingEntries { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public string? TrashDirectory { get; init; }

    public string Summary => $"恢复 {Restored} 个，移除 {Removed} 个" +
                             (Missing.Count > 0 ? $"，{Missing.Count} 个找不到旧文件" : string.Empty);
}

/// <summary>
/// Best-effort restore of a mod-list history snapshot. It never downloads:
/// old JARs are looked up by SHA1 in the current mods folder, .packdep-trash
/// and repair-backup files. Anything missing is reported, not guessed.
/// </summary>
public sealed class ModListRestoreService
{
    public Task<ModListRestoreResult> RestoreAsync(
        string packRoot, ModListSnapshot snapshot, CancellationToken cancellationToken = default) =>
        Task.Run(() => Restore(packRoot, snapshot, cancellationToken), cancellationToken);

    private static ModListRestoreResult Restore(
        string packRoot, ModListSnapshot snapshot, CancellationToken cancellationToken)
    {
        var modsDirectory = Path.Combine(packRoot, "mods");
        Directory.CreateDirectory(modsDirectory);
        var trash = Path.Combine(packRoot, ".packdep-trash",
            $"history-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(trash);

        var pool = BuildPool(packRoot);
        var desired = snapshot.Mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.FilePath) &&
                          mod.FilePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(mod => Path.GetFileName(mod.FilePath!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var restored = 0;
        var removed = 0;
        var missing = new List<string>();
        var missingEntries = new List<ModListSnapshotEntry>();
        var warnings = new List<string>();
        var desiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in desired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = mod.FilePath!.Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(packRoot, relative));
            desiredPaths.Add(destination);
            if (File.Exists(destination) &&
                (string.IsNullOrWhiteSpace(mod.Sha1) ||
                 string.Equals(Sha1Of(destination), mod.Sha1, StringComparison.OrdinalIgnoreCase)))
                continue;

            var source = FindSource(pool, mod);
            if (source is null)
            {
                missing.Add($"{mod.Name}（{mod.Id} {mod.Version}）");
                missingEntries.Add(mod);
                continue;
            }

            try
            {
                if (File.Exists(destination))
                    MoveToTrash(destination, trash);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{mod.Name}：{ex.Message}");
            }
        }

        foreach (var file in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(file);
            if (desiredPaths.Contains(full)) continue;
            try
            {
                MoveToTrash(full, trash);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }

        return new ModListRestoreResult
        {
            Restored = restored,
            Removed = removed,
            Missing = missing,
            MissingEntries = missingEntries,
            Warnings = warnings,
            TrashDirectory = trash,
        };
    }

    private static Dictionary<string, string> BuildPool(string packRoot)
    {
        var pool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDirectory(pool, Path.Combine(packRoot, "mods"));
        AddDirectory(pool, Path.Combine(packRoot, ".packdep-trash"));
        AddDirectory(pool, Path.Combine(packRoot, ".packdep-backups"));
        return pool;
    }

    private static void AddDirectory(Dictionary<string, string> pool, string directory)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.jar", SearchOption.AllDirectories))
                pool.TryAdd(Sha1Of(file), file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partially readable backup/trash directory still contributes what it can.
        }
    }

    private static string? FindSource(IReadOnlyDictionary<string, string> pool, ModListSnapshotEntry mod)
    {
        if (!string.IsNullOrWhiteSpace(mod.Sha1) && pool.TryGetValue(mod.Sha1, out var byHash))
            return byHash;
        return null;
    }

    private static string Sha1Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(stream));
    }

    private static void MoveToTrash(string file, string trash)
    {
        Directory.CreateDirectory(trash);
        var destination = Path.Combine(trash, Path.GetFileName(file));
        if (File.Exists(destination)) destination = Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(file));
        File.Move(file, destination);
    }
}
