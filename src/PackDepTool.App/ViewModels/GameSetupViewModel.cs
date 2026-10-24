using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;
using PackDepTool.Core.Scanning;
using PackDepTool.App.Views;

namespace PackDepTool.App.ViewModels;

public sealed record GameLoaderOption(GameLoader Value, string Label);

public sealed partial class GameSetupViewModel : ObservableObject
{
    private readonly MinecraftInstallService _installer;
    private readonly PclLocatorService _pclLocator;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Func<Task<string?>> _pickFolder;
    private readonly Func<Task<string?>> _pickJava;
    private readonly Func<IReadOnlyList<PclGameDirectoryCandidate>, Task<PclGameDirectoryCandidate?>> _pickPclDirectory;
    private List<MinecraftVersionInfo> _allVersions = [];
    private int _loaderLoadId;
    private bool _suppressSave;

    public GameSetupViewModel(
        MinecraftInstallService installer,
        PclLocatorService pclLocator,
        AppSettings settings,
        SettingsStore settingsStore,
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
            new(GameLoader.Vanilla, "原版 Vanilla"),
            new(GameLoader.Fabric, "Fabric"),
            new(GameLoader.Quilt, "Quilt"),
            new(GameLoader.Forge, "Forge"),
            new(GameLoader.NeoForge, "NeoForge"),
        ];
        _suppressSave = true;
        _selectedLoader = null;
        _targetDirectory = !string.IsNullOrWhiteSpace(settings.GameInstallTargetDirectory)
            ? settings.GameInstallTargetDirectory!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PackDependencyTool", "Games");
        _onlyReleases = true;
        _javaPath = MinecraftInstallService.DetectJava() ?? string.Empty;
        _showJava = false;
        _downloadAssets = settings.GameInstallDownloadAssets;
        _useTargetAsInstance = settings.GameInstallUseTargetAsInstance;
        _installButtonText = "下载并安装";
        _suppressSave = false;
    }

    public IReadOnlyList<GameLoaderOption> Loaders { get; }
    public ObservableCollection<MinecraftVersionInfo> Versions { get; } = [];
    public ObservableCollection<LoaderVersionInfo> LoaderVersions { get; } = [];

    [ObservableProperty] private string _targetDirectory;
    [ObservableProperty] private string _instanceName = string.Empty;
    [ObservableProperty] private MinecraftVersionInfo? _selectedVersion;
    [ObservableProperty] private GameLoaderOption? _selectedLoader;
    [ObservableProperty] private LoaderVersionInfo? _selectedLoaderVersion;
    [ObservableProperty] private string _loaderVersionText = string.Empty;
    [ObservableProperty] private bool _onlyReleases;
    [ObservableProperty] private bool _downloadAssets;
    [ObservableProperty] private bool _useTargetAsInstance;
    [ObservableProperty] private string _javaPath;
    [ObservableProperty] private bool _showJava;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _statusText = "正在读取 Minecraft 版本清单…";
    [ObservableProperty] private bool _hasLoaderVersions;
    [ObservableProperty] private string _installButtonText;

    public event Action<GameInstallResult?>? Completed;

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            _allVersions = await _installer.GetMinecraftVersionsAsync();
            RefreshVersions();

            SelectedVersion = null;
            SelectedLoader = null;
            LoaderVersionText = string.Empty;
            StatusText = $"已读取 {_allVersions.Count} 个 Minecraft 版本，请选择 Minecraft 版本和加载器。";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            StatusText = $"读取 Mojang 版本清单失败：{ex.Message}";
        }
        finally
        {
            IsInstalling = false;
            IsBusy = false;
            ProgressText = string.Empty;
        }

    }

    partial void OnSelectedVersionChanged(MinecraftVersionInfo? value)
    {
        _ = LoadLoaderVersionsAsync();
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
        InstallButtonText = value?.Value switch
        {
            GameLoader.Vanilla => "下载原版游戏",
            null => "下载并安装",
            _ => "下载并安装",
        };
        _ = LoadLoaderVersionsAsync();
    }

    partial void OnTargetDirectoryChanged(string value) => SaveSettings();
    partial void OnDownloadAssetsChanged(bool value) => SaveSettings();
    partial void OnUseTargetAsInstanceChanged(bool value) => SaveSettings();

    partial void OnOnlyReleasesChanged(bool value) => RefreshVersions();

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
                StatusText = "没有找到启动器目录。请确认启动器已运行过，或手动点「浏览」选择 .minecraft。";
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

            TargetDirectory = selected.Path;
            UseTargetAsInstance = true;
            await LoadLoaderVersionsAsync();
            StatusText = $"已选择启动器游戏目录：{selected.Path}（{selected.Detail}）。安装会直接写入这里，启动器可直接读取。";
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
    private async Task BrowseTargetAsync()
    {
        var folder = await _pickFolder();
        if (!string.IsNullOrWhiteSpace(folder)) TargetDirectory = folder;
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
            StatusText = "没有自动找到 java.exe，请手动选择 Java 21 的 java.exe。";
            return;
        }

        JavaPath = detected;
        StatusText = $"已找到 Java：{detected}";
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsInstalling) return;
        if (SelectedVersion is null)
        {
            StatusText = "请选择 Minecraft 版本。";
            return;
        }

        if (SelectedLoader is null)
        {
            StatusText = "请选择加载器。";
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

        if (string.IsNullOrWhiteSpace(TargetDirectory))
        {
            StatusText = "请选择实例保存目录。";
            return;
        }

        if (SelectedLoader.Value is GameLoader.Forge or GameLoader.NeoForge &&
            (string.IsNullOrWhiteSpace(JavaPath) || !File.Exists(JavaPath)))
        {
            StatusText = "Forge/NeoForge 需要 Java，请选择 java.exe 或点「自动检测」。";
            return;
        }

        SaveSettings();
        var request = new GameInstallRequest
        {
            TargetDirectory = TargetDirectory,
            InstanceName = string.IsNullOrWhiteSpace(InstanceName) ? null : InstanceName.Trim(),
            MinecraftVersion = SelectedVersion.Id,
            Loader = SelectedLoader.Value,
            LoaderVersion = loaderVersion,
            DownloadAssets = DownloadAssets,
            UseTargetAsInstance = UseTargetAsInstance,
            JavaPath = ShowJava ? JavaPath : null,
        };

        IsInstalling = true;
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "准备下载…";
        StatusText = $"正在安装 {SelectedVersion.Id} / {SelectedLoader.Label}…";

        var detail = new DownloadProgressViewModel(
            "下载游戏本体与加载器",
            $"{request.MinecraftVersion} · {SelectedLoader.Label} {loaderVersion}".Trim());
        DownloadProgressWindow.ShowFor(detail);
        var progress = new Progress<DownloadProgress>(report =>
        {
            detail.Update(report);
            ProgressValue = report.Fraction;
            ProgressText = report.Total > 0
                ? $"{report.Phase} {report.Done}/{report.Total}"
                : report.Phase;
            if (!string.IsNullOrWhiteSpace(report.Message))
                StatusText = $"{report.Phase} · {report.Message}";
        });

        try
        {
            var result = await _installer.InstallAsync(request, progress, CancellationToken.None);
            StatusText = $"安装完成：{result.InstanceDirectory}";
            detail.Finish(true, $"安装完成：{result.InstanceDirectory}");
            Completed?.Invoke(result);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                                       InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            StatusText = $"安装失败：{ex.Message}";
            detail.Finish(false, ex.Message);
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

    private async Task LoadLoaderVersionsAsync(bool forceRefresh = false)
    {
        var loadId = Interlocked.Increment(ref _loaderLoadId);
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        HasLoaderVersions = false;

        if (SelectedVersion is null || SelectedLoader is null)
        {
            StatusText = "请先选择 Minecraft 版本和加载器。";
            return;
        }
        if (SelectedLoader.Value == GameLoader.Vanilla)
        {
            StatusText = "原版模式：只下载游戏本体，不安装加载器。";
            return;
        }

        var loaderLabel = SelectedLoader.Label;
        var minecraftVersion = SelectedVersion.Id;
        IsBusy = true;
        StatusText = $"正在读取 {loaderLabel} 版本…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var versions = await _installer.GetLoaderVersionsAsync(
                minecraftVersion, SelectedLoader.Value, timeout.Token, forceRefresh);
            if (loadId != _loaderLoadId) return;

            foreach (var version in versions) LoaderVersions.Add(version);
            HasLoaderVersions = LoaderVersions.Count > 0;
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(LoaderVersionText) && SelectedLoaderVersion is not null)
                LoaderVersionText = SelectedLoaderVersion.Version;
            StatusText = versions.Count == 0
                ? $"没有找到 {minecraftVersion} 可用的 {loaderLabel} 版本。"
                : $"找到 {versions.Count} 个 {loaderLabel} 版本。";
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
            if (loadId == _loaderLoadId)
            {
                IsBusy = false;
                ProgressText = string.Empty;
            }
        }
    }

    private void SaveSettings()
    {
        if (_suppressSave) return;

        try
        {
            _settings.GameInstallTargetDirectory = TargetDirectory;
            _settings.GameInstallDownloadAssets = DownloadAssets;
            _settings.GameInstallUseTargetAsInstance = UseTargetAsInstance;
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings persistence is best effort; never block installation.
        }
    }

    private void RefreshVersions()
    {
        var selectedId = SelectedVersion?.Id;
        Versions.Clear();
        foreach (var version in _allVersions.Where(version => !OnlyReleases || version.IsRelease))
            Versions.Add(version);

        if (selectedId is not null)
            SelectedVersion = Versions.FirstOrDefault(version => version.Id == selectedId);
    }
}
