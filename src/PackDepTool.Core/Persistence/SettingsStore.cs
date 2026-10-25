using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Persistence;

public sealed class AppSettings
{
    public string? LastPackPath { get; set; }
    public List<string> RecentPacks { get; set; } = [];

    public string Theme { get; set; } = "Light";
    public bool NetworkEnabled { get; set; } = true;
    public string? ModrinthContact { get; set; }

    // Edge type visibility. Defaults follow the product spec: requires + incompatible only.
    public bool ShowOptional { get; set; }
    public bool ShowEmbedded { get; set; }
    public bool ShowRecommends { get; set; }
    public bool ShowManual { get; set; } = true;
    public bool HideIsolated { get; set; }
    public bool ShowLabels { get; set; } = true;
    public int MaxParallelDownloads { get; set; } = 3;
    public string GameDownloadSource { get; set; } = "auto";
    public string? ModrinthApiBaseUrl { get; set; }
    public string? CurseForgeApiBaseUrl { get; set; }
    public string? ModCdnBaseUrl { get; set; }
    public string? UpdateFeedUrl { get; set; }
    public bool CheckUpdatesOnStartup { get; set; }
    public bool AutoDownloadUpdates { get; set; }
    public bool AutoInstallUpdates { get; set; }

    public LayoutAlgorithm LayoutAlgorithm { get; set; } = LayoutAlgorithm.Auto;

    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public bool Maximized { get; set; }

    // Persisted game-install / loader-switch choices.
    // Minecraft version and loader are intentionally NOT persisted (they change frequently).
    public string? GameInstallTargetDirectory { get; set; }
    public bool GameInstallDownloadAssets { get; set; } = true;
    public bool GameInstallUseTargetAsInstance { get; set; }

    public string? LoaderSwitchInstanceDirectory { get; set; }

    // Instance manager: launcher-style versions directory list.
    public string? VersionsDirectory { get; set; }
    public List<string> VersionsDirectories { get; set; } = [];

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.RecentPacks = [.. RecentPacks];
        clone.VersionsDirectories = [.. VersionsDirectories];
        return clone;
    }

    public void CopyFrom(AppSettings other)
    {
        LastPackPath = other.LastPackPath;
        RecentPacks = [.. other.RecentPacks];
        Theme = other.Theme;
        NetworkEnabled = other.NetworkEnabled;
        ModrinthContact = other.ModrinthContact;
        ShowOptional = other.ShowOptional;
        ShowEmbedded = other.ShowEmbedded;
        ShowRecommends = other.ShowRecommends;
        ShowManual = other.ShowManual;
        HideIsolated = other.HideIsolated;
        ShowLabels = other.ShowLabels;
        MaxParallelDownloads = other.MaxParallelDownloads;
        GameDownloadSource = other.GameDownloadSource;
        ModrinthApiBaseUrl = other.ModrinthApiBaseUrl;
        CurseForgeApiBaseUrl = other.CurseForgeApiBaseUrl;
        ModCdnBaseUrl = other.ModCdnBaseUrl;
        UpdateFeedUrl = other.UpdateFeedUrl;
        CheckUpdatesOnStartup = other.CheckUpdatesOnStartup;
        AutoDownloadUpdates = other.AutoDownloadUpdates;
        AutoInstallUpdates = other.AutoInstallUpdates;
        LayoutAlgorithm = other.LayoutAlgorithm;
        WindowWidth = other.WindowWidth;
        WindowHeight = other.WindowHeight;
        WindowX = other.WindowX;
        WindowY = other.WindowY;
        Maximized = other.Maximized;
        GameInstallTargetDirectory = other.GameInstallTargetDirectory;
        GameInstallDownloadAssets = other.GameInstallDownloadAssets;
        GameInstallUseTargetAsInstance = other.GameInstallUseTargetAsInstance;
        LoaderSwitchInstanceDirectory = other.LoaderSwitchInstanceDirectory;
        VersionsDirectory = other.VersionsDirectory;
        VersionsDirectories = [.. other.VersionsDirectories];
    }
}

public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(AppPaths paths) => _paths = paths;

    public AppSettings Load()
    {
        var text = AtomicFile.TryReadText(_paths.SettingsFilePath);
        if (string.IsNullOrWhiteSpace(text)) return new AppSettings();

        try
        {
            var settings = JsonDefaults.Deserialize<AppSettings>(text);
            return settings ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            var json = JsonDefaults.Serialize(settings);
            await AtomicFile.WriteTextAsync(_paths.SettingsFilePath, json, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Used while the main window is closing, when awaiting is not an option.</summary>
    public void Save(AppSettings settings)
    {
        _gate.Wait();
        try
        {
            _paths.EnsureDirectories();
            var json = JsonDefaults.Serialize(settings);
            AtomicFile.WriteTextAsync(_paths.SettingsFilePath, json).GetAwaiter().GetResult();
        }
        finally
        {
            _gate.Release();
        }
    }
}
