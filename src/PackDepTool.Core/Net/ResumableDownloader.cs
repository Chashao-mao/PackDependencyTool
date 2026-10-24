using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace PackDepTool.Core.Net;

public sealed class DownloadFileRequest
{
    public string Url { get; init; } = string.Empty;
    public IReadOnlyList<string> MirrorUrls { get; init; } = [];
    public string DestinationPath { get; init; } = string.Empty;
    public string? ExpectedSha1 { get; init; }
    public string? ExpectedSha512 { get; init; }
    public long ExpectedSize { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public bool AllowResume { get; init; } = true;
    public string? DisplayName { get; init; }
}

public sealed class DownloadFileResult
{
    public string FilePath { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public string Sha1 { get; init; } = string.Empty;
    public string Sha512 { get; init; } = string.Empty;
    public int Attempts { get; init; }
    public bool UsedMirror { get; init; }
}

/// <summary>
/// Resumable file downloader with bounded retries and mirror fallback.
/// The destination is replaced atomically only after hash verification.
/// </summary>
public sealed class ResumableDownloader
{
    public const long MaxDownloadBytes = 512L * 1024 * 1024;
    private const int BufferSize = 128 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly string _userAgent;

    public ResumableDownloader(HttpClient http, string userAgent)
    {
        _http = http;
        _userAgent = userAgent;
    }

    public async Task<DownloadFileResult> DownloadAsync(
        DownloadFileRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var primaryUri) ||
            primaryUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("下载地址不是有效的 HTTPS 链接。");
        if (string.IsNullOrWhiteSpace(request.DestinationPath))
            throw new InvalidOperationException("下载目标路径为空。");

        var destination = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("无效的下载目标路径。");
        Directory.CreateDirectory(directory);

        var gate = PathLocks.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DownloadCoreAsync(request, destination, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DownloadFileResult> DownloadCoreAsync(
        DownloadFileRequest request,
        string destination,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var part = destination + ".part";
        var partState = new PartState { Length = File.Exists(part) ? new FileInfo(part).Length : 0 };
        if (request.ExpectedSize > 0 && partState.Length > request.ExpectedSize)
        {
            TryDelete(part);
            partState.Length = 0;
        }

        if (File.Exists(destination))
        {
            var existingLength = new FileInfo(destination).Length;
            var sizeMatches = request.ExpectedSize <= 0 || existingLength == request.ExpectedSize;
            if (sizeMatches && string.IsNullOrWhiteSpace(request.ExpectedSha1) &&
                string.IsNullOrWhiteSpace(request.ExpectedSha512))
                return new DownloadFileResult
                {
                    FilePath = destination,
                    Bytes = existingLength,
                };

            if (sizeMatches && (!string.IsNullOrWhiteSpace(request.ExpectedSha1) ||
                                !string.IsNullOrWhiteSpace(request.ExpectedSha512)))
            {
                var (existingSha1, existingSha512) =
                    await ComputeHashesAsync(destination, cancellationToken).ConfigureAwait(false);
                var sha1Matches = string.IsNullOrWhiteSpace(request.ExpectedSha1) ||
                                  string.Equals(existingSha1, request.ExpectedSha1.Trim(),
                                      StringComparison.OrdinalIgnoreCase);
                var sha512Matches = string.IsNullOrWhiteSpace(request.ExpectedSha512) ||
                                    string.Equals(existingSha512, request.ExpectedSha512.Trim(),
                                        StringComparison.OrdinalIgnoreCase);
                if (sha1Matches && sha512Matches)
                    return new DownloadFileResult
                    {
                        FilePath = destination,
                        Bytes = existingLength,
                        Sha1 = existingSha1,
                        Sha512 = existingSha512,
                    };
            }

            TryDelete(destination);
        }

        var candidates = new List<Uri> { new(request.Url) };
        foreach (var mirror in request.MirrorUrls)
            if (Uri.TryCreate(mirror, UriKind.Absolute, out var mirrorUri) &&
                mirrorUri.Scheme == Uri.UriSchemeHttps &&
                !candidates.Any(candidate => candidate == mirrorUri))
                candidates.Add(mirrorUri);

        Exception? lastError = null;
        var attempts = 0;
        var usedMirror = false;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            for (var attempt = 0; attempt < Math.Clamp(request.MaxAttempts, 1, 5); attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;
                try
                {
                    var result = await DownloadCandidateAsync(
                        candidate, request, destination, part, partState, progress,
                        candidateIndex > 0, cancellationToken).ConfigureAwait(false);
                    return new DownloadFileResult
                    {
                        FilePath = result.FilePath,
                        Bytes = result.Bytes,
                        Sha1 = result.Sha1,
                        Sha512 = result.Sha512,
                        Attempts = attempts,
                        UsedMirror = candidateIndex > 0,
                    };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    lastError = ex;
                    if (attempt + 1 >= Math.Clamp(request.MaxAttempts, 1, 5)) break;
                    await Task.Delay(Backoff(attempt, ex), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HashMismatchException)
                {
                    lastError = ex;
                    TryDelete(part);
                    partState.Length = 0;
                    if (attempt + 1 >= Math.Clamp(request.MaxAttempts, 1, 5)) break;
                    await Task.Delay(Backoff(attempt, ex), cancellationToken).ConfigureAwait(false);
                }
                catch (NonRetryableHttpException ex)
                {
                    throw new InvalidOperationException(
                        $"下载失败：{Path.GetFileName(destination)}（HTTP {ex.StatusCode}）", ex);
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
                {
                    lastError = ex;
                    break;
                }
            }

            if (candidateIndex + 1 < candidates.Count)
            {
                usedMirror = true;
                progress?.Report(new DownloadProgress("切换镜像源", 0, 0,
                    $"{Path.GetFileName(destination)} ← {candidate.Host}"));
            }
        }

        throw new InvalidOperationException(
            $"下载失败：{Path.GetFileName(destination)}", lastError);
    }

    private async Task<DownloadFileResult> DownloadCandidateAsync(
        Uri uri,
        DownloadFileRequest request,
        string destination,
        string part,
        PartState partState,
        IProgress<DownloadProgress>? progress,
        bool viaMirror,
        CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(TimeSpan.FromMinutes(30));
        var token = attemptCts.Token;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        httpRequest.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        httpRequest.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (request.AllowResume && partState.Length > 0)
            httpRequest.Headers.Range = new RangeHeaderValue(partState.Length, null);

        using var response = await _http.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
            request.ExpectedSize > 0 && partState.Length == request.ExpectedSize)
        {
            return await FinalizeAsync(part, destination, request, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitException(response.Headers.RetryAfter?.Delta);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is >= 400 and < 500 &&
                response.StatusCode is not HttpStatusCode.RequestTimeout)
                throw new NonRetryableHttpException(response.StatusCode);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var append = partState.Length > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append && partState.Length > 0)
        {
            TryDelete(part);
            partState.Length = 0;
        }

        var responseLength = response.Content.Headers.ContentLength ?? 0;
        var totalBytes = responseLength + (append ? partState.Length : 0);
        if (request.ExpectedSize > 0) totalBytes = request.ExpectedSize;
        if (totalBytes > MaxDownloadBytes || partState.Length > MaxDownloadBytes)
            throw new InvalidOperationException("下载文件超过 512 MB 限制。");

        var bytes = partState.Length;
        await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var target = new FileStream(part, append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None, BufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[BufferSize];
            var lastReport = partState.Length;
            while (true)
            {
                var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                bytes += read;
                if (bytes > MaxDownloadBytes)
                    throw new InvalidOperationException("下载文件超过 512 MB 限制。");
                if (bytes - lastReport >= 256 * 1024)
                {
                    lastReport = bytes;
                    progress?.Report(new DownloadProgress("下载文件", 0, 0,
                        Path.GetFileName(destination), bytes, totalBytes));
                }
            }

            await target.FlushAsync(token).ConfigureAwait(false);
        }

        partState.Length = bytes;
        return await FinalizeAsync(part, destination, request, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DownloadFileResult> FinalizeAsync(
        string part,
        string destination,
        DownloadFileRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(part))
            throw new IOException("临时下载文件不存在。");
        var bytes = new FileInfo(part).Length;
        if (request.ExpectedSize > 0 && bytes != request.ExpectedSize)
            throw new HashMismatchException($"文件大小不符：期望 {request.ExpectedSize}，实际 {bytes}");

        var (sha1, sha512) = await ComputeHashesAsync(part, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha1) &&
            !string.Equals(sha1, request.ExpectedSha1.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new HashMismatchException($"SHA-1 校验失败：期望 {request.ExpectedSha1}，实际 {sha1}");
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha512) &&
            !string.Equals(sha512, request.ExpectedSha512.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new HashMismatchException($"SHA-512 校验失败：期望 {request.ExpectedSha512}，实际 {sha512}");

        File.Move(part, destination, overwrite: true);
        progress?.Report(new DownloadProgress("下载完成", 1, 1,
            Path.GetFileName(destination), bytes, bytes));
        return new DownloadFileResult
        {
            FilePath = destination,
            Bytes = bytes,
            Sha1 = sha1,
            Sha512 = sha512,
        };
    }

    private static async Task<(string Sha1, string Sha512)> ComputeHashesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha1.AppendData(buffer, 0, read);
            sha512.AppendData(buffer, 0, read);
        }

        return (Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Convert.ToHexStringLower(sha512.GetHashAndReset()));
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or IOException or TaskCanceledException or RateLimitException;

    private static TimeSpan Backoff(int attempt, Exception exception)
    {
        if (exception is RateLimitException { RetryAfter: { } retryAfter })
            return retryAfter > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : retryAfter;
        return TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class HashMismatchException(string message) : Exception(message);

    private sealed class PartState
    {
        public long Length { get; set; }
    }

    private sealed class NonRetryableHttpException(HttpStatusCode statusCode)
        : Exception($"HTTP {(int)statusCode}")
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }

    private sealed class RateLimitException(TimeSpan? retryAfter)
        : Exception("请求被限流")
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
