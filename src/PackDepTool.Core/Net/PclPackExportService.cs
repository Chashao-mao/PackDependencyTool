using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Net;

public sealed class PclPackExportResult
{
    public string FilePath { get; init; } = string.Empty;
    public int ModrinthFiles { get; set; }
    public int OverrideMods { get; set; }
    public int ConfigFiles { get; set; }
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Exports the current pack as a Modrinth .mrpack that PCL2 can import and launch.
/// Mods that can be resolved on Modrinth are referenced by URL + hashes; other jars
/// are placed under overrides/mods so launchers still install them.
/// </summary>
public sealed class PclPackExportService
{
    private readonly ModrinthClient _modrinth;

    public PclPackExportService(ModrinthClient modrinth) => _modrinth = modrinth;

    public async Task<PclPackExportResult> ExportAsync(
        GraphDocument graph,
        PackProfile profile,
        string packRoot,
        string outputPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new PclPackExportResult { FilePath = outputPath };
        var nodes = graph.Nodes
            .Where(node => !node.IsSynthetic)
            .ToList();

        progress?.Report("读取 Modrinth 版本信息…");
        var hashes = nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Sha1))
            .Select(node => node.Sha1!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var versions = new Dictionary<string, ModrinthVersionInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (hashes.Count > 0)
                versions = await _modrinth.GetVersionsByHashesAsync(hashes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ModrinthApiException or HttpRequestException)
        {
            result.Warnings.Add($"Modrinth 查询失败，所有模组将作为 overrides 打包：{ex.Message}");
        }

        var indexFiles = new List<ExportIndexFile>();
        var overrideMods = new List<string>();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(node.Sha1) &&
                versions.TryGetValue(node.Sha1, out var version))
            {
                var file = version.Files.FirstOrDefault(candidate =>
                               candidate.IsPrimary && candidate.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                           ?? version.Files.FirstOrDefault(candidate =>
                               candidate.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

                if (file is not null && !string.IsNullOrWhiteSpace(file.Url))
                {
                    indexFiles.Add(new ExportIndexFile
                    {
                        Path = "mods/" + SafeFileName(file.FileName, $"{node.Id}.jar"),
                        Hashes = new Dictionary<string, string>
                        {
                            ["sha1"] = file.Sha1 ?? node.Sha1!,
                            ["sha512"] = file.Sha512 ?? string.Empty,
                        }.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                            .ToDictionary(pair => pair.Key, pair => pair.Value),
                        Downloads = [file.Url],
                        FileSize = file.Size,
                        Env = new ExportEnvironment { Client = "required", Server = "required" },
                    });
                    result.ModrinthFiles++;
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(node.FilePath))
            {
                var localPath = Path.IsPathRooted(node.FilePath)
                    ? node.FilePath
                    : Path.Combine(packRoot, node.FilePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(localPath))
                {
                    overrideMods.Add(localPath);
                    result.OverrideMods++;
                    continue;
                }
            }

            result.Warnings.Add($"{node.Name}（{node.Id}）没有本地 JAR，已跳过。");
        }

        var dependencies = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(profile.McVersion) &&
            !string.Equals(profile.McVersion, "unknown", StringComparison.OrdinalIgnoreCase))
            dependencies["minecraft"] = profile.McVersion;

        var loaderKey = profile.Loader switch
        {
            LoaderKind.Fabric => "fabric-loader",
            LoaderKind.Quilt => "quilt-loader",
            LoaderKind.Forge => "forge",
            LoaderKind.NeoForge => "neoforge",
            _ => null,
        };
        if (loaderKey is not null && !string.IsNullOrWhiteSpace(profile.LoaderVersion))
            dependencies[loaderKey] = profile.LoaderVersion!;
        else if (loaderKey is not null)
            result.Warnings.Add($"未检测到 {profile.DisplayLoader} 版本，PCL 导入后需要手动选择加载器版本。");
        else
            result.Warnings.Add("未检测到加载器，PCL 导入后需要手动选择。");

        var index = new ExportIndex
        {
            FormatVersion = 1,
            Game = "minecraft",
            VersionId = $"packdep-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Name = string.IsNullOrWhiteSpace(profile.PackName)
                ? new DirectoryInfo(packRoot).Name
                : profile.PackName!,
            Summary = "Exported by Pack Dependency Tool",
            Files = indexFiles,
            Dependencies = dependencies,
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        progress?.Report("写入 .mrpack…");
        await using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var indexEntry = archive.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
            await using (var writer = new StreamWriter(indexEntry.Open(), new UTF8Encoding(false)))
                await writer.WriteAsync(JsonSerializer.Serialize(index, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                })).ConfigureAwait(false);

            foreach (var modPath in overrideMods.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await AddFileAsync(archive, "overrides/mods/" + SafeFileName(Path.GetFileName(modPath), "mod.jar"),
                    modPath, cancellationToken).ConfigureAwait(false);
            }

            foreach (var folder in new[] { "config", "defaultconfigs" })
            {
                var path = Path.Combine(packRoot, folder);
                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(packRoot, file).Replace('\\', '/');
                    await AddFileAsync(archive, "overrides/" + relative, file, cancellationToken)
                        .ConfigureAwait(false);
                    result.ConfigFiles++;
                }
            }

            var options = Path.Combine(packRoot, "options.txt");
            if (File.Exists(options))
                await AddFileAsync(archive, "overrides/options.txt", options, cancellationToken)
                    .ConfigureAwait(false);
        }

        progress?.Report("导出完成");
        return result;
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        string entryName,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) return;
        var info = new FileInfo(sourcePath);
        if (info.Length > 512L * 1024 * 1024) return;

        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var source = File.OpenRead(sourcePath);
        await using var target = entry.Open();
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static string SafeFileName(string? fileName, string fallback)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (name.Length == 0) name = fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private sealed class ExportIndex
    {
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
        [JsonPropertyName("game")] public string Game { get; set; } = "minecraft";
        [JsonPropertyName("versionId")] public string VersionId { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
        [JsonPropertyName("files")] public List<ExportIndexFile> Files { get; set; } = [];
        [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = [];
    }

    private sealed class ExportIndexFile
    {
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = [];
        [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = [];
        [JsonPropertyName("fileSize")] public long FileSize { get; set; }
        [JsonPropertyName("env")] public ExportEnvironment Env { get; set; } = new();
    }

    private sealed class ExportEnvironment
    {
        [JsonPropertyName("client")] public string Client { get; set; } = "required";
        [JsonPropertyName("server")] public string Server { get; set; } = "required";
    }
}
