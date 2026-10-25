using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Scanning;

namespace PackDepTool.App.ViewModels;

public sealed class InstanceManagerResult
{
    public string? VersionsDirectory { get; init; }
    public string? InstancePath { get; init; }
}

/// <summary>Browse a launcher versions folder, inspect instances and switch the active one.</summary>
public sealed partial class InstanceManagerViewModel : ObservableObject
{
    private readonly InstanceCatalogService _catalog;
    private readonly PclLocatorService _pclLocator;
    private readonly Func<Task<string?>> _pickFolder;
    private readonly Func<IReadOnlyList<PclGameDirectoryCandidate>, Task<PclGameDirectoryCandidate?>> _pickLauncher;

    public InstanceManagerViewModel(
        InstanceCatalogService catalog,
        PclLocatorService pclLocator,
        string? versionsDirectory,
        Func<Task<string?>> pickFolder,
        Func<IReadOnlyList<PclGameDirectoryCandidate>, Task<PclGameDirectoryCandidate?>> pickLauncher)
    {
        _catalog = catalog;
        _pclLocator = pclLocator;
        _pickFolder = pickFolder;
        _pickLauncher = pickLauncher;
        _versionsDirectory = versionsDirectory ?? string.Empty;
    }

    public ObservableCollection<MinecraftInstanceInfo> Instances { get; } = [];

    [ObservableProperty] private string _versionsDirectory;
    [ObservableProperty] private MinecraftInstanceInfo? _selected;
    [ObservableProperty] private string _statusText = "选择或浏览 versions 文件夹";
    [ObservableProperty] private bool _isBusy;

    public bool HasInstances => Instances.Count > 0;
    public bool CanOpen => Selected is not null;

    public event Action<InstanceManagerResult?>? Completed;

    public async Task InitializeAsync() => await ScanAsync();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var folder = await _pickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;
        VersionsDirectory = folder;
        await ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取实例…";
        try
        {
            var result = await _catalog.ScanAsync(VersionsDirectory);
            Instances.Clear();
            if (result.VersionsDirectory is not null)
                VersionsDirectory = result.VersionsDirectory;
            foreach (var instance in result.Instances)
                Instances.Add(instance);

            Selected = Instances.FirstOrDefault();
            OnPropertyChanged(nameof(HasInstances));
            OnPropertyChanged(nameof(CanOpen));
            StatusText = result.VersionsDirectory is null
                ? result.Warnings.FirstOrDefault() ?? "没有找到 instances。"
                : Instances.Count == 0
                    ? $"未找到实例：{result.VersionsDirectory}"
                    : $"共 {Instances.Count} 个实例 · {result.VersionsDirectory}";
            if (result.Warnings.Count > 0 && Instances.Count > 0)
                StatusText += $"（{result.Warnings.Count} 个目录读取失败）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"读取实例失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanLaunchersAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在扫描启动器目录…";
        try
        {
            var result = await _pclLocator.ScanAsync();
            var candidates = result.Candidates
                .Where(candidate => candidate.VersionCount > 0)
                .ToList();
            if (candidates.Count == 0)
            {
                StatusText = result.Warnings.FirstOrDefault() ?? "没有找到可用的启动器游戏目录。";
                return;
            }

            var selected = candidates.Count == 1
                ? candidates[0]
                : await _pickLauncher(candidates);
            if (selected is null) return;

            VersionsDirectory = selected.Path;
            await ScanAsync();
            StatusText = $"已载入 {selected.Display}：{StatusText}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"扫描启动器失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Open()
    {
        if (Selected is null) return;
        Completed?.Invoke(new InstanceManagerResult
        {
            VersionsDirectory = InstanceCatalogService.ResolveVersionsDirectory(VersionsDirectory),
            InstancePath = Selected.EffectiveRoot,
        });
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);

    partial void OnSelectedChanged(MinecraftInstanceInfo? value) =>
        OnPropertyChanged(nameof(CanOpen));
}
