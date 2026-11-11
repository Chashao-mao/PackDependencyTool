using PackDepTool.Core.Models;

namespace PackDepTool.Core.Persistence;

public sealed class VersionLockEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Sha1 { get; set; } = string.Empty;
    public LoaderKind Loader { get; set; }
    public ModSide Side { get; set; }
}

public sealed class VersionLockDocument
{
    public int Schema { get; set; } = 1;
    public string PackName { get; set; } = string.Empty;
    public string McVersion { get; set; } = string.Empty;
    public LoaderKind Loader { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<VersionLockEntry> Mods { get; set; } = [];
}

public enum VersionLockDiffKind
{
    Same,
    Added,
    Removed,
    VersionChanged,
    FileChanged,
}

public sealed class VersionLockDiffEntry
{
    public VersionLockDiffKind Kind { get; init; }
    public string ModId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string LockedVersion { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public sealed class VersionLockDiff
{
    public List<VersionLockDiffEntry> Entries { get; } = [];
    public int ChangedCount => Entries.Count(entry => entry.Kind != VersionLockDiffKind.Same);
}

/// <summary>
/// A human-readable version lock inspired by packwiz lock files: it records the
/// installed mod versions and SHA1s so later changes can be identified offline.
/// </summary>
public sealed class VersionLockService
{
    public const string FileName = "packdep.lock.json";

    public string LockPath(string packRoot) => Path.Combine(packRoot, FileName);

    public VersionLockDocument? Load(string packRoot)
    {
        var path = LockPath(packRoot);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonDefaults.Deserialize<VersionLockDocument>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public VersionLockDocument Create(ScanResult scan, GraphDocument graph)
    {
        var mods = graph.Nodes
            .Where(node => !node.IsSynthetic && !node.IsEmbedded && !string.IsNullOrWhiteSpace(node.FilePath))
            .OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .Select(node => new VersionLockEntry
            {
                Id = node.Id,
                Name = node.Name,
                Version = node.Version,
                File = node.FilePath,
                Sha1 = node.Sha1 ?? string.Empty,
                Loader = node.Loader,
                Side = node.Side,
            })
            .ToList();

        return new VersionLockDocument
        {
            PackName = scan.Profile.PackName ?? Path.GetFileName(scan.RootPath),
            McVersion = scan.Profile.McVersion,
            Loader = scan.Profile.Loader,
            CreatedAt = DateTimeOffset.UtcNow,
            Mods = mods,
        };
    }

    public void Save(string packRoot, VersionLockDocument document)
    {
        Directory.CreateDirectory(packRoot);
        File.WriteAllText(LockPath(packRoot), JsonDefaults.Serialize(document));
    }

    public VersionLockDiff Compare(VersionLockDocument document, GraphDocument graph)
    {
        var locked = document.Mods.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var current = graph.Nodes
            .Where(node => !node.IsSynthetic && !node.IsEmbedded && !string.IsNullOrWhiteSpace(node.FilePath))
            .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var diff = new VersionLockDiff();

        foreach (var node in current.Values.OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!locked.TryGetValue(node.Id, out var entry))
            {
                diff.Entries.Add(new VersionLockDiffEntry
                {
                    Kind = VersionLockDiffKind.Added,
                    ModId = node.Id,
                    Name = node.Name,
                    CurrentVersion = node.Version,
                    Detail = "锁定后新增",
                });
                continue;
            }

            if (!string.Equals(entry.Version, node.Version, StringComparison.OrdinalIgnoreCase))
            {
                diff.Entries.Add(new VersionLockDiffEntry
                {
                    Kind = VersionLockDiffKind.VersionChanged,
                    ModId = node.Id,
                    Name = node.Name,
                    LockedVersion = entry.Version,
                    CurrentVersion = node.Version,
                    Detail = $"{entry.Version} → {node.Version}",
                });
            }
            else if (!string.IsNullOrWhiteSpace(entry.Sha1) &&
                     !string.IsNullOrWhiteSpace(node.Sha1) &&
                     !string.Equals(entry.Sha1, node.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                diff.Entries.Add(new VersionLockDiffEntry
                {
                    Kind = VersionLockDiffKind.FileChanged,
                    ModId = node.Id,
                    Name = node.Name,
                    LockedVersion = entry.Version,
                    CurrentVersion = node.Version,
                    Detail = "版本号相同，但文件 SHA1 已变化",
                });
            }
            else
            {
                diff.Entries.Add(new VersionLockDiffEntry
                {
                    Kind = VersionLockDiffKind.Same,
                    ModId = node.Id,
                    Name = node.Name,
                    LockedVersion = entry.Version,
                    CurrentVersion = node.Version,
                });
            }
        }

        foreach (var entry in locked.Values
                     .Where(entry => !current.ContainsKey(entry.Id))
                     .OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase))
        {
            diff.Entries.Add(new VersionLockDiffEntry
            {
                Kind = VersionLockDiffKind.Removed,
                ModId = entry.Id,
                Name = entry.Name,
                LockedVersion = entry.Version,
                Detail = "锁定后已移除",
            });
        }

        diff.Entries.Sort((left, right) =>
        {
            var order = left.Kind == VersionLockDiffKind.Same ? 1 : 0;
            var other = right.Kind == VersionLockDiffKind.Same ? 1 : 0;
            return order != other ? order.CompareTo(other) : string.Compare(left.ModId, right.ModId,
                StringComparison.OrdinalIgnoreCase);
        });
        return diff;
    }
}
