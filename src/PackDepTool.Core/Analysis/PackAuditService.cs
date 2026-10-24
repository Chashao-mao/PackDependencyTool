using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public sealed class PackFileEntry
{
    public string FileName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string AbsolutePath { get; init; } = string.Empty;
    public long Size { get; init; }
    public string Sha1 { get; init; } = string.Empty;
    public List<string> ModIds { get; init; } = [];
    public List<string> ModNames { get; init; } = [];
    public string SizeText => Size < 1024 * 1024
        ? $"{Size / 1024d:0.#} KB"
        : $"{Size / 1024d / 1024d:0.#} MB";
}

public sealed class DuplicateModGroup
{
    public string ModId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public List<PackFileEntry> Files { get; init; } = [];
}

public sealed class DuplicateFileGroup
{
    public string Sha1 { get; init; } = string.Empty;
    public long Size { get; init; }
    public List<PackFileEntry> Files { get; init; } = [];
    public string SizeText => Size < 1024 * 1024
        ? $"{Size / 1024d:0.#} KB"
        : $"{Size / 1024d / 1024d:0.#} MB";
}

public sealed class MissingModInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Loader { get; init; } = string.Empty;
}

public sealed class PackAuditReport
{
    public string PackName { get; init; } = string.Empty;
    public string Profile { get; init; } = string.Empty;
    public int JarCount { get; init; }
    public int ModCount { get; init; }
    public int DisabledCount { get; init; }
    public int MissingCount { get; init; }
    public int ConflictCount { get; init; }
    public int MismatchCount { get; init; }
    public long TotalBytes { get; init; }
    public string TotalSizeText => TotalBytes < 1024L * 1024 * 1024
        ? $"{TotalBytes / 1024d / 1024d:0.#} MB"
        : $"{TotalBytes / 1024d / 1024d / 1024d:0.##} GB";

    public List<PackFileEntry> Files { get; init; } = [];
    public List<PackFileEntry> LargestFiles { get; init; } = [];
    public List<DuplicateModGroup> DuplicateModIds { get; init; } = [];
    public List<DuplicateFileGroup> DuplicateHashes { get; init; } = [];
    public Dictionary<LoaderKind, int> LoaderCounts { get; init; } = [];
    public Dictionary<ModSide, int> SideCounts { get; init; } = [];
    public List<MissingModInfo> MissingMods { get; init; } = [];
    public Dictionary<ModCategory, int> CategoryCounts { get; init; } = [];

    public bool HasProblems => MissingCount + ConflictCount + MismatchCount > 0;
    public bool HasMissing => MissingMods.Count > 0;
    public string CategoryText => CategoryCounts.Count == 0
        ? "未分类"
        : string.Join(" · ", CategoryCounts
            .OrderByDescending(pair => pair.Value)
            .Take(4)
            .Select(pair => $"{ModCategoryText.Name(pair.Key)} {pair.Value}"));
    public string LoaderText => LoaderCounts.Count == 0
        ? "未识别"
        : string.Join(" · ", LoaderCounts
            .OrderByDescending(pair => pair.Value)
            .Select(pair => $"{LoaderName(pair.Key)} {pair.Value}"));
    private static string LoaderName(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "未知",
    };

    public string SideText
    {
        get
        {
            var client = SideCounts.GetValueOrDefault(ModSide.Client);
            var server = SideCounts.GetValueOrDefault(ModSide.Server);
            var both = SideCounts.GetValueOrDefault(ModSide.Both);
            return $"客户端 {client} · 服务端 {server} · 双端 {both}";
        }
    }
    public int DuplicateHashCount => DuplicateHashes.Count;
    public int DuplicateJarCount => DuplicateHashes.Sum(group => group.Files.Count - 1);
    public string Summary =>
        $"{ModCount} 个模组 · {JarCount} 个 JAR · 缺失 {MissingCount} · 冲突 {ConflictCount}";
}

/// <summary>Pack-author audit: duplicate IDs/hashes, size, loader mix and problem counts.</summary>
public sealed class PackAuditService
{
    public PackAuditReport Audit(ScanResult scan, GraphDocument graph)
    {
        var files = BuildFiles(scan);
        var duplicateIds = files
            .SelectMany(file => file.ModIds.Select(id => (id, file)))
            .GroupBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.file.AbsolutePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => new DuplicateModGroup
            {
                ModId = group.Key,
                Name = group.SelectMany(item => item.file.ModNames).FirstOrDefault() ?? group.Key,
                Files = group.Select(item => item.file)
                    .DistinctBy(item => item.AbsolutePath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            })
            .OrderBy(group => group.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var duplicateHashes = files
            .Where(file => !string.IsNullOrWhiteSpace(file.Sha1))
            .GroupBy(file => file.Sha1, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new DuplicateFileGroup
            {
                Sha1 = group.Key,
                Size = group.First().Size,
                Files = group.ToList(),
            })
            .OrderByDescending(group => group.Size)
            .ToList();

        var loaders = scan.Jars
            .SelectMany(jar => jar.Mods)
            .GroupBy(mod => mod.Loader)
            .ToDictionary(group => group.Key, group => group.Count());

        var disabled = ListDisabledCount(scan.ModsDirectory);
        var categories = graph.Nodes
            .GroupBy(node => node.Category)
            .ToDictionary(group => group.Key, group => group.Count());
        var sides = scan.Jars
            .SelectMany(jar => jar.Mods)
            .GroupBy(mod => mod.Side)
            .ToDictionary(group => group.Key, group => group.Count());
        var missing = graph.Nodes
            .Where(node => node.Status == ModStatus.Missing)
            .OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(node => new MissingModInfo
            {
                Id = node.Id,
                Name = node.Name,
                Loader = LoaderName(node.Loader),
            })
            .ToList();

        return new PackAuditReport
        {
            PackName = scan.Profile.PackName ?? Path.GetFileName(scan.RootPath),
            Profile = $"{scan.Profile.DisplayLoader} · Minecraft {scan.Profile.McVersion}",
            JarCount = scan.Jars.Count,
            ModCount = graph.Nodes.Count(node => !node.IsSynthetic),
            DisabledCount = disabled,
            MissingCount = graph.Nodes.Count(node => node.Status == ModStatus.Missing),
            ConflictCount = graph.Nodes.Count(node => node.Status == ModStatus.Conflict),
            MismatchCount = graph.Nodes.Count(node => node.Status == ModStatus.VersionMismatch),
            TotalBytes = files.Sum(file => file.Size),
            Files = files,
            LargestFiles = files.OrderByDescending(file => file.Size).Take(10).ToList(),
            DuplicateModIds = duplicateIds,
            DuplicateHashes = duplicateHashes,
            LoaderCounts = loaders,
            SideCounts = sides,
            MissingMods = missing,
            CategoryCounts = categories,
        };
    }

    private static List<PackFileEntry> BuildFiles(ScanResult scan)
    {
        var files = new List<PackFileEntry>(scan.Jars.Count);
        foreach (var jar in scan.Jars)
        {
            long size = 0;
            try
            {
                if (File.Exists(jar.AbsolutePath))
                    size = new FileInfo(jar.AbsolutePath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Size is informational only.
            }

            files.Add(new PackFileEntry
            {
                FileName = Path.GetFileName(jar.FilePath),
                RelativePath = jar.FilePath,
                AbsolutePath = jar.AbsolutePath,
                Size = size,
                Sha1 = jar.Sha1,
                ModIds = jar.Mods.Select(mod => mod.ModId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList(),
                ModNames = jar.Mods.Select(mod => mod.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
            });
        }

        return files;
    }

    private static string LoaderName(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "未知",
    };

    private static int ListDisabledCount(string modsDirectory)
    {
        try
        {
            return Directory.Exists(modsDirectory)
                ? Directory.EnumerateFiles(modsDirectory, "*.jar.disabled", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
