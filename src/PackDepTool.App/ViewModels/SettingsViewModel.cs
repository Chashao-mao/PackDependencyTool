using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;

namespace PackDepTool.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly ApiKeyProvider _keys;
    private readonly CurseForgeClient _curseForge;

    public SettingsViewModel(
        AppSettings current,
        AppPaths paths,
        ApiKeyProvider keys,
        CurseForgeClient curseForge,
        bool hasStoredKey)
    {
        _settings = current.Clone();
        _paths = paths;
        _keys = keys;
        _curseForge = curseForge;

        ThemeOptions = ThemeOption.All;
        LayoutOptions = LayoutOption.All;

        SelectedTheme = ThemeOptions.FirstOrDefault(t =>
                            string.Equals(t.Value, _settings.Theme, StringComparison.OrdinalIgnoreCase))
                        ?? ThemeOptions[0];

        var layoutValue = _settings.LayoutAlgorithm switch
        {
            LayoutAlgorithm.ForceDirected => "force",
            LayoutAlgorithm.Layered => "layered",
            _ => "auto",
        };
        SelectedLayout = LayoutOptions.FirstOrDefault(o => o.Value == layoutValue) ?? LayoutOptions[0];

        ModrinthContact = _settings.ModrinthContact ?? string.Empty;
        NetworkEnabled = _settings.NetworkEnabled;
        ShowOptional = _settings.ShowOptional;
        ShowEmbedded = _settings.ShowEmbedded;
        ShowRecommends = _settings.ShowRecommends;
        ShowManual = _settings.ShowManual;
        ShowLabels = _settings.ShowLabels;
        MaxParallelDownloads = _settings.MaxParallelDownloads;
        UpdateFeedUrl = _settings.UpdateFeedUrl ?? string.Empty;
        CheckUpdatesOnStartup = _settings.CheckUpdatesOnStartup;
        AutoDownloadUpdates = _settings.AutoDownloadUpdates;
        AutoInstallUpdates = _settings.AutoInstallUpdates;

        HasStoredKey = hasStoredKey;
        IsFromEnvironment = keys.IsFromEnvironment;
        KeyStatusText = BuildKeyStatus();
        DataDirectory = paths.DataDirectory;
        StorageDescription = keys.StorageDescription;
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }
    public IReadOnlyList<LayoutOption> LayoutOptions { get; }
    public string DataDirectory { get; }
    public string StorageDescription { get; }
    public string VersionLabel => "Pack Dependency Tool v1.0.0 · .NET 9 / Avalonia";

    [ObservableProperty] private ThemeOption? _selectedTheme;
    [ObservableProperty] private LayoutOption? _selectedLayout;
    [ObservableProperty] private string _modrinthContact = string.Empty;
    [ObservableProperty] private bool _networkEnabled;
    [ObservableProperty] private bool _showOptional;
    [ObservableProperty] private bool _showEmbedded;
    [ObservableProperty] private bool _showRecommends;
    [ObservableProperty] private bool _showManual;
    [ObservableProperty] private bool _showLabels;
    [ObservableProperty] private int _maxParallelDownloads = 3;
    [ObservableProperty] private string _updateFeedUrl = string.Empty;
    [ObservableProperty] private bool _checkUpdatesOnStartup;
    [ObservableProperty] private bool _autoDownloadUpdates;
    [ObservableProperty] private bool _autoInstallUpdates;

    [ObservableProperty] private string _keyInput = string.Empty;
    [ObservableProperty] private string _keyStatusText = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasStoredKey;
    [ObservableProperty] private bool _isFromEnvironment;

    public event Action<AppSettings?>? Completed;

    [RelayCommand]
    private void SaveAndClose()
    {
        _settings.Theme = SelectedTheme?.Value ?? "Dark";
        _settings.LayoutAlgorithm = SelectedLayout?.Value switch
        {
            "force" => LayoutAlgorithm.ForceDirected,
            "layered" => LayoutAlgorithm.Layered,
            _ => LayoutAlgorithm.Auto,
        };
        _settings.ModrinthContact = string.IsNullOrWhiteSpace(ModrinthContact) ? null : ModrinthContact.Trim();
        _settings.NetworkEnabled = NetworkEnabled;
        _settings.ShowOptional = ShowOptional;
        _settings.ShowEmbedded = ShowEmbedded;
        _settings.ShowRecommends = ShowRecommends;
        _settings.ShowManual = ShowManual;
        _settings.ShowLabels = ShowLabels;
        _settings.MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 6);
        _settings.UpdateFeedUrl = string.IsNullOrWhiteSpace(UpdateFeedUrl) ? null : UpdateFeedUrl.Trim();
        _settings.CheckUpdatesOnStartup = CheckUpdatesOnStartup;
        _settings.AutoDownloadUpdates = AutoDownloadUpdates;
        _settings.AutoInstallUpdates = AutoInstallUpdates && AutoDownloadUpdates;
        Completed?.Invoke(_settings);
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);

    [RelayCommand]
    private async Task SaveKeyAsync()
    {
        if (IsFromEnvironment)
        {
            Message = "当前 Key 来自环境变量 CURSEFORGE_API_KEY，程序不会覆盖它。";
            return;
        }

        var key = KeyInput.Trim();
        if (key.Length == 0)
        {
            Message = "请先输入 CurseForge API Key。";
            return;
        }

        IsBusy = true;
        Message = "正在保存并验证 Key…";
        try
        {
            await _keys.SetCurseForgeKeyAsync(key);
            var valid = await _curseForge.ValidateKeyAsync();
            if (!valid)
            {
                await _keys.ClearAsync();
                HasStoredKey = false;
                KeyStatusText = BuildKeyStatus();
                Message = "Key 验证失败（HTTP 401/403），未保存。";
                return;
            }

            HasStoredKey = true;
            KeyInput = string.Empty;
            KeyStatusText = BuildKeyStatus();
            Message = "已加密保存并通过验证。";
        }
        catch (Exception ex)
        {
            // The key may still be stored; report the network/validation error without echoing it.
            HasStoredKey = await _keys.HasStoredKeyAsync();
            KeyStatusText = BuildKeyStatus();
            Message = $"已加密保存，但验证失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearKeyAsync()
    {
        if (IsFromEnvironment)
        {
            Message = "Key 来自环境变量，请从系统环境变量中移除。";
            return;
        }

        await _keys.ClearAsync();
        HasStoredKey = false;
        KeyStatusText = BuildKeyStatus();
        Message = "已清除本地 API Key。";
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_paths.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Message = $"无法打开数据目录：{ex.Message}";
        }
    }

    private string BuildKeyStatus()
    {
        if (IsFromEnvironment) return "已从环境变量 CURSEFORGE_API_KEY 读取（优先级最高）";
        if (HasStoredKey) return $"已保存并加密（{_keys.StorageDescription}）";
        return "未配置";
    }
}
