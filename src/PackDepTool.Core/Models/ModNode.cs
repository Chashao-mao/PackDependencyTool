namespace PackDepTool.Core.Models;

using System.Text.Json.Serialization;

/// <summary>A single mod in the dependency graph. Id is the stable mod id (lower-case).</summary>
public sealed class ModNode
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "unknown";
    public LoaderKind Loader { get; set; } = LoaderKind.Unknown;
    public List<string> McVersions { get; set; } = [];
    public ProjectIds ProjectIds { get; set; } = new();

    /// <summary>Path relative to the pack root (forward slashes). Null for synthetic nodes.</summary>
    public string? FilePath { get; set; }
    public DateTimeOffset? FileModifiedAt { get; set; }

    public EdgeSource Source { get; set; } = EdgeSource.Local;
    public ModStatus Status { get; set; } = ModStatus.Ok;

    // ---- optional enrichment / display data (ignored when unknown) ----
    public string? Description { get; set; }
    public string? Authors { get; set; }
    public ModSide Side { get; set; } = ModSide.Unknown;

    /// <summary>True when the physical side was inferred from dependency metadata or defaults.</summary>
    public bool SideInferred { get; set; }
    public string? Slug { get; set; }
    /// <summary>User-provided Chinese display name. No third-party translation index is bundled.</summary>
    public string? NameZh { get; set; }
    /// <summary>Modrinth / CurseForge project logo URL (not persisted; used to fetch icons).</summary>
    [JsonIgnore]
    public string? IconUrl { get; set; }
    /// <summary>MC百科 class id when the local index knows this mod.</summary>
    public int? McmodId { get; set; }
    public string? Sha1 { get; set; }
    public bool IsEmbedded { get; set; }
    public List<string> Provides { get; set; } = [];
    public string? McVersionRange { get; set; }
    /// <summary>Human explanation for a non-ok status.</summary>
    public string? StatusReason { get; set; }
    /// <summary>Normalised project title from CurseForge/Modrinth when it differs from the local name.</summary>
    public string? ProjectTitle { get; set; }

    /// <summary>Coarse functional category inferred from id/name/description.</summary>
    public ModCategory Category { get; set; } = ModCategory.Other;
    /// <summary>Short explanation of the classifier match (not shown in normal UI).</summary>
    public string? CategoryReason { get; set; }

    /// <summary>True when the node was created only because an edge references it (not installed).</summary>
    [JsonIgnore] public bool IsSynthetic { get; set; }
    /// <summary>Synthetic placeholder created only by an optional edge.</summary>
    [JsonIgnore] public bool IsOptionalDependency { get; set; }
    /// <summary>Synthetic placeholder created only by a recommended edge.</summary>
    [JsonIgnore] public bool IsRecommendedDependency { get; set; }

    /// <summary>Raw icon bytes from the jar; ignored by JSON exports.</summary>
    [JsonIgnore] public byte[]? IconData { get; set; }

    public bool IsMissing => Status == ModStatus.Missing;

    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? "unknown" : Version;
}
