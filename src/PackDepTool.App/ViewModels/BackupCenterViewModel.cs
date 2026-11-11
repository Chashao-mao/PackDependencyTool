using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Persistence;

namespace PackDepTool.App.ViewModels;

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

    public BackupCenterViewModel(
        PackBackupService service,
        string packRoot,
        Func<string, string, Task<bool>> confirm)
    {
        _service = service;
        _packRoot = packRoot;
        _confirm = confirm;
        Refresh();
    }

    public ObservableCollection<PackBackupInfo> Backups { get; } = [];
    public ObservableCollection<PackBackupDiffEntry> Diff { get; } = [];

    [ObservableProperty] private PackBackupInfo? _selected;
    [ObservableProperty] private string _summaryText = "选择一个快照查看差异";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canRestore;

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
