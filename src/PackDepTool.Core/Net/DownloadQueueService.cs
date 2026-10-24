namespace PackDepTool.Core.Net;

public enum DownloadQueueState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

public sealed class DownloadQueueItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public required DownloadFileRequest Request { get; init; }
    public DownloadQueueState State { get; internal set; } = DownloadQueueState.Queued;
    public DownloadProgress? Progress { get; internal set; }
    public string? Error { get; internal set; }
    public int Attempts { get; internal set; }

    internal CancellationTokenSource? Cts { get; set; }
    internal bool PauseRequested { get; set; }
    internal bool CancelRequested { get; set; }

    public double Fraction => Progress?.Fraction ?? 0;
}

/// <summary>
/// Single-session download queue with bounded concurrency, pause/resume,
/// cancellation and retry. Partial files are preserved by ResumableDownloader.
/// </summary>
public sealed class DownloadQueueService : IDisposable
{
    private readonly ResumableDownloader _downloader;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly List<DownloadQueueItem> _items = [];
    private bool _disposed;

    public DownloadQueueService(ResumableDownloader downloader, int maxConcurrency)
    {
        _downloader = downloader;
        _slots = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 6));
    }

    public event Action? Changed;

    public IReadOnlyList<DownloadQueueItem> Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public DownloadQueueItem Enqueue(
        DownloadFileRequest request,
        string title,
        string? detail = null)
    {
        var item = new DownloadQueueItem
        {
            Title = title,
            Detail = detail,
            Request = request,
        };
        lock (_gate) _items.Add(item);
        RaiseChanged();
        _ = RunAsync(item);
        return item;
    }

    public void Pause(DownloadQueueItem item)
    {
        lock (_gate)
        {
            if (item.State == DownloadQueueState.Queued)
            {
                item.State = DownloadQueueState.Paused;
            }
            else if (item.State == DownloadQueueState.Running)
            {
                item.PauseRequested = true;
                item.Cts?.Cancel();
            }
            else return;
        }
        RaiseChanged();
    }

    public void Resume(DownloadQueueItem item)
    {
        lock (_gate)
        {
            if (item.State != DownloadQueueState.Paused) return;
            item.PauseRequested = false;
            item.State = DownloadQueueState.Queued;
        }
        RaiseChanged();
        _ = RunAsync(item);
    }

    public void Cancel(DownloadQueueItem item)
    {
        lock (_gate)
        {
            item.CancelRequested = true;
            if (item.State == DownloadQueueState.Queued)
                item.State = DownloadQueueState.Cancelled;
            else if (item.State == DownloadQueueState.Running)
                item.Cts?.Cancel();
            else if (item.State == DownloadQueueState.Paused)
                item.State = DownloadQueueState.Cancelled;
            else return;
        }
        RaiseChanged();
    }

    public void Retry(DownloadQueueItem item)
    {
        lock (_gate)
        {
            if (item.State is not (DownloadQueueState.Failed or DownloadQueueState.Cancelled)) return;
            item.Error = null;
            item.CancelRequested = false;
            item.PauseRequested = false;
            item.State = DownloadQueueState.Queued;
        }
        RaiseChanged();
        _ = RunAsync(item);
    }

    public void Remove(DownloadQueueItem item)
    {
        bool removed;
        lock (_gate)
        {
            if (item.State is DownloadQueueState.Running or DownloadQueueState.Queued)
                return;
            removed = _items.Remove(item);
        }
        if (removed) RaiseChanged();
    }

    private async Task RunAsync(DownloadQueueItem item)
    {
        await _slots.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed || item.State != DownloadQueueState.Queued)
                    return;
                item.State = DownloadQueueState.Running;
                item.PauseRequested = false;
                item.CancelRequested = false;
                item.Cts = new CancellationTokenSource();
            }
            RaiseChanged();

            var progress = new Progress<DownloadProgress>(value =>
            {
                item.Progress = value;
                RaiseChanged();
            });

            try
            {
                await _downloader.DownloadAsync(item.Request, progress, item.Cts!.Token)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    item.Attempts++;
                    item.Progress = new DownloadProgress("下载完成", 1, 1,
                        item.Request.DisplayName ?? Path.GetFileName(item.Request.DestinationPath),
                        item.Request.ExpectedSize, item.Request.ExpectedSize);
                    item.State = DownloadQueueState.Completed;
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    item.State = item.PauseRequested
                        ? DownloadQueueState.Paused
                        : DownloadQueueState.Cancelled;
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    item.State = DownloadQueueState.Failed;
                    item.Error = ex.Message;
                }
            }
            finally
            {
                item.Cts?.Dispose();
                item.Cts = null;
                RaiseChanged();
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var item in _items)
            {
                item.CancelRequested = true;
                item.Cts?.Cancel();
            }
        }
        _slots.Dispose();
    }
}
