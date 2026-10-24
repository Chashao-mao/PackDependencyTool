using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Models;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;
using PackDepTool.Core.Scanning;

namespace PackDepTool.App.ViewModels;

public sealed class LoaderSwitchContext
{
    public string? InstanceDirectory { get; init; }
    public string? MinecraftVersion { get; init; }
    public LoaderKind CurrentLoader { get; init; }
    public string? CurrentLoaderVersion { get; init; }
}

public sealed partial class LoaderSwitchViewModel : ObservableObject
{
    private readonly MinecraftInstallService _installer;
    private readonly PclLocatorService _pclLocator;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Func<Task<string?>> _pickFolder;
    private readonly Func<Task<string?>> _pickJava;
    private readonly Func<IReadOnlyList<PclGameDirectoryCandidate>, Task<PclGameDirectoryCandidate?>> _pickPclDirectory;
    private int _loadId;
    private bool _suppressSave;

    public LoaderSwitchViewModel(
        MinecraftInstallService installer,
        PclLocatorService pclLocator,
        AppSettings settings,
        SettingsStore settingsStore,
        LoaderSwitchContext context,
        Func<Task<string?>> pickFolder,
        Func<Task<string?>> pickJava,
        Func<IReadOnlyList<PclGameDirectoryCandidate>, Task<PclGameDirectoryCandidate?>> pickPclDirectory)
    {
        _installer = installer;
        _pclLocator = pclLocator;
        _settings = settings;
        _settingsStore = settingsStore;
        _pickFolder = pickFolder;
        _pickJava = pickJava;
        _pickPclDirectory = pickPclDirectory;

        Loaders =
        [
            new(GameLoader.Vanilla, "原版 Vanilla（移除加载器）"),
            new(GameLoader.Fabric, "Fabric"),
            new(GameLoader.Quilt, "Quilt"),
            new(GameLoader.Forge, "Forge"),
            new(GameLoader.NeoForge, "NeoForge"),
        ];

        _suppressSave = true;
        _instanceDirectory = !string.IsNullOrWhiteSpace(context.InstanceDirectory)
            ? context.InstanceDirectory!
            : (settings.LoaderSwitchInstanceDirectory ?? string.Empty);
        _minecraftVersion = context.MinecraftVersion ?? string.Empty;
        _currentInfo = context.MinecraftVersion is null
            ? (string.IsNullOrWhiteSpace(_instanceDirectory)
                ? "请选择包含 versions/mmc-pack.json 的实例目录。"
                : $"实例：{_instanceDirectory}")
            : $"Minecraft {context.MinecraftVersion} · 当前 {LoaderName(context.CurrentLoader)}" +
              (string.IsNullOrWhiteSpace(context.CurrentLoaderVersion) ? "" : " " + context.CurrentLoaderVersion);

        _selectedLoader = null;
        _loaderVersionText = string.Empty;
        _showJava = false;
        _javaPath = MinecraftInstallService.DetectJava() ?? string.Empty;
        _suppressSave = false;
    }

    public IReadOnlyList<GameLoaderOption> Loaders { get; }
    public ObservableCollection<LoaderVersionInfo> LoaderVersions { get; } = [];

    [ObservableProperty] private string _instanceDirectory;
    [ObservableProperty] private string _minecraftVersion;
    [ObservableProperty] private string _currentInfo;
    [ObservableProperty] private GameLoaderOption? _selectedLoader;
    [ObservableProperty] private LoaderVersionInfo? _selectedLoaderVersion;
    [ObservableProperty] private string _loaderVersionText = string.Empty;
    [ObservableProperty] private string _javaPath;
    [ObservableProperty] private bool _showJava;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _statusText = "选择目标加载器后会保留 mods/config，只安装缺失的游戏/加载器文件。";

    public event Action<GameInstallResult?>? Completed;

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(InstanceDirectory) && Directory.Exists(InstanceDirectory))
            DetectProfile(InstanceDirectory);
        await LoadLoaderVersionsAsync();
    }

    partial void OnSelectedLoaderVersionChanged(LoaderVersionInfo? value)
    {
        if (!string.IsNullOrWhiteSpace(value?.Version)) LoaderVersionText = value!.Version;
    }

    partial void OnSelectedLoaderChanged(GameLoaderOption? value)
    {
        LoaderVersionText = string.Empty;
        SelectedLoaderVersion = null;
        ShowJava = value?.Value is GameLoader.Forge or GameLoader.NeoForge;
        _ = LoadLoaderVersionsAsync();
    }

    partial void OnInstanceDirectoryChanged(string value) => SaveSettings();

    [RelayCommand]
    private async Task ScanPclDirectoriesAsync()
    {
        IsBusy = true;
        StatusText = "正在扫描启动器目录…";

        try
        {
            var scan = await _pclLocator.ScanAsync();
            if (scan.Candidates.Count == 0)
            {
                StatusText = "没有找到启动器目录。请确认启动器已运行过，或手动选择 .minecraft。";
                return;
            }

            var configured = scan.Candidates.Where(candidate => candidate.IsSelected).ToList();
            PclGameDirectoryCandidate? selected = configured.Count == 1
                ? configured[0]
                : (scan.Candidates.Count == 1
                    ? scan.Candidates[0]
                    : await _pickPclDirectory(scan.Candidates));
            if (selected is null)
            {
                StatusText = "已取消选择启动器目录。";
                return;
            }

            InstanceDirectory = selected.Path;
            DetectProfile(selected.Path);
            await LoadLoaderVersionsAsync();
            StatusText = $"已选择启动器游戏目录：{selected.Path}（{selected.Detail}）";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描启动器目录失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshLoaderVersionsAsync() => LoadLoaderVersionsAsync(forceRefresh: true);

    [RelayCommand]
    private async Task BrowseInstanceAsync()
    {
        var folder = await _pickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;

        InstanceDirectory = folder;
        DetectProfile(folder);
        await LoadLoaderVersionsAsync();
    }

    [RelayCommand]
    private async Task BrowseJavaAsync()
    {
        var java = await _pickJava();
        if (!string.IsNullOrWhiteSpace(java)) JavaPath = java;
    }

    [RelayCommand]
    private void DetectJava()
    {
        var detected = MinecraftInstallService.DetectJava();
        if (string.IsNullOrWhiteSpace(detected))
        {
            StatusText = "没有自动找到 java.exe，请手动选择 Java 21。";
            return;
        }

        JavaPath = detected;
        StatusText = $"已找到 Java：{detected}";
    }

    [RelayCommand]
    private async Task SwitchAsync()
    {
        if (IsInstalling) return;
        if (string.IsNullOrWhiteSpace(InstanceDirectory) || !Directory.Exists(InstanceDirectory))
        {
            StatusText = "实例目录不存在。";
            return;
        }
        if (string.IsNullOrWhiteSpace(MinecraftVersion))
        {
            StatusText = "没有识别到 Minecraft 版本，请选择本工具创建的实例目录。";
            return;
        }
        if (SelectedLoader is null)
        {
            StatusText = "请选择目标加载器。";
            return;
        }

        var loaderVersion = !string.IsNullOrWhiteSpace(LoaderVersionText)
            ? LoaderVersionText.Trim()
            : SelectedLoaderVersion?.Version;
        if (SelectedLoader.Value != GameLoader.Vanilla && string.IsNullOrWhiteSpace(loaderVersion))
        {
            StatusText = "请选择加载器版本，或在「手动版本」中填写。";
            return;
        }
        if (SelectedLoader.Value is GameLoader.Forge or GameLoader.NeoForge &&
            (string.IsNullOrWhiteSpace(JavaPath) || !File.Exists(JavaPath)))
        {
            StatusText = "Forge/NeoForge 需要 Java；可点「自动检测」或手动选择 java.exe。";
            return;
        }

        SaveSettings();
        var request = new GameInstallRequest
        {
            TargetDirectory = InstanceDirectory,
            MinecraftVersion = MinecraftVersion,
            Loader = SelectedLoader.Value,
            LoaderVersion = loaderVersion,
            DownloadAssets = false,
            UseTargetAsInstance = true,
            JavaPath = ShowJava ? JavaPath : null,
            MaxConcurrency = _settings.MaxParallelDownloads,
        };

        IsInstalling = true;
        IsBusy = true;
        ProgressValue = 0;
        StatusText = $"正在切换为 {SelectedLoader.Label}…";

        var progress = new Progress<DownloadProgress>(report =>
        {
            ProgressValue = report.Fraction;
            ProgressText = report.Total > 0
                ? $"{report.Phase} {report.Done}/{report.Total}"
                : report.Phase;
            if (!string.IsNullOrWhiteSpace(report.Message))
                StatusText = $"{report.Phase} · {report.Message}";
        });

        try
        {
            var result = await _installer.SwitchLoaderAsync(InstanceDirectory, request, progress,
                CancellationToken.None);
            StatusText = $"切换完成：{SelectedLoader.Label}";
            Completed?.Invoke(result);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                                       InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            StatusText = $"切换失败：{ex.Message}";
        }
        finally
        {
            IsInstalling = false;
            IsBusy = false;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);

    private void SaveSettings()
    {
        if (_suppressSave) return;

        try
        {
            _settings.LoaderSwitchInstanceDirectory = InstanceDirectory;
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private void DetectProfile(string directory)
    {
        var profile = new PackProfileDetector().Detect(directory);
        if (profile.McVersion != "unknown" && !string.IsNullOrWhiteSpace(profile.McVersion))
            MinecraftVersion = profile.McVersion;

        var detectedLoader = profile.Loader != LoaderKind.Unknown
            ? LoaderName(profile.Loader)
            : "未识别";
        CurrentInfo = $"Minecraft {MinecraftVersion} · 当前 {detectedLoader}" +
                      (string.IsNullOrWhiteSpace(profile.LoaderVersion) ? "" : " " + profile.LoaderVersion);

        // Do not preselect a loader; the user chooses it explicitly to avoid
        // unnecessary metadata requests just by opening the dialog.
    }

    private async Task LoadLoaderVersionsAsync(bool forceRefresh = false)
    {
        var loadId = Interlocked.Increment(ref _loadId);
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        if (SelectedLoader is null || string.IsNullOrWhiteSpace(MinecraftVersion))
        {
            StatusText = "请先选择目标加载器。";
            return;
        }
        if (SelectedLoader.Value == GameLoader.Vanilla)
        {
            StatusText = "原版模式：只安装游戏本体，不安装加载器。";
            return;
        }

        var loaderLabel = SelectedLoader.Label;
        var minecraftVersion = MinecraftVersion;
        IsBusy = true;
        StatusText = $"正在读取 {loaderLabel} 版本…";
        try
        {
            var versions = await _installer.GetLoaderVersionsAsync(
                minecraftVersion, SelectedLoader.Value, CancellationToken.None, forceRefresh);
            if (loadId != _loadId) return;

            foreach (var version in versions) LoaderVersions.Add(version);
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
            if (SelectedLoaderVersion is not null) LoaderVersionText = SelectedLoaderVersion.Version;
            StatusText = versions.Count == 0
                ? $"没有找到 {minecraftVersion} 可用的 {loaderLabel} 版本。"
                : $"找到 {versions.Count} 个 {loaderLabel} 版本；mods/config 会保留。";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"读取 {loaderLabel} 版本超时，请点「刷新」重试或换一个加载器。";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            StatusText = $"读取加载器版本失败：{ex.Message}";
        }
        finally
        {
            if (loadId == _loadId)
            {
                IsBusy = false;
                ProgressText = string.Empty;
            }
        }
    }

    private static GameLoader MapLoader(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => GameLoader.Fabric,
        LoaderKind.Quilt => GameLoader.Quilt,
        LoaderKind.Forge => GameLoader.Forge,
        LoaderKind.NeoForge => GameLoader.NeoForge,
        _ => GameLoader.Vanilla,
    };

    private static string LoaderName(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "未识别",
    };
}
