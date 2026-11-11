namespace PackDepTool.App.Services;

/// <summary>
/// Watches only a pack's mods/config trees and emits a debounced change signal.
/// It never rescans by itself; the UI decides when a manual rescan is safe.
/// </summary>
public sealed class FileChangeMonitor : IDisposable
{
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private string _root = string.Empty;

    public event Action? Changed;

    public void Watch(string packRoot)
    {
        Stop();
        if (!Directory.Exists(packRoot)) return;

        _root = Path.GetFullPath(packRoot);
        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;
            _watcher.Error += (_, _) => Signal();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            _debounce?.Dispose();
            _debounce = null;
            _root = string.Empty;
        }
    }

    public void Dispose() => Stop();

    private void OnFileSystemEvent(object sender, FileSystemEventArgs args)
    {
        if (!IsRelevant(args.FullPath)) return;
        Signal();
    }

    private void OnFileSystemEvent(object sender, RenamedEventArgs args)
    {
        if (!IsRelevant(args.FullPath) && !IsRelevant(args.OldFullPath)) return;
        Signal();
    }

    private bool IsRelevant(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(_root)) return false;
        var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
        return relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(relative, "mods", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(relative, "config", StringComparison.OrdinalIgnoreCase);
    }

    private void Signal()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Changed?.Invoke(), null, 700, Timeout.Infinite);
        }
    }
}
