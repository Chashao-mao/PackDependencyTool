namespace PackDepTool.Core.Models;

/// <summary>Loader / Minecraft / pack name detected from launcher metadata files or inferred from mods.</summary>
public sealed class PackProfile
{
    public string? PackName { get; set; }
    public string McVersion { get; set; } = "unknown";
    public LoaderKind Loader { get; set; } = LoaderKind.Unknown;
    public string? LoaderVersion { get; set; }
    public string? DetectedFrom { get; set; }
    public bool IsInferred { get; set; }
    public List<string> Warnings { get; set; } = [];

    public string LoaderKey => Loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => "unknown",
    };

    public string DisplayLoader => Loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "unknown",
    };
}

public sealed class ScanProgress
{
    public int Processed { get; init; }
    public int Total { get; init; }
    public string CurrentFile { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;

    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Processed / Total, 0, 1);
}
