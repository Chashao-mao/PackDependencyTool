using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PackDepTool.App.ViewModels;

public sealed partial class LogTailViewModel : ObservableObject
{
    private const int MaxChars = 240_000;

    public LogTailViewModel(string logPath)
    {
        LogPath = logPath;
        Refresh();
    }

    public string LogPath { get; }
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isAutoScroll = true;

    public event Action? CloseRequested;

    public void Refresh()
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                Content = "日志文件不存在。";
                StatusText = LogPath;
                return;
            }

            using var stream = new FileStream(
                LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (text.Length > MaxChars)
                text = "…（只显示末尾内容）\n" + text[^MaxChars..];
            Content = text;
            var info = new FileInfo(LogPath);
            StatusText = $"{info.Length / 1024d:0} KB · {info.LastWriteTime:HH:mm:ss} · 自动刷新";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"读取失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{LogPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            StatusText = $"打开目录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
