using System.Text.Json;
using System.Text.RegularExpressions;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Scanning;

public sealed class MinecraftInstanceInfo
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string GameDirectory { get; init; } = string.Empty;
    public string EffectiveRoot { get; init; } = string.Empty;
    public string MinecraftVersion { get; init; } = "?";
    public LoaderKind Loader { get; init; } = LoaderKind.Unknown;
    public int ModCount { get; init; }
    public int DisabledModCount { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
    public bool HasJson { get; init; }

    public string Detail => $"{MinecraftVersion} · {LoaderText(Loader)} · {ModCount} 个模组";
    public string ModifiedText => ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string LoaderText(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "原版 / 未知",
    };
}

public sealed class InstanceCatalogResult
{
    public string? VersionsDirectory { get; init; }
    public List<MinecraftInstanceInfo> Instances { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Lists Minecraft instances under a launcher-style versions folder and reads
/// enough metadata to show the version, loader and mod count without a full scan.
/// </summary>
public sealed class InstanceCatalogService
{
    private static readonly Regex VersionPattern = new(@"\d+\.\d+(?:\.\d+)?", RegexOptions.Compiled);

    public Task<InstanceCatalogResult> ScanAsync(
        string? path,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(path, cancellationToken), cancellationToken);

    public static string? ResolveVersionsDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) return null;

            if (string.Equals(Path.GetFileName(full), "versions", StringComparison.OrdinalIgnoreCase))
                return full;

            var child = Path.Combine(full, "versions");
            if (Directory.Exists(child)) return child;

            var parent = Directory.GetParent(full);
            if (parent is not null &&
                string.Equals(parent.Name, "versions", StringComparison.OrdinalIgnoreCase))
                return parent.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return null;
    }

    private static InstanceCatalogResult Scan(string? path, CancellationToken cancellationToken)
    {
        var result = new InstanceCatalogResult
        {
            VersionsDirectory = ResolveVersionsDirectory(path),
        };
        if (result.VersionsDirectory is null)
        {
            result.Warnings.Add("所选目录不是 versions 文件夹，也不是包含 versions 的游戏目录。");
            return result;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(
                         result.VersionsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var name = Path.GetFileName(directory);
                    var json = ReadJson(directory, name);
                    var modCount = CountFiles(Path.Combine(directory, "mods"), "*.jar");
                    var disabledCount = CountFiles(Path.Combine(directory, "mods"), "*.jar.disabled");
                    var id = json?.Id ?? name;
                    var inherits = json?.InheritsFrom;
                    var detected = DetectLoader(id, inherits, json?.Libraries);
                    result.Instances.Add(new MinecraftInstanceInfo
                    {
                        Name = name,
                        Path = directory,
                        MinecraftVersion = DetectMinecraftVersion(inherits ?? id),
                        Loader = detected,
                        ModCount = modCount,
                        DisabledModCount = disabledCount,
                        ModifiedAt = new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero),
                        HasJson = json is not null,
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                               JsonException or InvalidOperationException)
                {
                    result.Warnings.Add($"{Path.GetFileName(directory)}：{ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add(ex.Message);
        }

        result.Instances.Sort((left, right) =>
        {
            var version = string.Compare(right.MinecraftVersion, left.MinecraftVersion,
                StringComparison.OrdinalIgnoreCase);
            return version != 0
                ? version
                : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return result;
    }

    private sealed class VersionJson
    {
        public string? Id { get; init; }
        public string? InheritsFrom { get; init; }
        public List<string> Libraries { get; init; } = [];
    }

    private static VersionJson? ReadJson(string directory, string name)
    {
        var candidates = new[]
        {
            Path.Combine(directory, name + ".json"),
            Path.Combine(directory, "version.json"),
        }.Concat(Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var file in candidates)
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var libraries = new List<string>();
                if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var library in libs.EnumerateArray())
                    {
                        if (library.TryGetProperty("name", out var libraryName) &&
                            libraryName.ValueKind == JsonValueKind.String)
                            libraries.Add(libraryName.GetString() ?? string.Empty);
                    }
                }

                return new VersionJson
                {
                    Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null,
                    InheritsFrom = root.TryGetProperty("inheritsFrom", out var inherits) &&
                                   inherits.ValueKind == JsonValueKind.String
                        ? inherits.GetString()
                        : null,
                    Libraries = libraries,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Try the next JSON file in the instance folder.
            }
        }

        return null;
    }

    private static LoaderKind DetectLoader(
        string id, string? inheritsFrom, IReadOnlyList<string>? libraries)
    {
        var text = (id + " " + inheritsFrom + " " +
                    string.Join(' ', libraries ?? [])).ToLowerInvariant();
        if (text.Contains("neoforge", StringComparison.Ordinal)) return LoaderKind.NeoForge;
        if (text.Contains("quilt", StringComparison.Ordinal)) return LoaderKind.Quilt;
        if (text.Contains("fabric", StringComparison.Ordinal)) return LoaderKind.Fabric;
        if (text.Contains("forge", StringComparison.Ordinal)) return LoaderKind.Forge;
        return LoaderKind.Unknown;
    }

    private static string DetectMinecraftVersion(string value)
    {
        var match = VersionPattern.Match(value);
        return match.Success ? match.Value : value;
    }

    private static int CountFiles(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
