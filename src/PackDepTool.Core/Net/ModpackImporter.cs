using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PackDepTool.Core.Persistence;

namespace PackDepTool.Core.Net;

/// <summary>
/// Imports Modrinth (.mrpack) and CurseForge (manifest.json) packs, downloading
/// their files from official APIs and extracting overrides with path-traversal protection.
/// </summary>
public sealed class ModpackImporter
{
    private const long MaxExtractedBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxMetadataBytes = 16 * 1024 * 1024;

    private readonly ModDownloadService _downloader;
    private readonly CurseForgeClient _curseForge;
    private readonly ApiKeyProvider _keys;

    public ModpackImporter(
        ModDownloadService downloader,
        CurseForgeClient curseForge,
        ApiKeyProvider keys)
    {
        _downloader = downloader;
        _curseForge = curseForge;
        _keys = keys;
    }

    public async Task<PackImportResult> ImportAsync(
        string archivePath,
        string parentDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("整合包文件不存在", archivePath);

        Directory.CreateDirectory(parentDirectory);

        await using var stream = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var modrinthText = ReadEntryText(archive, "modrinth.index.json");
        var manifestText = ReadEntryText(archive, "manifest.json");

        if (!string.IsNullOrWhiteSpace(modrinthText))
            return await ImportModrinthAsync(archive, modrinthText!, parentDirectory, progress, cancellationToken)
                .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(manifestText))
            return await ImportCurseForgeAsync(archive, manifestText!, parentDirectory, progress, cancellationToken)
                .ConfigureAwait(false);

        return ExtractGeneric(archive, Path.GetFileNameWithoutExtension(archivePath), parentDirectory, progress, cancellationToken);
    }

    private async Task<PackImportResult> ImportModrinthAsync(
        ZipArchive archive,
        string indexText,
        string parentDirectory,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(indexText);
        var rootElement = doc.RootElement;
        var name = GetString(rootElement, "name") ?? "Modrinth Pack";
        var packRoot = CreatePackDirectory(parentDirectory, name);

        var result = new PackImportResult { PackRoot = packRoot };
        WriteText(packRoot, "modrinth.index.json", indexText);

        var files = new List<JsonElement>();
        if (rootElement.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
            files.AddRange(fileArray.EnumerateArray());

        var index = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            var relative = GetString(file, "path") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(relative))
            {
                result.SkippedFiles++;
                continue;
            }

            if (file.TryGetProperty("env", out var env) &&
                string.Equals(GetString(env, "client"), "unsupported", StringComparison.OrdinalIgnoreCase))
            {
                result.SkippedFiles++;
                continue;
            }

            var destination = SafeCombine(packRoot, relative);
            if (destination is null)
            {
                result.Warnings.Add($"已跳过不安全的路径：{relative}");
                continue;
            }

            var url = GetFirstDownloadUrl(file);
            if (string.IsNullOrWhiteSpace(url))
            {
                result.MissingFiles.Add(relative);
                result.Warnings.Add($"没有下载地址：{relative}");
                continue;
            }

            var sha1 = file.TryGetProperty("hashes", out var hashes) ? GetString(hashes, "sha1") : null;
            var sha512 = file.TryGetProperty("hashes", out var hashes2) ? GetString(hashes2, "sha512") : null;
            var size = file.TryGetProperty("fileSize", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;

            progress?.Report(new DownloadProgress(
                "下载整合包文件", index, files.Count, $"{index}/{files.Count} {Path.GetFileName(relative)}"));

            try
            {
                await _downloader.DownloadUrlAsync(
                        url!, destination, sha1, sha512, size, progress, cancellationToken)
                    .ConfigureAwait(false);
                result.DownloadedFiles++;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or
                                           InvalidOperationException)
            {
                result.MissingFiles.Add(relative);
                result.Warnings.Add($"{Path.GetFileName(relative)}：{ex.Message}");
            }
        }

        ExtractPrefix(archive, "overrides/", packRoot, result, progress, cancellationToken);
        ExtractPrefix(archive, "client-overrides/", packRoot, result, progress, cancellationToken);
        return result;
    }

    private async Task<PackImportResult> ImportCurseForgeAsync(
        ZipArchive archive,
        string manifestText,
        string parentDirectory,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(manifestText);
        var rootElement = doc.RootElement;
        var name = GetString(rootElement, "name") ?? "CurseForge Pack";
        var packRoot = CreatePackDirectory(parentDirectory, name);

        var result = new PackImportResult { PackRoot = packRoot };
        WriteText(packRoot, "manifest.json", manifestText);

        var files = new List<JsonElement>();
        if (rootElement.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
            files.AddRange(fileArray.EnumerateArray());

        var key = await _keys.GetCurseForgeKeyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.Warnings.Add("未配置 CurseForge API Key：已导入 overrides，但无法下载模组文件。");
            foreach (var file in files)
                result.MissingFiles.Add($"project {GetLong(file, "projectID")}/file {GetLong(file, "fileID")}");
        }
        else
        {
            var index = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                if (!file.TryGetProperty("projectID", out var projectElement) ||
                    !projectElement.TryGetInt64(out var projectId) ||
                    !file.TryGetProperty("fileID", out var fileElement) ||
                    !fileElement.TryGetInt64(out var fileId))
                {
                    result.SkippedFiles++;
                    continue;
                }

                progress?.Report(new DownloadProgress(
                    "下载 CurseForge 文件", index, files.Count, $"{index}/{files.Count} project {projectId}"));

                try
                {
                    var metadata = await _curseForge.GetFileAsync(projectId, fileId, cancellationToken)
                        .ConfigureAwait(false);
                    var url = metadata?.DownloadUrl ??
                              await _curseForge.GetDownloadUrlAsync(projectId, fileId, cancellationToken)
                                  .ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(url))
                    {
                        result.MissingFiles.Add($"{projectId}/{fileId}");
                        result.Warnings.Add($"project {projectId} 无下载地址（作者可能关闭第三方下载）。");
                        continue;
                    }

                    var fileName = metadata?.FileName ?? $"mod-{projectId}-{fileId}.jar";
                    var destination = SafeCombine(packRoot, Path.Combine("mods", fileName));
                    if (destination is null)
                    {
                        result.Warnings.Add($"已跳过不安全的文件名：{fileName}");
                        continue;
                    }

                    await _downloader.DownloadUrlAsync(
                            url, destination, metadata?.Sha1, null, metadata?.FileLength ?? 0,
                            progress, cancellationToken)
                        .ConfigureAwait(false);
                    result.DownloadedFiles++;
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or
                                               InvalidOperationException or CurseForgeApiException)
                {
                    result.MissingFiles.Add($"{projectId}/{fileId}");
                    result.Warnings.Add($"project {projectId}：{ex.Message}");
                }
            }
        }

        var overridesPrefix = GetString(rootElement, "overrides") ?? "overrides";
        overridesPrefix = overridesPrefix.TrimEnd('/', '\\') + "/";
        ExtractPrefix(archive, overridesPrefix, packRoot, result, progress, cancellationToken);
        return result;
    }

    private static PackImportResult ExtractGeneric(
        ZipArchive archive,
        string name,
        string parentDirectory,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var packRoot = CreatePackDirectory(parentDirectory, name);
        var result = new PackImportResult { PackRoot = packRoot };
        result.Warnings.Add("未找到 modrinth.index.json 或 manifest.json，已按普通压缩包解压。");

        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var index = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            ExtractEntry(entry, entry.FullName, packRoot, result, new DownloadProgress(
                "解压整合包", index, entries.Count, entry.FullName), cancellationToken);
        }

        return result;
    }

    private static void ExtractPrefix(
        ZipArchive archive,
        string prefix,
        string packRoot,
        PackImportResult result,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name) &&
                            entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var index = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            var relative = entry.FullName[prefix.Length..];
            var stage = new DownloadProgress("解压 overrides", index, entries.Count, relative);
            progress?.Report(stage);
            ExtractEntry(entry, relative, packRoot, result, stage, cancellationToken);
        }
    }

    private static void ExtractEntry(
        ZipArchiveEntry entry,
        string relative,
        string packRoot,
        PackImportResult result,
        DownloadProgress stage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Length > MaxExtractedBytes)
        {
            result.Warnings.Add($"文件过大，已跳过：{relative}");
            return;
        }

        var destination = SafeCombine(packRoot, relative);
        if (destination is null)
        {
            result.Warnings.Add($"已跳过不安全的路径：{relative}");
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = entry.Open();
            using var target = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            source.CopyTo(target);
            result.ExtractedFiles++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.Warnings.Add($"{relative}：{ex.Message}");
        }
    }

    private static string? SafeCombine(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized)) return null;

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".." || segment.Contains(':'))) return null;

        try
        {
            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var destination = Path.GetFullPath(Path.Combine(rootFull, normalized));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return destination.StartsWith(rootFull, comparison) ? destination : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string CreatePackDirectory(string parentDirectory, string name)
    {
        var safeName = SanitizeFolderName(name);
        var root = Path.Combine(parentDirectory, safeName);
        var suffix = 2;
        while (Directory.Exists(root))
            root = Path.Combine(parentDirectory, $"{safeName} ({suffix++})");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        return root;
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return cleaned.Length == 0 ? "Imported Pack" : cleaned;
    }

    private static string? ReadEntryText(ZipArchive archive, string name)
    {
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length > MaxMetadataBytes) return null;

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteText(string root, string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string? GetFirstDownloadUrl(JsonElement file)
    {
        if (!file.TryGetProperty("downloads", out var downloads) ||
            downloads.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var download in downloads.EnumerateArray())
        {
            if (download.ValueKind == JsonValueKind.String)
            {
                var value = download.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;
}
