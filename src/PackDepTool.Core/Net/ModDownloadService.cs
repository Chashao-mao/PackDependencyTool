using System.Security.Cryptography;
using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;

namespace PackDepTool.Core.Net;

/// <summary>Search/downloads mods and modpacks from the official Modrinth / CurseForge APIs.</summary>
public sealed class ModDownloadService
{
    private readonly HttpClient _http;
    private readonly ModrinthClient _modrinth;
    private readonly CurseForgeClient _curseForge;
    private readonly string _userAgent;
    private readonly ApiCacheStore? _cache;
    private readonly ResumableDownloader _resumable;
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan VersionCacheTtl = TimeSpan.FromHours(24);

    public ModDownloadService(
        HttpClient http,
        ModrinthClient modrinth,
        CurseForgeClient curseForge,
        string userAgent,
        ApiCacheStore? cache = null)
    {
        _http = http;
        _modrinth = modrinth;
        _curseForge = curseForge;
        _userAgent = userAgent;
        _cache = cache;
        _resumable = new ResumableDownloader(http, userAgent);
    }

    public async Task<List<DownloadProject>> SearchAsync(
        DownloadSource source,
        DownloadProjectType type,
        string query,
        CancellationToken cancellationToken = default)
    {
        var page = await SearchPageAsync(
            source, type, query, null, 0, 20, cancellationToken).ConfigureAwait(false);
        return page.Projects;
    }

    public async Task<DownloadSearchPage> SearchPageAsync(
        DownloadSource source,
        DownloadProjectType type,
        string query,
        DownloadCategory? category,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 50);
        if (string.IsNullOrWhiteSpace(query))
            return new DownloadSearchPage { Offset = safeOffset, Limit = safeLimit };

        var categoryKey = category is null
            ? "all"
            : $"{category.Name}:{category.ModrinthSlug}:{category.CurseForgeId}";
        var cacheKey = $"page:{source}:{type}:{query.Trim().ToLowerInvariant()}:{categoryKey}:{safeOffset}:{safeLimit}";
        if (_cache is not null)
        {
            var cached = await _cache.GetAsync<DownloadSearchPage>(
                "mod-download", cacheKey, SearchCacheTtl, cancellationToken).ConfigureAwait(false);
            if (cached is not null) return cached;
        }

        var projects = new List<DownloadProject>();
        var hasMore = false;
        if (source is DownloadSource.Modrinth or DownloadSource.All &&
            (category is null || !string.IsNullOrWhiteSpace(category.ModrinthSlug)))
        {
            var projectType = type == DownloadProjectType.Modpack ? "modpack" : "mod";
            var hits = await _modrinth.SearchAsync(
                query, safeOffset, safeLimit, category?.ModrinthSlug, cancellationToken, projectType)
                .ConfigureAwait(false);
            projects.AddRange(hits.Select(hit => new DownloadProject
            {
                Source = DownloadSource.Modrinth,
                Type = type,
                ProjectId = hit.Id,
                Slug = hit.Slug,
                Name = hit.Title,
                Description = hit.Description,
                IconUrl = hit.IconUrl,
                Author = hit.Author,
                Downloads = hit.Downloads ?? 0,
            }));
            hasMore = hits.Count >= safeLimit;
        }

        if (source is DownloadSource.CurseForge or DownloadSource.All &&
            (category is null || category.CurseForgeId is > 0))
        {
            try
            {
                var classId = type == DownloadProjectType.Modpack
                    ? CurseForgeClient.ModpackClassIdValue
                    : 6;
                var mods = await _curseForge.SearchAsync(
                    query, classId, safeLimit, safeOffset, category?.CurseForgeId, cancellationToken)
                    .ConfigureAwait(false);
                projects.AddRange(mods.Select(mod => new DownloadProject
                {
                    Source = DownloadSource.CurseForge,
                    Type = type,
                    ProjectId = mod.Id.ToString(),
                    Slug = mod.Slug,
                    Name = mod.Name,
                    Description = mod.Summary,
                    IconUrl = mod.IconUrl,
                    Downloads = mod.DownloadCount,
                }));
                hasMore |= mods.Count >= safeLimit;
            }
            catch (MissingApiKeyException)
            {
                // CurseForge is unavailable without a key; Modrinth results remain usable.
            }
        }

        var page = new DownloadSearchPage
        {
            Projects = projects
                .GroupBy(project => $"{project.Source}:{project.ProjectId}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(project => project.Downloads)
                .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            HasMore = hasMore,
            Offset = safeOffset,
            Limit = safeLimit,
        };
        if (_cache is not null)
            await _cache.SetAsync("mod-download", cacheKey, page, SearchCacheTtl, cancellationToken)
                .ConfigureAwait(false);
        return page;
    }

    public async Task<List<DownloadCategory>> GetCategoriesAsync(
        DownloadSource source,
        DownloadProjectType type,
        CancellationToken cancellationToken = default)
    {
        var projectType = type == DownloadProjectType.Modpack ? "modpack" : "mod";
        var modrinth = source is DownloadSource.Modrinth or DownloadSource.All
            ? await _modrinth.GetCategoriesAsync(projectType, cancellationToken).ConfigureAwait(false)
            : [];
        var curseForge = new List<DownloadCategory>();
        if (source is DownloadSource.CurseForge or DownloadSource.All)
        {
            try
            {
                var classId = type == DownloadProjectType.Modpack
                    ? CurseForgeClient.ModpackClassIdValue
                    : 6;
                curseForge = await _curseForge.GetCategoriesAsync(classId, type, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MissingApiKeyException)
            {
                // Keep Modrinth categories when CurseForge is unavailable.
            }
        }

        if (source != DownloadSource.All)
            return modrinth.Concat(curseForge)
                .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        var merged = new Dictionary<string, DownloadCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in modrinth)
        {
            var key = NormalizeCategoryName(category.Name);
            merged[key] = category;
        }
        foreach (var category in curseForge)
        {
            var key = NormalizeCategoryName(category.Name);
            if (merged.TryGetValue(key, out var existing))
            {
                merged[key] = new DownloadCategory
                {
                    Name = existing.Name,
                    ModrinthSlug = existing.ModrinthSlug,
                    CurseForgeId = category.CurseForgeId,
                    Type = type,
                };
            }
            else
            {
                merged[key] = category;
            }
        }

        return merged.Values
            .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string NormalizeCategoryName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public async Task<List<DownloadVersion>> GetVersionsAsync(
        DownloadProject project,
        string? mcVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        var versionCacheKey = $"versions:{project.Source}:{project.ProjectId}:{mcVersion}:{loader}";
        if (_cache is not null)
        {
            var cached = await _cache.GetAsync<List<DownloadVersion>>(
                "mod-download", versionCacheKey, VersionCacheTtl, cancellationToken).ConfigureAwait(false);
            if (cached is not null) return cached;
        }

        if (project.Source == DownloadSource.Modrinth)
        {
            var versions = await _modrinth.GetVersionsAsync(project.ProjectId, cancellationToken)
                .ConfigureAwait(false);

            var result = new List<DownloadVersion>();
            foreach (var version in versions)
            {
                var file = SelectFile(version.Files, project.Type);
                if (file is null) continue;

                var compatible = IsCompatible(version.GameVersions, version.Loaders, project.Type, mcVersion, loader);
                result.Add(new DownloadVersion
                {
                    Source = DownloadSource.Modrinth,
                    ProjectId = project.ProjectId,
                    ProjectName = project.Name,
                    VersionId = version.Id,
                    VersionNumber = string.IsNullOrWhiteSpace(version.VersionNumber) ? version.Name : version.VersionNumber,
                    FileName = file.FileName,
                    DownloadUrl = file.Url,
                    Sha1 = file.Sha1,
                    Sha512 = file.Sha512,
                    FileSize = file.Size,
                    GameVersions = version.GameVersions,
                    Loaders = version.Loaders,
                    PublishedAt = version.DatePublished,
                    IsCompatible = compatible,
                    Dependencies = version.Dependencies
                        .Where(dependency => !string.IsNullOrWhiteSpace(dependency.ProjectId))
                        .Select(dependency => dependency.ProjectId!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                });
            }

            var sorted = Sort(result);
            await CacheVersionsAsync(versionCacheKey, sorted, cancellationToken).ConfigureAwait(false);
            return sorted;
        }

        var files = await _curseForge.GetFilesAsync(
                long.Parse(project.ProjectId), mcVersion, loader, cancellationToken)
            .ConfigureAwait(false);

        var targets = new List<DownloadVersion>();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.FileName)) continue;
            if (!MatchesExtension(file.FileName, project.Type)) continue;

            targets.Add(new DownloadVersion
            {
                Source = DownloadSource.CurseForge,
                ProjectId = project.ProjectId,
                ProjectName = project.Name,
                VersionId = file.Id.ToString(),
                VersionNumber = file.DisplayName ?? file.FileName,
                FileName = file.FileName,
                DownloadUrl = file.DownloadUrl,
                Sha1 = file.Sha1,
                FileSize = file.FileLength,
                GameVersions = file.GameVersions,
                IsCompatible = true,
                Dependencies = file.Dependencies.Select(dependency => dependency.ModId.ToString()).ToList(),
            });
        }

        var sortedTargets = Sort(targets);
        await CacheVersionsAsync(versionCacheKey, sortedTargets, cancellationToken).ConfigureAwait(false);
        return sortedTargets;
    }

    private async Task CacheVersionsAsync(
        string key,
        List<DownloadVersion> versions,
        CancellationToken cancellationToken)
    {
        if (_cache is not null)
            await _cache.SetAsync("mod-download", key, versions, VersionCacheTtl, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadVersion version,
        string destinationDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var url = version.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url) && version.Source == DownloadSource.CurseForge)
        {
            if (!long.TryParse(version.ProjectId, out var modId) ||
                !long.TryParse(version.VersionId, out var fileId))
                throw new InvalidOperationException("CurseForge 文件地址不可用，请检查 API Key 或项目权限。");

            url = await _curseForge.GetDownloadUrlAsync(modId, fileId, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("该项目没有可用的下载地址（作者可能关闭了第三方下载）。");

        var fileName = SafeFileName(version.FileName, version.Source == DownloadSource.CurseForge ? "mod.jar" : "download.jar");
        var path = Path.Combine(destinationDirectory, fileName);
        return await DownloadUrlAsync(
                url, path, version.Sha1, version.Sha512, version.FileSize, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DownloadResult> DownloadUrlAsync(
        string url,
        string destinationPath,
        string? expectedSha1,
        string? expectedSha512,
        long expectedSize,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? mirrorUrl = null)
    {
        var result = await _resumable.DownloadAsync(new DownloadFileRequest
        {
            Url = url,
            MirrorUrls = string.IsNullOrWhiteSpace(mirrorUrl) ? [] : [mirrorUrl],
            DestinationPath = destinationPath,
            ExpectedSha1 = expectedSha1,
            ExpectedSha512 = expectedSha512,
            ExpectedSize = expectedSize,
        }, progress, cancellationToken).ConfigureAwait(false);

        return new DownloadResult
        {
            FilePath = result.FilePath,
            FileName = Path.GetFileName(result.FilePath),
            Sha1 = result.Sha1,
            Bytes = result.Bytes,
        };
    }

    private static ModrinthFileInfo? SelectFile(ICollection<ModrinthFileInfo> files, DownloadProjectType type)
    {
        var primary = files.FirstOrDefault(file =>
            file.IsPrimary && MatchesExtension(file.FileName, type));
        return primary ?? files.FirstOrDefault(file => MatchesExtension(file.FileName, type));
    }

    private static bool MatchesExtension(string fileName, DownloadProjectType type) =>
        type == DownloadProjectType.Modpack
            ? fileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
              fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            : fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompatible(
        IReadOnlyCollection<string> gameVersions,
        IReadOnlyCollection<string> loaders,
        DownloadProjectType type,
        string? mcVersion,
        LoaderKind loader)
    {
        if (type == DownloadProjectType.Modpack) return true;
        if (string.IsNullOrWhiteSpace(mcVersion) || loader == LoaderKind.Unknown) return true;
        if (gameVersions.Count > 0 && !gameVersions.Contains(mcVersion, StringComparer.OrdinalIgnoreCase))
            return false;

        var loaderKey = LoaderKey(loader);
        if (loaders.Count > 0 && loaderKey is not null &&
            !loaders.Contains(loaderKey, StringComparer.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static string? LoaderKey(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => null,
    };

    private static List<DownloadVersion> Sort(List<DownloadVersion> versions) =>
        versions
            .OrderByDescending(version => version.IsCompatible)
            .ThenByDescending(version => version.PublishedAt)
            .ThenByDescending(version => version.VersionNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string SafeFileName(string? fileName, string fallback)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (name.Length == 0) name = fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
