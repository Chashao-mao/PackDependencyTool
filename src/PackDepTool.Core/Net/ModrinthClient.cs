using System.Net;
using System.Text;
using System.Text.Json;

namespace PackDepTool.Core.Net;

public sealed class ModrinthApiException : Exception
{
    public ModrinthApiException(int statusCode)
        : base($"Modrinth 请求失败（HTTP {statusCode}）") => StatusCode = statusCode;

    public int StatusCode { get; }
}

/// <summary>
/// Modrinth v2 client. Sends a descriptive User-Agent (required by Modrinth) and respects
/// the documented ~300 requests/minute limit with a conservative 250 ms interval.
/// </summary>
public sealed class ModrinthClient
{
    public const string BaseUrl = "https://api.modrinth.com/v2";

    private readonly HttpClient _http;
    private readonly RateLimiter _limiter;
    private readonly string _userAgent;
    private readonly string _baseUrl;
    private readonly string _cdnBase;

    public ModrinthClient(
        HttpClient http,
        string userAgent,
        RateLimiter? limiter = null,
        string? baseUrl = null,
        string? cdnBase = null)
    {
        _http = http;
        _limiter = limiter ?? new RateLimiter(TimeSpan.FromMilliseconds(250));
        _userAgent = string.IsNullOrWhiteSpace(userAgent)
            ? "PackDependencyTool/1.0.0 (local desktop app)"
            : userAgent;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? BaseUrl : baseUrl.TrimEnd('/');
        _cdnBase = string.IsNullOrWhiteSpace(cdnBase) ? string.Empty : cdnBase.TrimEnd('/');
    }

    private string Cdn(string? url) =>
        string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(_cdnBase)
            ? url ?? string.Empty
            : url.Replace("https://cdn.modrinth.com", _cdnBase, StringComparison.OrdinalIgnoreCase);

    /// <summary>Exact hash lookup: POST /version_files. Returns hash -> version metadata.</summary>
    public async Task<Dictionary<string, ModrinthVersionInfo>> MatchBySha1Async(
        IReadOnlyCollection<string> hashes,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ModrinthVersionInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in Chunk(hashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList(), 100))
        {
            var body = JsonSerializer.Serialize(new { hashes = chunk, algorithm = "sha1" });
            using var doc = await SendAsync(
                () => NewRequest(HttpMethod.Post, $"{_baseUrl}/version_files", body),
                cancellationToken).ConfigureAwait(false);

            if (doc is null) continue;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var version = ParseVersion(property.Value);
                if (version is not null) result[property.Name] = version;
            }
        }

        return result;
    }

    public async Task<List<ModrinthProjectInfo>> GetProjectsAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken = default)
    {
        var projects = new List<ModrinthProjectInfo>();
        foreach (var chunk in Chunk(ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList(), 100))
        {
            var json = JsonSerializer.Serialize(chunk);
            var url = $"{_baseUrl}/projects?ids={Uri.EscapeDataString(json)}";
            using var doc = await SendAsync(() => NewRequest(HttpMethod.Get, url), cancellationToken)
                .ConfigureAwait(false);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) continue;

            foreach (var item in doc.RootElement.EnumerateArray())
                projects.Add(ParseProject(item));
        }

        return projects;
    }

    public Task<List<ModrinthProjectInfo>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default,
        string projectType = "mod") =>
        SearchAsync(query, 0, 20, null, cancellationToken, projectType);

    public async Task<List<ModrinthProjectInfo>> SearchAsync(
        string query,
        int offset,
        int limit,
        string? category,
        CancellationToken cancellationToken = default,
        string projectType = "mod")
    {
        var quote = '"';
        var facets = new List<string> { "[" + quote + "project_type:" + projectType + quote + "]" };
        if (!string.IsNullOrWhiteSpace(category))
            facets.Add("[" + quote + "categories:" + category + quote + "]");
        var encodedFacets = Uri.EscapeDataString("[" + string.Join(",", facets) + "]");
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 100);
        var url = $"{_baseUrl}/search?query={Uri.EscapeDataString(query)}" +
                  $"&offset={safeOffset}&limit={safeLimit}&facets={encodedFacets}";
        using var doc = await SendAsync(() => NewRequest(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        if (doc is null) return [];

        var projects = new List<ModrinthProjectInfo>();
        if (!doc.RootElement.TryGetProperty("hits", out var hits)) return projects;

        foreach (var hit in hits.EnumerateArray())
        {
            var id = GetString(hit, "project_id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            projects.Add(new ModrinthProjectInfo
            {
                Id = id!,
                Slug = GetString(hit, "slug") ?? id!,
                Title = GetString(hit, "title") ?? id!,
                Description = GetString(hit, "description"),
                IconUrl = GetString(hit, "icon_url"),
                Author = GetString(hit, "author"),
                Downloads = GetLong(hit, "downloads"),
                ProjectType = GetString(hit, "project_type") ?? projectType,
            });
        }

        return projects;
    }

    public async Task<List<DownloadCategory>> GetCategoriesAsync(
        string projectType = "mod",
        CancellationToken cancellationToken = default)
    {
        using var doc = await SendAsync(
                () => NewRequest(HttpMethod.Get, $"{_baseUrl}/tag/category"), cancellationToken)
            .ConfigureAwait(false);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var categories = new List<DownloadCategory>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = GetString(item, "project_type");
            if (!string.IsNullOrWhiteSpace(type) &&
                !string.Equals(type, projectType, StringComparison.OrdinalIgnoreCase))
                continue;
            var slug = GetString(item, "name");
            if (string.IsNullOrWhiteSpace(slug)) continue;
            var display = GetString(item, "header");
            categories.Add(new DownloadCategory
            {
                Name = string.IsNullOrWhiteSpace(display) ? slug! : display!,
                ModrinthSlug = slug,
                Type = string.Equals(projectType, "modpack", StringComparison.OrdinalIgnoreCase)
                    ? DownloadProjectType.Modpack
                    : DownloadProjectType.Mod,
            });
        }

        return categories
            .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<List<ModrinthVersionInfo>> GetVersionsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/project/{Uri.EscapeDataString(projectId)}/version";
        using var doc = await SendAsync(() => NewRequest(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var versions = new List<ModrinthVersionInfo>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var version = ParseVersion(item);
            if (version is not null) versions.Add(version);
        }

        return versions;
    }

    /// <summary>Batch SHA-1 lookup used for icon/project resolution without extra searches.</summary>
    public async Task<Dictionary<string, ModrinthVersionInfo>> GetVersionsByHashesAsync(
        IReadOnlyCollection<string> hashes,
        CancellationToken cancellationToken = default)
    {
        var distinct = hashes
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(hash => hash.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return new Dictionary<string, ModrinthVersionInfo>(StringComparer.OrdinalIgnoreCase);

        var body = JsonSerializer.Serialize(new { hashes = distinct, algorithm = "sha1" });
        using var doc = await SendAsync(
                () => NewRequest(HttpMethod.Post, $"{_baseUrl}/version_files", body),
                cancellationToken)
            .ConfigureAwait(false);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, ModrinthVersionInfo>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, ModrinthVersionInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var version = ParseVersion(property.Value);
            if (version is not null) result[property.Name] = version;
        }

        return result;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url, string? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private Task<JsonDocument?> SendAsync(
        Func<HttpRequestMessage> factory,
        CancellationToken cancellationToken) =>
        SendToBaseAsync(factory, cancellationToken);

    private async Task<JsonDocument?> SendToBaseAsync(
        Func<HttpRequestMessage> factory,
        CancellationToken cancellationToken)
    {
        using var response = await HttpRetry.SendAsync(_http, factory, _limiter, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new ModrinthApiException((int)response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private ModrinthVersionInfo? ParseVersion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var projectId = GetString(element, "project_id");
        if (string.IsNullOrWhiteSpace(projectId)) return null;

        var version = new ModrinthVersionInfo
        {
            Id = GetString(element, "id") ?? string.Empty,
            ProjectId = projectId!,
            VersionNumber = GetString(element, "version_number") ?? string.Empty,
            Name = GetString(element, "name") ?? string.Empty,
            DatePublished = GetDateTime(element, "date_published"),
        };

        if (element.TryGetProperty("game_versions", out var gameVersions) &&
            gameVersions.ValueKind == JsonValueKind.Array)
        {
            version.GameVersions = gameVersions.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToList();
        }

        if (element.TryGetProperty("loaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
        {
            version.Loaders = loaders.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToList();
        }

        if (element.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object) continue;
                var url = GetString(file, "url");
                var fileName = GetString(file, "filename");
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(fileName)) continue;

                var info = new ModrinthFileInfo
                {
                    Url = Cdn(url),
                    FileName = fileName!,
                    Size = GetLong(file, "size") ?? 0,
                    IsPrimary = file.TryGetProperty("primary", out var primary) &&
                                primary.ValueKind == JsonValueKind.True,
                };

                if (file.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
                {
                    info.Sha1 = GetString(hashes, "sha1");
                    info.Sha512 = GetString(hashes, "sha512");
                }

                version.Files.Add(info);
            }
        }

        if (element.TryGetProperty("dependencies", out var dependencies) &&
            dependencies.ValueKind == JsonValueKind.Array)
        {
            foreach (var dependency in dependencies.EnumerateArray())
            {
                var type = GetString(dependency, "dependency_type") ?? "required";
                var mapped = type.ToLowerInvariant() switch
                {
                    "required" => Models.EdgeType.Requires,
                    "optional" => Models.EdgeType.Optional,
                    "incompatible" => Models.EdgeType.Incompatible,
                    "embedded" => Models.EdgeType.Embedded,
                    _ => (Models.EdgeType?)null,
                };
                if (mapped is null) continue;

                version.Dependencies.Add(new ModrinthDependencyInfo
                {
                    ProjectId = GetString(dependency, "project_id"),
                    VersionId = GetString(dependency, "version_id"),
                    FileName = GetString(dependency, "file_name"),
                    Type = mapped.Value,
                });
            }
        }

        return version;
    }

    private ModrinthProjectInfo ParseProject(JsonElement element) => new()
    {
        Id = GetString(element, "id") ?? string.Empty,
        Slug = GetString(element, "slug") ?? string.Empty,
        Title = GetString(element, "title") ?? string.Empty,
        Description = GetString(element, "description"),
        IconUrl = Cdn(GetString(element, "icon_url")),
        ProjectType = GetString(element, "project_type"),
    };

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;

    private static DateTimeOffset? GetDateTime(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }
}

public sealed class ModrinthVersionInfo
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string VersionNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> GameVersions { get; set; } = [];
    public List<string> Loaders { get; set; } = [];
    public List<ModrinthFileInfo> Files { get; set; } = [];
    public DateTimeOffset? DatePublished { get; set; }
    public List<ModrinthDependencyInfo> Dependencies { get; set; } = [];
}

public sealed class ModrinthFileInfo
{
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsPrimary { get; set; }
    public string? Sha1 { get; set; }
    public string? Sha512 { get; set; }
}

public sealed class ModrinthDependencyInfo
{
    public string? ProjectId { get; set; }
    public string? VersionId { get; set; }
    public string? FileName { get; set; }
    public Models.EdgeType Type { get; set; } = Models.EdgeType.Requires;
}

public sealed class ModrinthProjectInfo
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? IconUrl { get; set; }
    public string? Author { get; set; }
    public long? Downloads { get; set; }
    public string? ProjectType { get; set; }
}
