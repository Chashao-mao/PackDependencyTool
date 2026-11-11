using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Persistence;

public sealed class ModListSnapshotEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? NameZh { get; set; }
    public string Version { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public string? Sha1 { get; set; }
}

public sealed class ModListSnapshot
{
    public DateTimeOffset CapturedAt { get; set; }
    public string PackName { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public List<ModListSnapshotEntry> Mods { get; set; } = [];
}

public enum ModListChangeKind
{
    Added,
    Removed,
    VersionChanged,
    NameChanged,
}

public sealed class ModListChange
{
    public ModListChangeKind Kind { get; init; }
    public string ModId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Before { get; init; } = string.Empty;
    public string After { get; init; } = string.Empty;

    public string KindText => Kind switch
    {
        ModListChangeKind.Added => "新增",
        ModListChangeKind.Removed => "移除",
        ModListChangeKind.VersionChanged => "版本变化",
        _ => "名称变化",
    };
}

public sealed class ModListHistoryService
{
    public const string DirectoryName = ".packdep-history";
    private const int MaxSnapshots = 50;

    public string HistoryRoot(string packRoot) => Path.Combine(packRoot, DirectoryName);

    public ModListSnapshot Capture(ScanResult scan, GraphDocument graph)
    {
        var entries = graph.Nodes
            .Where(node => !node.IsSynthetic && !node.IsEmbedded && !string.IsNullOrWhiteSpace(node.FilePath))
            .OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .Select(node => new ModListSnapshotEntry
            {
                Id = node.Id,
                Name = node.Name,
                NameZh = node.NameZh,
                Version = node.Version,
                FilePath = node.FilePath,
                Sha1 = node.Sha1,
            })
            .ToList();
        var fingerprintSource = string.Join('\n', entries.Select(entry =>
            $"{entry.Id}|{entry.Version}|{entry.Sha1}|{entry.NameZh}"));
        return new ModListSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            PackName = scan.Profile.PackName ?? Path.GetFileName(scan.RootPath),
            Profile = $"{scan.Profile.DisplayLoaderWithVersion} · {scan.Profile.McVersion}",
            Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))),
            Mods = entries,
        };
    }

    public void Save(string packRoot, ModListSnapshot snapshot)
    {
        var root = HistoryRoot(packRoot);
        Directory.CreateDirectory(root);
        var latest = List(packRoot).FirstOrDefault();
        if (latest.Snapshot is not null &&
            string.Equals(latest.Snapshot.Fingerprint, snapshot.Fingerprint, StringComparison.OrdinalIgnoreCase))
            return;

        var file = Path.Combine(root, $"{snapshot.CapturedAt:yyyyMMdd-HHmmss-fff}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(snapshot));
        foreach (var old in List(packRoot).Skip(MaxSnapshots))
        {
            try { File.Delete(old.FilePath ?? string.Empty); } catch (IOException) { }
        }
    }

    public IReadOnlyList<(string? FilePath, ModListSnapshot Snapshot)> List(string packRoot)
    {
        var root = HistoryRoot(packRoot);
        if (!Directory.Exists(root)) return [];
        var result = new List<(string?, ModListSnapshot)>();
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<ModListSnapshot>(File.ReadAllText(file));
                if (snapshot is not null) result.Add((file, snapshot));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Ignore a damaged history file.
            }
        }
        return result.OrderByDescending(item => item.Item2.CapturedAt).ToList();
    }

    public IReadOnlyList<ModListChange> Compare(ModListSnapshot before, ModListSnapshot after)
    {
        var oldMods = before.Mods.ToDictionary(mod => mod.Id, StringComparer.OrdinalIgnoreCase);
        var newMods = after.Mods.ToDictionary(mod => mod.Id, StringComparer.OrdinalIgnoreCase);
        var changes = new List<ModListChange>();
        foreach (var (id, current) in newMods)
        {
            if (!oldMods.TryGetValue(id, out var previous))
            {
                changes.Add(new ModListChange
                {
                    Kind = ModListChangeKind.Added, ModId = id, Name = current.Name,
                    After = current.Version,
                });
                continue;
            }
            if (!string.Equals(previous.Version, current.Version, StringComparison.OrdinalIgnoreCase))
                changes.Add(new ModListChange
                {
                    Kind = ModListChangeKind.VersionChanged, ModId = id, Name = current.Name,
                    Before = previous.Version, After = current.Version,
                });
            else if (!string.Equals(previous.NameZh, current.NameZh, StringComparison.OrdinalIgnoreCase))
                changes.Add(new ModListChange
                {
                    Kind = ModListChangeKind.NameChanged, ModId = id, Name = current.Name,
                    Before = previous.NameZh ?? previous.Name, After = current.NameZh ?? current.Name,
                });
        }
        foreach (var (id, previous) in oldMods.Where(pair => !newMods.ContainsKey(pair.Key)))
            changes.Add(new ModListChange
            {
                Kind = ModListChangeKind.Removed, ModId = id, Name = previous.Name,
                Before = previous.Version,
            });
        return changes;
    }
}
