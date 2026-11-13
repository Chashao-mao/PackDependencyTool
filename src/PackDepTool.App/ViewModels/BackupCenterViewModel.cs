using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;

namespace PackDepTool.App.ViewModels;

public sealed class ModListHistoryItem
{
    public ModListHistoryItem(ModListSnapshot snapshot)
    {
        Snapshot = snapshot;
        Display = $"{snapshot.CapturedAt.ToLocalTime():MM-dd HH:mm} · {snapshot.Mods.Count} 个模组";
    }

    public ModListSnapshot Snapshot { get; }
    public string Display { get; }
}

public sealed class BackupCenterResult
{
    public bool NeedsRescan { get; init; }
    public bool ConfigBackedUp { get; init; }
}

public sealed partial class BackupCenterViewModel : ObservableObject
{
    private readonly PackBackupService _service;
    private readonly string _packRoot;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly ModListHistoryService _history;
    private readonly ModListRestoreService _restore = new();
    private readonly Func<ModListSnapshotEntry, Task<string?>>? _onlineRestore;
    private readonly Func<string, string, Task<string?>>? _pickExport;
    private readonly ScanResult? _scan;
    private readonly GraphDocument? _graph;

    public BackupCenterViewModel(
        PackBackupService service,
        string packRoot,
        Func<string, string, Task<bool>> confirm,
        ModListHistoryService? history = null,
        ScanResult? scan = null,
        GraphDocument? graph = null,
        Func<ModListSnapshotEntry, Task<string?>>? onlineRestore = null,
        Func<string, string, Task<string?>>? pickExport = null)
    {
        _service = service;
        _packRoot = packRoot;
        _confirm = confirm;
        _history = history ?? new ModListHistoryService();
        _scan = scan;
        _graph = graph;
        _onlineRestore = onlineRestore;
        _pickExport = pickExport;
        Refresh();
        RefreshHistory();
    }

    public ObservableCollection<PackBackupInfo> Backups { get; } = [];
    public ObservableCollection<PackBackupDiffEntry> Diff { get; } = [];
    public ObservableCollection<ModListHistoryItem> HistorySnapshots { get; } = [];
    public ObservableCollection<ModListChange> HistoryChanges { get; } = [];

    [ObservableProperty] private PackBackupInfo? _selected;
    [ObservableProperty] private string _summaryText = "选择一个快照查看差异";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canRestore;
    [ObservableProperty] private ModListHistoryItem? _selectedHistory;
    [ObservableProperty] private string _historySummary = "选择历史快照查看与当前模组的差异";

    public event Action<BackupCenterResult?>? Completed;

    private void Refresh()
    {
        var selectedId = Selected?.Id;
        Backups.Clear();
        foreach (var snapshot in _service.List(_packRoot))
            Backups.Add(snapshot);
        Selected = Backups.FirstOrDefault(item => item.Id == selectedId) ?? Backups.FirstOrDefault();
        OnPropertyChanged(nameof(HasBackups));
    }

    public bool HasBackups => Backups.Count > 0;

    private void RefreshHistory()
    {
        var selectedTime = SelectedHistory?.Snapshot.CapturedAt;
        HistorySnapshots.Clear();
        foreach (var item in _history.List(_packRoot))
            HistorySnapshots.Add(new ModListHistoryItem(item.Snapshot));
        SelectedHistory = HistorySnapshots.FirstOrDefault(item => item.Snapshot.CapturedAt == selectedTime)
                          ?? HistorySnapshots.FirstOrDefault();
        OnPropertyChanged(nameof(HasHistory));
    }

    public bool HasHistory => HistorySnapshots.Count > 0;

    partial void OnSelectedHistoryChanged(ModListHistoryItem? value)
    {
        HistoryChanges.Clear();
        if (value is null || _scan is null || _graph is null)
        {
            HistorySummary = value is null ? "选择历史快照查看与当前模组的差异" : "重新扫描后才能对比当前列表";
            return;
        }

        var current = _history.Capture(_scan, _graph);
        var changes = _history.Compare(value.Snapshot, current);
        foreach (var change in changes) HistoryChanges.Add(change);
        HistorySummary = changes.Count == 0
            ? "当前模组列表与该快照一致"
            : $"与 {value.Snapshot.CapturedAt.ToLocalTime():MM-dd HH:mm} 相比，{changes.Count} 项变化";
    }

    [RelayCommand]
    private async Task ExportHistoryAsync()
    {
        if (IsBusy || SelectedHistory is null) return;
        if (_pickExport is null)
        {
            StatusText = "导出对话框不可用。";
            return;
        }

        var name = _scan?.Profile.PackName ?? Path.GetFileName(_packRoot);
        var path = await _pickExport("导出模组变更日志",
            $"{name}-changelog-{DateTime.Now:yyyyMMdd-HHmm}.md");
        if (string.IsNullOrWhiteSpace(path)) return;

        var snapshot = SelectedHistory.Snapshot;
        var builder = new StringBuilder();
        builder.AppendLine("# 模组列表变更日志");
        builder.AppendLine();
        builder.AppendLine($"- 实例：{name}");
        builder.AppendLine($"- 对比基准：{snapshot.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        builder.AppendLine($"- 基准模组数：{snapshot.Mods.Count}");
        builder.AppendLine($"- 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine($"- 变更数：{HistoryChanges.Count}");
        builder.AppendLine();
        if (HistoryChanges.Count == 0)
        {
            builder.AppendLine("当前模组列表与该快照一致。");
        }
        else
        {
            builder.AppendLine("| 类型 | 模组 | modId | 变更前 | 变更后 |");
            builder.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var change in HistoryChanges)
                builder.AppendLine(
                    $"| {change.KindText} | {Escape(change.Name)} | {Escape(change.ModId)} | " +
                    $"{Escape(change.Before)} | {Escape(change.After)} |");
        }
        builder.AppendLine();
        builder.AppendLine("由 Pack Dependency Tool 生成。");

        try
        {
            await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8);
            StatusText = $"变更日志已导出：{path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|").Replace(Environment.NewLine, " ");

    [RelayCommand]
    private async Task RestoreHistoryAsync()
    {
        if (IsBusy || SelectedHistory is null) return;
        var snapshot = SelectedHistory.Snapshot;
        if (!await _confirm(
                "还原模组列表",
                $"把当前 mods 还原到 {snapshot.CapturedAt.ToLocalTime():MM-dd HH:mm} 的 {snapshot.Mods.Count} 个模组？{Environment.NewLine}{Environment.NewLine}" +
                "只使用本地现成 JAR（mods / .packdep-trash / 修复快照）按 SHA1 匹配；" +
                "找不到的旧版本会明确列出，不会联网乱下。当前多余模组会移到 .packdep-trash。"))
            return;

        IsBusy = true;
        StatusText = "正在还原模组列表…";
        try
        {
            var result = await _restore.RestoreAsync(_packRoot, snapshot);
            StatusText = result.Summary;
            if (result.MissingEntries.Count > 0 && _onlineRestore is not null &&
                await _confirm("联网补齐缺失模组",
                    $"本地找不到 {result.MissingEntries.Count} 个旧 JAR。{Environment.NewLine}{Environment.NewLine}" +
                    "将用历史条目的 SHA1 / 版本号到模组站精确匹配并下载；匹配不到就跳过，不会下最新版。" +
                    $"{Environment.NewLine}{Environment.NewLine}是否联网补齐？"))
            {
                var online = 0;
                var onlineFailed = new List<string>();
                foreach (var entry in result.MissingEntries)
                {
                    try
                    {
                        if (await _onlineRestore(entry) is not null) online++;
                        else onlineFailed.Add(entry.Name);
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
                    {
                        onlineFailed.Add($"{entry.Name}（{ex.Message}）");
                    }
                }
                StatusText = $"{result.Summary}；联网补齐 {online} 个" +
                             (onlineFailed.Count > 0 ? $"，{onlineFailed.Count} 个失败" : string.Empty);
                if (onlineFailed.Count > 0)
                    await _confirm("联网补齐结果",
                        string.Join(Environment.NewLine, onlineFailed.Take(15)));
            }
            if (result.Missing.Count > 0 || result.Warnings.Count > 0)
            {
                var detail = string.Join(Environment.NewLine,
                    result.Missing.Select(item => "缺失：" + item)
                        .Concat(result.Warnings.Select(item => "警告：" + item))
                        .Take(20));
                await _confirm("还原完成", result.Summary + Environment.NewLine + Environment.NewLine + detail);
            }
            Completed?.Invoke(new BackupCenterResult { NeedsRescan = true });
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"还原失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedChanged(PackBackupInfo? value)
    {
        CanRestore = value is not null;
        Diff.Clear();
        if (value is null)
        {
            SummaryText = "选择一个快照查看差异";
            return;
        }

        try
        {
            var diff = _service.Diff(_packRoot, value);
            foreach (var entry in diff.Entries)
                Diff.Add(entry);
            SummaryText = diff.Summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SummaryText = $"读取差异失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task BackupConfigAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var info = _service.CreateConfigSnapshot(_packRoot);
            StatusText = info is null ? "没有可备份的 config 文件。" : $"已备份 config：{info.Detail}";
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"配置备份失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (IsBusy || Selected is null) return;
        var snapshot = Selected;
        if (!await _confirm(
                "恢复备份",
                $"恢复「{snapshot.Title}」？{Environment.NewLine}{Environment.NewLine}" +
                $"快照：{snapshot.Detail}{Environment.NewLine}当前文件会被快照覆盖。"))
            return;

        IsBusy = true;
        try
        {
            var result = _service.Restore(_packRoot, snapshot);
            StatusText = $"已恢复 {result.RestoredFiles} 个文件" +
                         (result.MissingFiles > 0 ? $"，{result.MissingFiles} 个备份文件缺失" : string.Empty);
            OnSelectedChanged(snapshot);
            Completed?.Invoke(new BackupCenterResult { NeedsRescan = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"恢复失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (IsBusy || Selected is null) return;
        var snapshot = Selected;
        if (!await _confirm(
                "删除备份",
                $"删除「{snapshot.Title}」快照？{Environment.NewLine}此操作只删除备份，不会修改游戏文件。"))
            return;

        try
        {
            _service.Delete(snapshot);
            StatusText = "已删除快照。";
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"删除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void Close() => Completed?.Invoke(null);
}
