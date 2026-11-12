using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Net;

namespace PackDepTool.App.ViewModels;

public sealed class ConsoleLogLine
{
    public string Time { get; init; } = string.Empty;
    public string Thread { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IBrush Brush { get; init; } = Brushes.Gray;
    public IBrush RowBackground { get; init; } = Brushes.Transparent;
    public IBrush BadgeBackground { get; init; } = Brushes.Transparent;
    public bool HasLevel => Level.Length > 0;
    public bool IsContinuation => Level.Length == 0;
    public string DisplayMessage => IsContinuation ? "    " + Message : Message;
}

public sealed partial class QueueItemViewModel : ObservableObject
{
    private readonly DownloadQueueService _queue;
    private readonly Action _refresh;

    public QueueItemViewModel(DownloadQueueItem item, DownloadQueueService queue, Action refresh)
    {
        _queue = queue;
        _refresh = refresh;
        Item = item;
    }

    public DownloadQueueItem Item { get; }
    public string Title => Item.Title;
    public string Detail => Item.Detail ?? Item.Request.DisplayName;
    public string StateText => Item.State switch
    {
        DownloadQueueState.Queued => "排队中",
        DownloadQueueState.Running => "下载中",
        DownloadQueueState.Paused => "已暂停",
        DownloadQueueState.Completed => "已完成",
        DownloadQueueState.Failed => "失败",
        DownloadQueueState.Cancelled => "已取消",
        _ => Item.State.ToString(),
    };
    public double Progress => Item.Fraction;
    public string ProgressText => Item.State == DownloadQueueState.Running
        ? $"{Item.Fraction * 100:0}%"
        : string.Empty;
    public string Error => Item.Error ?? string.Empty;

    public IBrush StateBrush => Item.State switch
    {
        DownloadQueueState.Completed => new SolidColorBrush(Color.Parse("#249B69")),
        DownloadQueueState.Failed => new SolidColorBrush(Color.Parse("#D94A4A")),
        DownloadQueueState.Running => new SolidColorBrush(Color.Parse("#2878D0")),
        DownloadQueueState.Paused => new SolidColorBrush(Color.Parse("#D99024")),
        _ => new SolidColorBrush(Color.Parse("#748197")),
    };

    [RelayCommand]
    private void PauseOrResume()
    {
        if (Item.State == DownloadQueueState.Running) _queue.Pause(Item);
        else if (Item.State is DownloadQueueState.Paused or DownloadQueueState.Queued) _queue.Resume(Item);
        _refresh();
    }

    [RelayCommand]
    private void Cancel()
    {
        _queue.Cancel(Item);
        _refresh();
    }
}

public sealed partial class RuntimeConsoleViewModel : ObservableObject, IDisposable
{
    private static readonly Regex LogPattern = new(
        @"^\[(?<time>[^\]]+)\]\s+\[(?<thread>[^/]+)/(?<level>[A-Z]+)\]:?\s?(?<message>.*)$",
        RegexOptions.Compiled);

    private readonly DownloadQueueService _queue;
    private readonly List<ConsoleLogLine> _allLines = [];
    private string? _logPath;
    private bool _disposed;

    public RuntimeConsoleViewModel(DownloadQueueService queue)
    {
        _queue = queue;
        _queue.Changed += OnQueueChanged;
        LevelFilters = ["全部等级", "INFO", "WARN", "ERROR"];
        RefreshDownloads();
    }

    public ObservableCollection<ConsoleLogLine> LogLines { get; } = [];
    public ObservableCollection<QueueItemViewModel> Downloads { get; } = [];
    public IReadOnlyList<string> LevelFilters { get; }
    [ObservableProperty] private string _selectedLevel = "全部等级";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _logStatus = "尚未打开实例日志";
    [ObservableProperty] private string _queueStatus = "没有下载任务";
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _collapseStackTraces = true;
    [ObservableProperty] private string _levelSummary = string.Empty;
    [ObservableProperty] private int _tabIndex;

    public string LogPath => _logPath ?? string.Empty;

    public void SetLogPath(string? path)
    {
        _logPath = path;
        RefreshLog();
    }

    public void RefreshLog()
    {
        if (string.IsNullOrWhiteSpace(_logPath))
        {
            LogStatus = "尚未定位 logs/latest.log";
            return;
        }
        if (!File.Exists(_logPath))
        {
            LogStatus = "当前实例还没有 logs/latest.log";
            return;
        }

        try
        {
            using var stream = new FileStream(
                _logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var text = reader.ReadToEnd();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length > 1200) lines = lines[^1200..];
            _allLines.Clear();
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                _allLines.Add(Parse(raw));
            }
            LevelSummary = $"INFO {_allLines.Count(line => line.Level == "INFO")} · " +
                           $"WARN {_allLines.Count(line => line.Level == "WARN")} · " +
                           $"ERROR {_allLines.Count(line => line.Level is "ERROR" or "FATAL")}";
            ApplyLogFilter();
            var info = new FileInfo(_logPath);
            LogStatus = $"{info.Length / 1024d:0.#} KB · {info.LastWriteTime:HH:mm:ss} · 自动刷新";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStatus = $"读取日志失败：{ex.Message}";
        }
    }

    private void ApplyLogFilter()
    {
        var level = SelectedLevel;
        var search = SearchText.Trim();
        LogLines.Clear();
        var hidden = 0;
        foreach (var line in _allLines)
        {
            if (CollapseStackTraces && line.IsContinuation)
            {
                hidden++;
                continue;
            }
            if (level != "全部等级" && !line.Level.Equals(level, StringComparison.OrdinalIgnoreCase)) continue;
            if (search.Length > 0 &&
                !line.Message.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !line.Thread.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            LogLines.Add(line);
        }
        LogStatus = LogStatus.Split(" · 已折叠")[0];
        if (hidden > 0) LogStatus += $" · 已折叠 {hidden} 行堆栈";
    }

    private static ConsoleLogLine Parse(string raw)
    {
        var match = LogPattern.Match(raw);
        if (!match.Success)
        {
            var continuationTime = Regex.Match(raw, @"\d{2}:\d{2}:\d{2}");
            return new ConsoleLogLine
            {
                Time = continuationTime.Success ? continuationTime.Value : "",
                Thread = "",
                Level = "",
                Message = raw,
                Brush = new SolidColorBrush(Color.Parse("#8A97AA")),
            };
        }

        var level = match.Groups["level"].Value.ToUpperInvariant();
        var rawTime = match.Groups["time"].Value;
        var timeMatch = Regex.Match(rawTime, @"\d{2}:\d{2}:\d{2}");
        var time = timeMatch.Success ? timeMatch.Value : rawTime;
        IBrush brush;
        IBrush row;
        IBrush badge;
        switch (level)
        {
            case "ERROR" or "FATAL":
                brush = new SolidColorBrush(Color.Parse("#D94A4A"));
                row = new SolidColorBrush(Color.Parse("#1FD94A4A"));
                badge = new SolidColorBrush(Color.Parse("#33D94A4A"));
                break;
            case "WARN":
                brush = new SolidColorBrush(Color.Parse("#D99024"));
                row = new SolidColorBrush(Color.Parse("#1AD99024"));
                badge = new SolidColorBrush(Color.Parse("#33D99024"));
                break;
            case "DEBUG" or "TRACE":
                brush = new SolidColorBrush(Color.Parse("#8A97AA"));
                row = Brushes.Transparent;
                badge = new SolidColorBrush(Color.Parse("#148A97AA"));
                break;
            default:
                brush = new SolidColorBrush(Color.Parse("#4D5B70"));
                row = Brushes.Transparent;
                badge = new SolidColorBrush(Color.Parse("#124D5B70"));
                break;
        }
        return new ConsoleLogLine
        {
            Time = time,
            Thread = match.Groups["thread"].Value,
            Level = level,
            Message = match.Groups["message"].Value,
            Brush = brush,
            RowBackground = row,
            BadgeBackground = badge,
        };
    }

    private void OnQueueChanged() =>
        Dispatcher.UIThread.Post(RefreshDownloads);

    public void RefreshDownloads()
    {
        var items = _queue.Snapshot();
        var existing = Downloads.ToDictionary(item => item.Item.Id);
        Downloads.Clear();
        foreach (var item in items.OrderBy(item => item.State == DownloadQueueState.Completed))
        {
            if (existing.TryGetValue(item.Id, out var vm))
            {
                // Recreate so state/progress bindings refresh; the queue itself owns the task.
                vm = new QueueItemViewModel(item, _queue, RefreshDownloads);
            }
            else
            {
                vm = new QueueItemViewModel(item, _queue, RefreshDownloads);
            }
            Downloads.Add(vm);
        }
        QueueStatus = items.Count == 0
            ? "没有下载任务"
            : $"{items.Count(item => item.State == DownloadQueueState.Running)} 个下载中 · " +
              $"{items.Count(item => item.State == DownloadQueueState.Completed)} 个完成 · " +
              $"{items.Count(item => item.State == DownloadQueueState.Failed)} 个失败";
    }

    partial void OnSelectedLevelChanged(string value) => ApplyLogFilter();
    partial void OnSearchTextChanged(string value) => ApplyLogFilter();
    partial void OnCollapseStackTracesChanged(bool value) => ApplyLogFilter();

    [RelayCommand]
    private void ClearLog()
    {
        _allLines.Clear();
        LogLines.Clear();
        LogStatus = "已清空控制台显示（不会删除日志文件）";
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (string.IsNullOrWhiteSpace(_logPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(_logPath) ? $"/select,\"{_logPath}\"" : Path.GetDirectoryName(_logPath)!,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            LogStatus = ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Changed -= OnQueueChanged;
    }
}
