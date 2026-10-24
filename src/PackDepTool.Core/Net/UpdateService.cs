using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackDepTool.Core.Net;

public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("releaseUrl")] public string? ReleaseUrl { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class UpdateCheckResult
{
    public bool HasUpdate { get; init; }
    public bool Succeeded { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? DownloadUrl { get; init; }
    public string? Sha256 { get; init; }
    public string? Notes { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Checks a repository-hosted update.json. This is intentionally a check-only
/// service: it never downloads or executes a replacement binary automatically.
/// </summary>
public sealed class UpdateService
{
    private readonly HttpClient _http;
    private readonly string _userAgent;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public UpdateService(HttpClient http, string userAgent)
    {
        _http = http;
        _userAgent = userAgent;
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string? feedUrl,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl) ||
            !Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return new UpdateCheckResult { Succeeded = false, Error = "未配置有效的 HTTPS 更新源。" };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult { Succeeded = false, Error = "更新清单不存在（HTTP 404）。" };
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult
                {
                    Succeeded = false,
                    Error = $"更新检查失败（HTTP {(int)response.StatusCode}）。",
                };

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length > 256 * 1024)
                return new UpdateCheckResult { Succeeded = false, Error = "更新清单过大。" };

            var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, JsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
                return new UpdateCheckResult { Succeeded = false, Error = "更新清单格式无效。" };

            var hasUpdate = SemanticVersion.TryParse(manifest.Version, out var latest) &&
                            SemanticVersion.TryParse(currentVersion, out var current) &&
                            latest.CompareTo(current) > 0;
            return new UpdateCheckResult
            {
                Succeeded = true,
                HasUpdate = hasUpdate,
                LatestVersion = manifest.Version.Trim(),
                ReleaseUrl = manifest.ReleaseUrl,
                DownloadUrl = manifest.DownloadUrl,
                Sha256 = manifest.Sha256,
                Notes = manifest.Notes,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult { Succeeded = false, Error = "更新检查超时。" };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return new UpdateCheckResult { Succeeded = false, Error = "无法连接更新源。" };
        }
    }

    private readonly record struct SemanticVersion(int Major, int Minor, int Patch, string[] PreRelease)
        : IComparable<SemanticVersion>
    {
        public static bool TryParse(string? text, out SemanticVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var value = text.Trim().TrimStart('v', 'V');
            var buildIndex = value.IndexOf('+');
            if (buildIndex >= 0) value = value[..buildIndex];
            var parts = value.Split('-', 2);
            var numbers = parts[0].Split('.');
            if (numbers.Length < 1 || numbers.Length > 3) return false;
            var values = new int[3];
            for (var index = 0; index < numbers.Length; index++)
                if (!int.TryParse(numbers[index], out values[index]) || values[index] < 0)
                    return false;

            var pre = parts.Length > 1
                ? parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries)
                : [];
            version = new SemanticVersion(values[0], values[1], values[2], pre);
            return true;
        }

        public int CompareTo(SemanticVersion other)
        {
            var core = Major.CompareTo(other.Major);
            if (core != 0) return core;
            core = Minor.CompareTo(other.Minor);
            if (core != 0) return core;
            core = Patch.CompareTo(other.Patch);
            if (core != 0) return core;

            if (PreRelease.Length == 0 && other.PreRelease.Length == 0) return 0;
            if (PreRelease.Length == 0) return 1;
            if (other.PreRelease.Length == 0) return -1;

            var count = Math.Min(PreRelease.Length, other.PreRelease.Length);
            for (var index = 0; index < count; index++)
            {
                var left = PreRelease[index];
                var right = other.PreRelease[index];
                var leftNumber = int.TryParse(left, out var leftValue);
                var rightNumber = int.TryParse(right, out var rightValue);
                var comparison = leftNumber && rightNumber
                    ? leftValue.CompareTo(rightValue)
                    : leftNumber != rightNumber
                        ? (leftNumber ? -1 : 1)
                        : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
                if (comparison != 0) return comparison;
            }

            return PreRelease.Length.CompareTo(other.PreRelease.Length);
        }
    }
}
