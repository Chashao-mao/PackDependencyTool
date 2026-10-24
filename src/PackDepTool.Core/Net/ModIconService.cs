using System.Collections.Concurrent;
using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;

namespace PackDepTool.Core.Net;

public sealed record IconProgress(string Phase, int Done, int Total, string Message = "");

/// <summary>
/// Fetches real project logos from Modrinth / CurseForge for mods that do not ship
/// an icon inside their JAR, and caches them on disk.
/// </summary>
public sealed class ModIconService
{
    private const int MaxIconBytes = 2 * 1024 * 1024;
    private const int MaxSearches = 40;
    private static readonly TimeSpan NegativeCacheLifetime = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly ModrinthClient _modrinth;
    private readonly CurseForgeClient _curseForge;
    private readonly string _userAgent;
    private readonly string _cacheDirectory;

    public ModIconService(
        HttpClient http,
        ModrinthClient modrinth,
        CurseForgeClient curseForge,
        string userAgent,
        string cacheDirectory)
    {
        _http = http;
        _modrinth = modrinth;
        _curseForge = curseForge;
        _userAgent = userAgent;
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>Loads only on-disk cached logos; never touches the network.</summary>
    public int LoadCachedIcons(IEnumerable<ModNode> nodes)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var loaded = 0;
        foreach (var node in nodes)
        {
            var cached = TryReadCache(node);
            if (cached is null) continue;
            node.IconData = cached;
            loaded++;
        }

        return loaded;
    }

    public async Task<int> FetchIconsAsync(
        IReadOnlyCollection<ModNode> nodes,
        IProgress<IconProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var pending = nodes.ToList();
        if (pending.Count == 0) return 0;

        Directory.CreateDirectory(_cacheDirectory);
        var loaded = forceRefresh ? 0 : LoadCachedIcons(pending);
        pending = forceRefresh
            ? pending
            : pending.Where(node => node.IconData is not { Length: > 0 }).ToList();

        if (pending.Count == 0)
        {
            progress?.Report(new IconProgress("图标缓存", loaded, loaded));
            return loaded;
        }

        var negative = forceRefresh ? new Dictionary<string, DateTimeOffset>() : LoadNegativeCache();
        if (!forceRefresh)
        {
            pending = pending.Where(node =>
                !negative.TryGetValue(CacheKey(node), out var timestamp) ||
                DateTimeOffset.UtcNow - timestamp > NegativeCacheLifetime).ToList();
        }

        if (pending.Count == 0)
        {
            progress?.Report(new IconProgress("图标缓存", loaded, loaded));
            return loaded;
        }

        var resolved = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in pending.Where(node => !string.IsNullOrWhiteSpace(node.IconUrl)))
            resolved[node.Id] = node.IconUrl;

        await ResolveByModrinthHashesAsync(pending, resolved, progress, cancellationToken)
            .ConfigureAwait(false);
        await ResolveByCurseForgeIdsAsync(pending, resolved, cancellationToken).ConfigureAwait(false);
        await ResolveBySearchAsync(pending, resolved, progress, cancellationToken).ConfigureAwait(false);

        var downloadTargets = pending
            .Where(node => resolved.TryGetValue(node.Id, out var url) && !string.IsNullOrWhiteSpace(url))
            .ToList();

        foreach (var node in downloadTargets)
        {
            if (resolved.TryGetValue(node.Id, out var url))
                node.IconUrl = url;
        }

        var downloaded = 0;
        var completed = 0;
        await Parallel.ForEachAsync(
            downloadTargets,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 6,
                CancellationToken = cancellationToken,
            },
            async (node, token) =>
            {
                var bytes = await DownloadIconAsync(node.IconUrl, token).ConfigureAwait(false);
                if (bytes is not null)
                {
                    node.IconData = bytes;
                    SaveCache(node, bytes);
                    negative.Remove(CacheKey(node));
                    Interlocked.Increment(ref downloaded);
                }

                var done = Interlocked.Increment(ref completed);
                progress?.Report(new IconProgress(
                    "下载在线图标", done, downloadTargets.Count, node.Name));
            }).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var node in pending)
        {
            var key = CacheKey(node);
            if (node.IconData is { Length: > 0 })
                negative.Remove(key);
            else
                negative[key] = now;
        }

        TryWriteNegativeCache(negative);
        progress?.Report(new IconProgress("在线图标完成", downloaded, downloadTargets.Count));
        return loaded + downloaded;
    }

    private async Task ResolveByModrinthHashesAsync(
        IReadOnlyCollection<ModNode> pending,
        ConcurrentDictionary<string, string?> resolved,
        IProgress<IconProgress>? progress,
        CancellationToken cancellationToken)
    {
        var hashNodes = pending
            .Where(node => string.IsNullOrWhiteSpace(node.IconUrl) &&
                           !string.IsNullOrWhiteSpace(node.Sha1))
            .ToList();
        if (hashNodes.Count == 0) return;

        progress?.Report(new IconProgress("Modrinth 哈希匹配", 0, hashNodes.Count));
        try
        {
            var versions = await _modrinth
                .GetVersionsByHashesAsync(hashNodes.Select(node => node.Sha1!).ToList(), cancellationToken)
                .ConfigureAwait(false);
            if (versions.Count == 0) return;

            var projectIds = versions.Values
                .Select(version => version.ProjectId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var projects = await _modrinth.GetProjectsAsync(projectIds, cancellationToken)
                .ConfigureAwait(false);
            var byId = projects
                .Where(project => !string.IsNullOrWhiteSpace(project.Id))
                .ToDictionary(project => project.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var node in hashNodes)
            {
                if (string.IsNullOrWhiteSpace(node.Sha1)) continue;
                if (!versions.TryGetValue(node.Sha1, out var version)) continue;
                if (!byId.TryGetValue(version.ProjectId, out var project)) continue;
                if (string.IsNullOrWhiteSpace(project.IconUrl)) continue;

                node.IconUrl ??= project.IconUrl;
                node.Slug ??= project.Slug;
                resolved[node.Id] = project.IconUrl;
            }
        }
        catch (Exception ex) when (ex is ModrinthApiException or HttpRequestException)
        {
            // Icons are optional; a failed lookup must not affect analysis.
        }
    }

    private async Task ResolveByCurseForgeIdsAsync(
        IReadOnlyCollection<ModNode> pending,
        ConcurrentDictionary<string, string?> resolved,
        CancellationToken cancellationToken)
    {
        var idNodes = pending
            .Where(node => string.IsNullOrWhiteSpace(node.IconUrl) &&
                           long.TryParse(node.ProjectIds.CurseForge, out _))
            .ToList();
        if (idNodes.Count == 0) return;

        try
        {
            var ids = idNodes
                .Select(node => long.Parse(node.ProjectIds.CurseForge!))
                .Distinct()
                .ToList();
            var mods = await _curseForge.GetModsAsync(ids, cancellationToken).ConfigureAwait(false);
            var byId = mods
                .GroupBy(mod => mod.Id)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (var node in idNodes)
            {
                if (!long.TryParse(node.ProjectIds.CurseForge, out var id)) continue;
                if (!byId.TryGetValue(id, out var mod)) continue;
                if (string.IsNullOrWhiteSpace(mod.IconUrl)) continue;

                node.IconUrl ??= mod.IconUrl;
                node.Slug ??= mod.Slug;
                resolved[node.Id] = mod.IconUrl;
            }
        }
        catch (Exception ex) when (ex is CurseForgeApiException or MissingApiKeyException or HttpRequestException)
        {
            // No key / auth failure: Modrinth logos still apply.
        }
    }

    private async Task ResolveBySearchAsync(
        IReadOnlyCollection<ModNode> pending,
        ConcurrentDictionary<string, string?> resolved,
        IProgress<IconProgress>? progress,
        CancellationToken cancellationToken)
    {
        var candidates = pending
            .Where(node => string.IsNullOrWhiteSpace(node.IconUrl) &&
                           !string.IsNullOrWhiteSpace(node.Name))
            .Take(MaxSearches)
            .ToList();

        var done = 0;
        foreach (var node in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new IconProgress("匹配模组图标", done, candidates.Count, node.Name));

            try
            {
                var query = !string.IsNullOrWhiteSpace(node.Id) ? node.Id : node.Name;
                var hits = await _modrinth.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                var match = hits.FirstOrDefault(hit =>
                    Normalize(hit.Slug) == Normalize(node.Id) ||
                    Normalize(hit.Title) == Normalize(node.Name) ||
                    Normalize(hit.Title) == Normalize(node.Id));
                if (match?.IconUrl is not { Length: > 0 } iconUrl) continue;

                node.IconUrl = iconUrl;
                node.Slug ??= match.Slug;
                resolved[node.Id] = iconUrl;
            }
            catch (Exception ex) when (ex is ModrinthApiException or HttpRequestException)
            {
                if (ex is ModrinthApiException { StatusCode: 429 }) break;
            }
        }
    }

    private async Task<byte[]?> DownloadIconAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.TryAddWithoutValidation("Accept", "image/*");

            using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength > MaxIconBytes) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length is 0 or > MaxIconBytes) return null;
            return IsImage(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    private byte[]? TryReadCache(ModNode node)
    {
        try
        {
            var path = CachePath(node);
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length is 0 or > MaxIconBytes) return null;
            var bytes = File.ReadAllBytes(path);
            return IsImage(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveCache(ModNode node, byte[] bytes)
    {
        try
        {
            var path = CachePath(node);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache is best effort.
        }
    }

    private string CachePath(ModNode node)
    {
        var safe = new string(CacheKey(node)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .ToArray());
        return Path.Combine(_cacheDirectory, safe + ".img");
    }

    private static string CacheKey(ModNode node)
    {
        return !string.IsNullOrWhiteSpace(node.Sha1)
            ? "sha1-" + node.Sha1
            : (!string.IsNullOrWhiteSpace(node.Slug) ? "slug-" + node.Slug : "id-" + node.Id);
    }

    private string NegativeCachePath => Path.Combine(_cacheDirectory, "negative.json");

    private Dictionary<string, DateTimeOffset> LoadNegativeCache()
    {
        try
        {
            if (!File.Exists(NegativeCachePath)) return new(StringComparer.OrdinalIgnoreCase);
            var values = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(NegativeCachePath));
            return values is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DateTimeOffset>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Text.Json.JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void TryWriteNegativeCache(Dictionary<string, DateTimeOffset> values)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var cutoff = DateTimeOffset.UtcNow - NegativeCacheLifetime;
            var pruned = values
                .Where(pair => pair.Value > cutoff)
                .OrderByDescending(pair => pair.Value)
                .Take(2000)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var path = NegativeCachePath;
            var temp = path + ".tmp";
            File.WriteAllText(path + ".tmp",
                System.Text.Json.JsonSerializer.Serialize(pruned));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache is best effort.
        }
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    private static bool IsImage(byte[] bytes)
    {
        if (bytes.Length < 12) return false;
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return true;
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return true;
        return false;
    }
}
