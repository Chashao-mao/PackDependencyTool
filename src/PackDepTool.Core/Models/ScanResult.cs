namespace PackDepTool.Core.Models;

/// <summary>Result of walking a pack's mods directory.</summary>
public sealed class ScanResult
{
    public string RootPath { get; set; } = string.Empty;
    public string ModsDirectory { get; set; } = string.Empty;
    public PackProfile Profile { get; set; } = new();
    public List<JarScanResult> Jars { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public int JarCount => Jars.Count;
}

/// <summary>Everything extracted from one jar file.</summary>
public sealed class JarScanResult
{
    public string FilePath { get; set; } = string.Empty; // relative to pack root, forward slashes
    public string AbsolutePath { get; set; } = string.Empty;
    public string Sha1 { get; set; } = string.Empty;
    public DateTimeOffset ModifiedAt { get; set; }
    public List<ParsedMod> Mods { get; set; } = [];
    public List<ParsedEmbedded> Embedded { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool IsDisabled { get; set; }
}

/// <summary>Normalised metadata for one mod (a jar can declare more than one mod).</summary>
public sealed class ParsedMod
{
    public string ModId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "unknown";
    public LoaderKind Loader { get; set; } = LoaderKind.Unknown;
    public string? Description { get; set; }
    public string? Authors { get; set; }
    public string? McVersionRange { get; set; }
    public List<string> McVersions { get; set; } = [];
    public ModSide Side { get; set; } = ModSide.Unknown;
    public List<ParsedDependency> Dependencies { get; set; } = [];
    public List<string> Provides { get; set; } = [];
    public List<ParsedEmbedded> Embedded { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    /// <summary>Icon path declared by the loader metadata (relative to the jar).</summary>
    public string? IconEntry { get; set; }
    /// <summary>Raw icon bytes extracted by the scanner (PNG/JPEG/GIF).</summary>
    public byte[]? IconData { get; set; }
}

public sealed class ParsedDependency
{
    public string ModId { get; set; } = string.Empty;
    public EdgeType Type { get; set; } = EdgeType.Requires;
    public string? VersionRange { get; set; }
    public ModSide Side { get; set; } = ModSide.Unknown;
    public string? Note { get; set; }
}

public sealed class ParsedEmbedded
{
    public string ModId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? File { get; set; }
    public LoaderKind Loader { get; set; } = LoaderKind.Unknown;
}

/// <summary>Mod ids that describe the platform rather than installable content.</summary>
public static class PlatformModIds
{
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "forge", "neoforge", "fabricloader", "fabric_loader", "fabric-loader",
        "quilt_loader", "quiltloader", "javafml", "lowcodefml", "liteloader", "modlauncher",
        "securejarhandler", "bootstraplauncher", "mixin", "mixinextras", "asm", "fmlcore",
    };

    public static bool IsPlatform(string? id) => !string.IsNullOrWhiteSpace(id) && All.Contains(id);
}
