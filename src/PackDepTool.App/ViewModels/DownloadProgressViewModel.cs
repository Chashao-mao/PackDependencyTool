using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Net;

namespace PackDepTool.App.ViewModels;

public sealed record ProgressLogItem(string Time, string Text);

public sealed partial class DownloadProgressViewModel : ObservableObject
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private DateTime _lastSample = DateTime.UtcNow;
    private long _lastBytes;
    private double _smoothedSpeed;
    private string _lastLogged = string.Empty;

    public DownloadProgressViewModel(string title, string subtitle = "")
    {
        _title = title;
        _subtitle = subtitle;
        _phase = "准备下载";
        _status = "正在准备…";
        _bytesText = "0 B";
        _speedText = "—";
        _etaText = "—";
        _elapsedText = "0:00";
    }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _subtitle;
    [ObservableProperty] private string _phase;
    [ObservableProperty] private string _status;
    [ObservableProperty] private string _currentFile = string.Empty;
    [ObservableProperty] private string _bytesText;
    [ObservableProperty] private string _speedText;
    [ObservableProperty] private string _etaText;
    [ObservableProperty] private string _elapsedText;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _isIndeterminate;

    public ObservableCollection<ProgressLogItem> Logs { get; } = [];

    public event Action? CancelRequested;

    public void Update(DownloadProgress report)
    {
        if (!IsRunning) return;

        Phase = report.Phase;
        CurrentFile = report.Message;
        if (report.Total > 0)
            Status = $"{report.Phase} · {report.Done}/{report.Total}";
        else if (!string.IsNullOrWhiteSpace(report.Message))
            Status = $"{report.Phase} · {report.Message}";

        var fraction = report.Fraction;
        if (fraction > 0) ProgressValue = fraction;
        else if (report.Total > 0) ProgressValue = (double)report.Done / report.Total;

        if (report.Bytes > 0 || report.TotalBytes > 0)
        {
            BytesText = report.TotalBytes > 0
                ? $"{FormatBytes(report.Bytes)} / {FormatBytes(report.TotalBytes)}"
                : FormatBytes(report.Bytes);

            var now = DateTime.UtcNow;
            var seconds = Math.Max(0.2, (now - _lastSample).TotalSeconds);
            var instant = (report.Bytes - _lastBytes) / seconds;
            if (instant < 0) instant = 0;
            _smoothedSpeed = _smoothedSpeed <= 0 ? instant : _smoothedSpeed * 0.72 + instant * 0.28;
            _lastSample = now;
            _lastBytes = report.Bytes;

            SpeedText = _smoothedSpeed > 0 ? FormatBytes((long)_smoothedSpeed) + "/s" : "—";
            if (report.TotalBytes > 0 && _smoothedSpeed > 1)
            {
                var remaining = TimeSpan.FromSeconds(
                    Math.Max(0, (report.TotalBytes - report.Bytes) / _smoothedSpeed));
                EtaText = FormatDuration(remaining);
            }
        }

        ElapsedText = FormatDuration(_stopwatch.Elapsed);

        var logText = string.IsNullOrWhiteSpace(report.Message)
            ? report.Phase
            : $"{report.Phase} · {report.Message}";
        if (!string.Equals(logText, _lastLogged, StringComparison.Ordinal))
        {
            _lastLogged = logText;
            AddLog(logText);
        }
    }

    public void Finish(bool success, string? message = null)
    {
        _stopwatch.Stop();
        IsRunning = false;
        IsIndeterminate = false;
        ElapsedText = FormatDuration(_stopwatch.Elapsed);
        EtaText = "—";
        SpeedText = "—";

        if (success)
        {
            ProgressValue = 1;
            HasError = false;
            Status = string.IsNullOrWhiteSpace(message) ? "下载完成" : message!;
            Phase = "完成";
            AddLog("✔ " + Status);
        }
        else
        {
            HasError = true;
            Status = string.IsNullOrWhiteSpace(message) ? "下载失败" : message!;
            Phase = "失败";
            AddLog("✖ " + Status);
        }
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke();

    private void AddLog(string text)
    {
        Logs.Add(new ProgressLogItem(DateTime.Now.ToString("HH:mm:ss"), text));
        while (Logs.Count > 200) Logs.RemoveAt(0);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024d:0.#} MB";
        return $"{bytes / 1024d / 1024d / 1024d:0.##} GB";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1) return "0s";
        if (duration.TotalMinutes < 1) return $"{duration.TotalSeconds:0}s";
        if (duration.TotalHours < 1) return $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
        return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
