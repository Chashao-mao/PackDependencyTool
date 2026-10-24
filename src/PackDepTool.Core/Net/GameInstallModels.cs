namespace PackDepTool.Core.Net;

public enum GameLoader
{
    Vanilla,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
}

public sealed class MinecraftVersionInfo
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public DateTimeOffset? ReleaseTime { get; init; }
    public string Url { get; init; } = string.Empty;
    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);
    public string Label => IsRelease ? Id : $"{Id} ({Type})";
}

public sealed class LoaderVersionInfo
{
    public string Version { get; init; } = string.Empty;
    public bool Stable { get; init; } = true;
    public string Label => Stable ? Version : $"{Version} (beta)";
}

public sealed class GameInstallRequest
{
    public string TargetDirectory { get; init; } = string.Empty;
    public string? InstanceName { get; init; }
    public string MinecraftVersion { get; init; } = string.Empty;
    public GameLoader Loader { get; init; } = GameLoader.Fabric;
    public string? LoaderVersion { get; init; }
    public bool DownloadAssets { get; init; }
    public bool UseTargetAsInstance { get; init; }
    public string? JavaPath { get; init; }
}

public sealed class GameInstallResult
{
    public string InstanceDirectory { get; init; } = string.Empty;
    public string MinecraftVersion { get; init; } = string.Empty;
    public GameLoader Loader { get; init; }
    public string? LoaderVersion { get; init; }
    public string? VersionId { get; set; }
    public int FilesDownloaded { get; set; }
    public long BytesDownloaded { get; set; }
    public string? JavaPath { get; set; }
    public List<string> Warnings { get; } = [];

    public string LoaderName => Loader switch
    {
        GameLoader.Fabric => "Fabric",
        GameLoader.Quilt => "Quilt",
        GameLoader.Forge => "Forge",
        GameLoader.NeoForge => "NeoForge",
        _ => "Vanilla",
    };
}
