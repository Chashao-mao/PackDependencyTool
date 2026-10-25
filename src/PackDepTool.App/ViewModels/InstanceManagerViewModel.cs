using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Scanning;

namespace PackDepTool.App.ViewModels;

public sealed class InstanceManagerResult
{
    public string? VersionsDirectory { get; init; }
    public IReadOnlyList<string> VersionsDirectories { get; init; } = [];
    public string? InstancePath { get; init; }
}

public sealed partial class VersionsFolderItem : ObservableObject
{
    public VersionsFolderItem(string path)
    {
        Path = path;
        DisplayName = System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = path;
    }

    public string Path { get; }
    public string DisplayName { get; }

    [ObservableProperty] private int _instanceCount;
    public string Detail => $"{InstanceCount} 个实例 · {Path}";
}

/// <summary>PCL-style versions folder list, instance inspection and active-instance switching.</summary>
public sealed partial class InstanceManagerViewModel : ObservableObject
{
    private readonly InstanceCatalogService _catalog;
    private readonly PclLocatorService _pclLocator;
    private readonly Func<Task<string?>> _pickFolder;
    private readonly Func<IReadOnlyList<PclGameDirectoryCandidate>, Task<PclGameDirectoryCandidate?>> _pickLauncher;
    private bool _initializing;
    private bool _suppressFolderAutoScan;
    private int _scanVersion;

    public InstanceManagerViewModel(
        InstanceCatalogService catalog,
        PclLocatorService pclLocator,
        IReadOnlyList<string> versionsDirectories,
        string? lastVersionsDirectory,
        Func<Task<string?>> pickFolder,
        Func<IReadOnlyList<PclGameDirectoryCandidate>, Task<PclGameDirectoryCandidate?>> pickLauncher)
    {
        _catalog = catalog;
        _pclLocator = pclLocator;
        _pickFolder = pickFolder;
        _pickLauncher = pickLauncher;
        _initializing = true;

        foreach (var path in versionsDirectories)
            AddFolder(path);
        if (Folders.Count == 0 && !string.IsNullOrWhiteSpace(lastVersionsDirectory))
            AddFolder(lastVersionsDirectory);

        _selectedFolder = Folders.FirstOrDefault();
        _versionsDirectory = _selectedFolder?.Path ?? string.Empty;
        _initializing = false;
    }

    public ObservableCollection<VersionsFolderItem> Folders { get; } = [];
    public ObservableCollection<MinecraftInstanceInfo> Instances { get; } = [];

    [ObservableProperty] private VersionsFolderItem? _selectedFolder;
    [ObservableProperty] private string _versionsDirectory = string.Empty;
    [ObservableProperty] private MinecraftInstanceInfo? _selected;
    [ObservableProperty] private string _statusText = "添加一个 versions 文件夹，或扫描启动器目录";
    [ObservableProperty] private bool _isBusy;

    public bool HasFolders => Folders.Count > 0;
    public bool HasInstances => Instances.Count > 0;
    public bool CanOpen => Selected is not null;
    public bool CanRemoveFolder => SelectedFolder is not null;

    public event Action<InstanceManagerResult?>? Completed;

    public async Task InitializeAsync() => await ScanSelectedFolderAsync();

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = await _pickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;

        var resolved = InstanceCatalogService.ResolveVersionsDirectory(folder);
        if (resolved is null)
        {
            StatusText = "请选择 versions 文件夹，或包含 versions 的游戏根目录。";
            return;
        }

        var item = AddFolder(resolved);
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(CanRemoveFolder));
        _suppressFolderAutoScan = true;
        SelectedFolder = item;
        _suppressFolderAutoScan = false;
        await ScanSelectedFolderAsync();
    }

    [RelayCommand]
    private void RemoveFolder()
    {
        if (SelectedFolder is null) return;
        var index = Folders.IndexOf(SelectedFolder);
        Folders.Remove(SelectedFolder);
        SelectedFolder = Folders.Count == 0
            ? null
            : Folders[Math.Clamp(index, 0, Folders.Count - 1)];
        if (SelectedFolder is null)
        {
            Instances.Clear();
            Selected = null;
            VersionsDirectory = string.Empty;
            StatusText = "尚未添加 versions 文件夹。";
        }
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(CanRemoveFolder));
        OnPropertyChanged(nameof(HasInstances));
    }

    [RelayCommand]
    private async Task RescanAsync() => await ScanSelectedFolderAsync();

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

            var selected = candidates.Count == 1 ? candidates[0] : await _pickLauncher(candidates);
            if (selected is null) return;

            var folder = InstanceCatalogService.ResolveVersionsDirectory(selected.Path);
            if (folder is null)
            {
                StatusText = $"{selected.Path} 下没有 versions 文件夹。";
                return;
            }

            var item = AddFolder(folder);
            OnPropertyChanged(nameof(HasFolders));
            _suppressFolderAutoScan = true;
            SelectedFolder = item;
            _suppressFolderAutoScan = false;
            await ScanSelectedFolderAsync();
            StatusText = $"已添加 {selected.Display}：{StatusText}";
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
        if (Selected is null || SelectedFolder is null) return;
        Completed?.Invoke(new InstanceManagerResult
        {
            VersionsDirectory = SelectedFolder.Path,
            VersionsDirectories = Folders.Select(folder => folder.Path).ToList(),
            InstancePath = Selected.EffectiveRoot,
        });
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(new InstanceManagerResult
    {
        VersionsDirectory = SelectedFolder?.Path,
        VersionsDirectories = Folders.Select(folder => folder.Path).ToList(),
    });

    private VersionsFolderItem AddFolder(string path)
    {
        var existing = Folders.FirstOrDefault(folder =>
            string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var item = new VersionsFolderItem(path);
        Folders.Add(item);
        return item;
    }

    private async Task ScanSelectedFolderAsync()
    {
        if (SelectedFolder is null) return;
        var version = ++_scanVersion;
        var folder = SelectedFolder;
        IsBusy = true;
        StatusText = "正在读取实例…";
        try
        {
            VersionsDirectory = folder.Path;
            var result = await _catalog.ScanAsync(folder.Path);
            if (version != _scanVersion) return;
            Instances.Clear();
            foreach (var instance in result.Instances)
                Instances.Add(instance);
            folder.InstanceCount = Instances.Count;
            Selected = Instances.FirstOrDefault();
            OnPropertyChanged(nameof(HasInstances));
            OnPropertyChanged(nameof(CanOpen));
            StatusText = Instances.Count == 0
                ? result.Warnings.FirstOrDefault() ?? $"未找到实例：{folder.Path}"
                : $"共 {Instances.Count} 个实例 · {folder.Path}";
            if (result.Warnings.Count > 0 && Instances.Count > 0)
                StatusText += $"（{result.Warnings.Count} 个目录读取失败）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (version == _scanVersion) StatusText = $"读取实例失败：{ex.Message}";
        }
        finally
        {
            if (version == _scanVersion) IsBusy = false;
        }
    }

    partial void OnSelectedChanged(MinecraftInstanceInfo? value) =>
        OnPropertyChanged(nameof(CanOpen));

    partial void OnSelectedFolderChanged(VersionsFolderItem? value)
    {
        OnPropertyChanged(nameof(CanRemoveFolder));
        OnPropertyChanged(nameof(CanOpen));
        if (!_initializing && !_suppressFolderAutoScan && value is not null)
            _ = ScanSelectedFolderAsync();
    }
}
