namespace PackDepTool.Core.Net;

/// <summary>Minimum interval between requests, shared by all callers of one API.</summary>
public sealed class RateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _interval;
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    public RateLimiter(TimeSpan interval) => _interval = interval;

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _next)
                await Task.Delay(_next - now, cancellationToken).ConfigureAwait(false);
            _next = DateTimeOffset.UtcNow + _interval;
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal static class HttpRetry
{
    private const int MaxAttempts = 3;

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        RateLimiter limiter,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts - 1)
            {
                lastException = ex;
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts - 1)
            {
                lastException = ex;
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response is null)
                break;

            if (response.IsSuccessStatusCode)
                return response;

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
            {
                if (attempt >= MaxAttempts - 1)
                    return response;

                var delay = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                if (delay > TimeSpan.FromSeconds(15)) delay = TimeSpan.FromSeconds(15);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return response;
        }

        throw new HttpRequestException("网络请求失败", lastException);
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
}
