using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PackDepTool.Core.Persistence;

/// <summary>
/// Small local cache for project/version metadata. Deliberately uses JSON files
/// instead of introducing a database dependency; entries are keyed by a SHA-256
/// hash so arbitrary project ids never become path segments.
/// </summary>
public sealed class ApiCacheStore
{
    private readonly string _root;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ApiCacheStore(string rootDirectory) => _root = Path.GetFullPath(rootDirectory);

    public async Task<T?> GetAsync<T>(
        string source,
        string key,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var path = PathFor(source, key);
        string? text;
        try
        {
            text = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var envelope = JsonSerializer.Deserialize<CacheEnvelope>(text, JsonOptions);
            if (envelope is null || envelope.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                TryDelete(path);
                return null;
            }

            return JsonSerializer.Deserialize<T>(envelope.Payload, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            TryDelete(path);
            return null;
        }
    }

    public async Task SetAsync<T>(
        string source,
        string key,
        T value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var path = PathFor(source, key);
        var payload = JsonSerializer.Serialize(value, JsonOptions);
        var envelope = JsonSerializer.Serialize(new CacheEnvelope(
            DateTimeOffset.UtcNow.Add(ttl), payload), JsonOptions);
        try
        {
            await AtomicFile.WriteTextAsync(path, envelope, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache is best-effort and must never fail a scan or download.
        }
    }

    private string PathFor(string source, string key)
    {
        var safeSource = new string(source
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            .ToArray());
        if (safeSource.Length == 0) safeSource = "api";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_root, safeSource, hash + ".json");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private sealed record CacheEnvelope(DateTimeOffset ExpiresAt, string Payload);
}
