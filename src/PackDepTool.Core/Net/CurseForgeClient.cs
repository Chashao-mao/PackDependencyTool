using System.Net;
using System.Text;
using System.Text.Json;
using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;

namespace PackDepTool.Core.Net;

public sealed class CurseForgeApiException : Exception
{
    public CurseForgeApiException(int statusCode)
        : base($"CurseForge 请求失败（HTTP {statusCode}）") => StatusCode = statusCode;

    public int StatusCode { get; }
    public bool IsAuthFailure => StatusCode is 401 or 403;
}

public sealed class MissingApiKeyException : Exception
{
    public MissingApiKeyException() : base("未配置 CurseForge API Key") { }
}

/// <summary>
/// CurseForge v1 client. The API key is read per request from <see cref="ApiKeyProvider"/>
/// and never logged or included in exception messages.
/// </summary>
public sealed class CurseForgeClient
{
    public const string BaseUrl = "https://api.curseforge.com/v1";
    private const int MinecraftGameId = 432;
    private const int ModClassId = 6;
    private const int ModpackClassId = 4471;

    private readonly HttpClient _http;
    private readonly ApiKeyProvider _keys;
    private readonly RateLimiter _limiter;
    private readonly string _userAgent;
    private readonly string _baseUrl;
    private readonly string _cdnBase;
    private readonly bool _sendApiKey;

    public CurseForgeClient(
        HttpClient http,
        ApiKeyProvider keys,
        string userAgent,
        RateLimiter? limiter = null,
        string? baseUrl = null,
        string? cdnBase = null,
        bool sendApiKey = true)
    {
        _http = http;
        _keys = keys;
        _userAgent = userAgent;
        _limiter = limiter ?? new RateLimiter(TimeSpan.FromMilliseconds(300));
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? BaseUrl : baseUrl.TrimEnd('/');
        _cdnBase = string.IsNullOrWhiteSpace(cdnBase) ? string.Empty : cdnBase.TrimEnd('/');
        _sendApiKey = sendApiKey;
    }

    private string Cdn(string? url) =>
        string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(_cdnBase)
            ? url ?? string.Empty
            : url.Replace("https://edge.forgecdn.net", _cdnBase, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> ValidateKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await SearchAsync("minecraft", 1, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CurseForgeApiException ex) when (ex.IsAuthFailure)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds a project by exact slug or normalised title. Returns null when nothing matches;
    /// never guesses based on fuzzy search ranking.
    /// </summary>
    public async Task<CurseForgeModInfo?> FindByIdentityAsync(
        string modId,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var queries = new[] { modId, displayName }
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            var mods = await SearchAsync(query, 20, cancellationToken).ConfigureAwait(false);
            var match = mods.FirstOrDefault(mod =>
                string.Equals(mod.Slug, modId, StringComparison.OrdinalIgnoreCase) ||
                Normalize(mod.Slug) == Normalize(modId) ||
                Normalize(mod.Name) == Normalize(modId) ||
                (displayName is not null && Normalize(mod.Name) == Normalize(displayName)));

            if (match is not null) return match;
        }

        return null;
    }

    public Task<List<CurseForgeModInfo>> SearchAsync(
        string query,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        SearchAsync(query, ModClassId, pageSize, cancellationToken);

    public Task<List<CurseForgeModInfo>> SearchAsync(
        string query,
        int classId,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        SearchAsync(query, classId, pageSize, 0, null, cancellationToken);

    public async Task<List<CurseForgeModInfo>> SearchAsync(
        string query,
        int classId,
        int pageSize,
        int index,
        int? categoryId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/mods/search?gameId={MinecraftGameId}&classId={classId}" +
                  $"&index={Math.Max(0, index)}&pageSize={Math.Clamp(pageSize, 1, 50)}" +
                  $"&searchFilter={Uri.EscapeDataString(query)}";
        if (categoryId is > 0)
            url += $"&categoryId={categoryId.Value}";

        using var doc = await SendAsync(() => NewRequest(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        return doc is null ? [] : ParseMods(doc.RootElement);
    }

    public async Task<List<DownloadCategory>> GetCategoriesAsync(
        int classId,
        DownloadProjectType type,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/categories?gameId={MinecraftGameId}&classId={classId}";
        using var doc = await SendAsync(() => NewRequest(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return [];

        var categories = new List<DownloadCategory>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) ||
                !item.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                !idElement.TryGetInt32(out var id))
                continue;
            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            categories.Add(new DownloadCategory
            {
                Name = name!,
                CurseForgeId = id,
                Type = type,
            });
        }

        return categories
            .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<List<CurseForgeFileInfo>> GetFilesAsync(
        long modId,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/mods/{modId}/files?pageSize=50";
        if (!string.IsNullOrWhiteSpace(gameVersion))
            url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";

        var loaderId = LoaderTypeId(loader);
        if (loaderId is not null)
            url += $"&modLoaderType={loaderId}";

        using var doc = await SendAsync(() => NewRequest(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        return doc is null ? [] : ParseFiles(doc.RootElement);
    }

    public async Task<CurseForgeFileInfo?> GetFileAsync(
        long modId,
        long fileId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/mods/{modId}/files/{fileId}";
        using var doc = await SendAsync(() => NewRequest(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
            return null;

        return ParseFile(data);
    }

    public async Task<string?> GetDownloadUrlAsync(
        long modId,
        long fileId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/mods/{modId}/files/{fileId}/download-url";
        using var doc = await SendAsync(() => NewRequest(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.String)
            return null;

        return Cdn(data.GetString());
    }

    public static int? LoaderTypeId(LoaderKind loader) => loader switch
    {
        LoaderKind.Forge => 1,
        LoaderKind.Fabric => 4,
        LoaderKind.Quilt => 5,
        LoaderKind.NeoForge => 6,
        _ => null,
    };

    public static int ModpackClassIdValue => ModpackClassId;

    public async Task<List<CurseForgeModInfo>> GetModsAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return [];

        var result = new List<CurseForgeModInfo>();
        foreach (var chunk in Chunk(ids.Distinct().ToList(), 50))
        {
            var body = JsonSerializer.Serialize(new { modIds = chunk });
            using var doc = await SendAsync(
                () => NewRequest(HttpMethod.Post, $"{_baseUrl}/mods", body),
                cancellationToken).ConfigureAwait(false);
            if (doc is null) continue;
            result.AddRange(ParseMods(doc.RootElement));
        }

        return result;
    }

    private Task<JsonDocument?> SendAsync(
        Func<HttpRequestMessage> factory,
        CancellationToken cancellationToken) =>
        SendToUrlAsync(factory, cancellationToken);

    private async Task<JsonDocument?> SendToUrlAsync(
        Func<HttpRequestMessage> factory,
        CancellationToken cancellationToken)
    {
        var key = _sendApiKey
            ? await _keys.GetCurseForgeKeyAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (_sendApiKey && string.IsNullOrWhiteSpace(key)) throw new MissingApiKeyException();

        using var response = await HttpRetry.SendAsync(
                _http,
                () =>
                {
                    var request = factory();
                    if (!string.IsNullOrWhiteSpace(key))
                        request.Headers.TryAddWithoutValidation("x-api-key", key);
                    return request;
                },
                _limiter,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new CurseForgeApiException((int)response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
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

    private List<CurseForgeModInfo> ParseMods(JsonElement root)
    {
        var result = new List<CurseForgeModInfo>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = item.TryGetProperty("id", out var idValue) && idValue.TryGetInt64(out var parsedId) ? parsedId : 0;
            if (id == 0) continue;

            var mod = new CurseForgeModInfo
            {
                Id = id,
                Name = GetString(item, "name") ?? string.Empty,
                Slug = GetString(item, "slug") ?? string.Empty,
                Summary = GetString(item, "summary"),
                IconUrl = Cdn(item.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object
                    ? (GetString(logo, "thumbnailUrl") ?? GetString(logo, "url"))
                    : null),
                DownloadCount = item.TryGetProperty("downloadCount", out var downloads) &&
                                downloads.TryGetInt64(out var downloadCount)
                    ? downloadCount
                    : 0,
            };

            if (item.TryGetProperty("latestFiles", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (file.ValueKind == JsonValueKind.Object)
                        mod.LatestFiles.Add(ParseFile(file));
                }
            }

            if (item.TryGetProperty("latestFilesIndexes", out var indexes) && indexes.ValueKind == JsonValueKind.Array)
            {
                foreach (var index in indexes.EnumerateArray())
                {
                    if (index.ValueKind != JsonValueKind.Object) continue;
                    mod.LatestFilesIndexes.Add(new CurseForgeFileIndexInfo
                    {
                        FileId = index.TryGetProperty("fileId", out var fileId) &&
                                 fileId.TryGetInt64(out var parsedFileId)
                            ? parsedFileId
                            : 0,
                        GameVersion = GetString(index, "gameVersion"),
                        ModLoader = index.TryGetProperty("modLoader", out var loader) &&
                                    loader.ValueKind == JsonValueKind.Number
                            ? loader.GetInt32().ToString()
                            : null,
                        FileName = GetString(index, "filename"),
                    });
                }
            }

            result.Add(mod);
        }

        return result;
    }

    private List<CurseForgeFileInfo> ParseFiles(JsonElement root)
    {
        var result = new List<CurseForgeFileInfo>();
        if (!root.TryGetProperty("data", out var data)) return result;

        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object) result.Add(ParseFile(item));
            }
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            result.Add(ParseFile(data));
        }

        return result;
    }

    private CurseForgeFileInfo ParseFile(JsonElement file)
    {
        var info = new CurseForgeFileInfo
        {
            Id = file.TryGetProperty("id", out var fileId) && fileId.TryGetInt64(out var parsedFileId)
                ? parsedFileId
                : 0,
            DisplayName = GetString(file, "displayName"),
            FileName = GetString(file, "fileName"),
            DownloadUrl = Cdn(GetString(file, "downloadUrl")),
            FileLength = file.TryGetProperty("fileLength", out var length) && length.TryGetInt64(out var parsedLength)
                ? parsedLength
                : 0,
        };

        if (file.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Array)
        {
            foreach (var hash in hashes.EnumerateArray())
            {
                if (hash.ValueKind != JsonValueKind.Object) continue;
                if (hash.TryGetProperty("algo", out var algo) && algo.TryGetInt32(out var algorithm) && algorithm == 1)
                    info.Sha1 = GetString(hash, "value");
            }
        }

        if (file.TryGetProperty("gameVersions", out var gameVersions) &&
            gameVersions.ValueKind == JsonValueKind.Array)
        {
            info.GameVersions = gameVersions.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToList();
        }

        if (file.TryGetProperty("dependencies", out var dependencies) &&
            dependencies.ValueKind == JsonValueKind.Array)
        {
            foreach (var dependency in dependencies.EnumerateArray())
            {
                if (!dependency.TryGetProperty("modId", out var modIdValue) ||
                    !modIdValue.TryGetInt64(out var depModId) ||
                    !dependency.TryGetProperty("relationType", out var relationValue) ||
                    !relationValue.TryGetInt32(out var relationType))
                    continue;

                info.Dependencies.Add(new CurseForgeDependencyInfo
                {
                    ModId = depModId,
                    RelationType = relationType,
                });
            }
        }

        return info;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }
}

public sealed class CurseForgeModInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? IconUrl { get; set; }
    public long DownloadCount { get; set; }
    public List<CurseForgeFileInfo> LatestFiles { get; set; } = [];
    public List<CurseForgeFileIndexInfo> LatestFilesIndexes { get; set; } = [];
}

public sealed class CurseForgeFileInfo
{
    public long Id { get; set; }
    public string? DisplayName { get; set; }
    public string? FileName { get; set; }
    public string? DownloadUrl { get; set; }
    public long FileLength { get; set; }
    public string? Sha1 { get; set; }
    public List<string> GameVersions { get; set; } = [];
    public List<CurseForgeDependencyInfo> Dependencies { get; set; } = [];
}

public sealed class CurseForgeFileIndexInfo
{
    public long FileId { get; set; }
    public string? GameVersion { get; set; }
    public string? ModLoader { get; set; }
    public string? FileName { get; set; }
}

public sealed class CurseForgeDependencyInfo
{
    public long ModId { get; set; }
    public int RelationType { get; set; }

    public EdgeType? ToEdgeType() => RelationType switch
    {
        1 => EdgeType.Embedded,
        2 => EdgeType.Optional,
        3 => EdgeType.Requires,
        5 => EdgeType.Incompatible,
        6 => EdgeType.Embedded,
        _ => null,
    };
}
