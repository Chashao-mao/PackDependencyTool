namespace PackDepTool.Core.Net;

public enum DownloadSource
{
    Modrinth,
    CurseForge,
    All,
}

public enum DownloadProjectType
{
    Mod,
    Modpack,
}

public sealed class DownloadProject
{
    public DownloadSource Source { get; init; }
    public DownloadProjectType Type { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? IconUrl { get; init; }
    public string? Author { get; init; }
    public long Downloads { get; init; }

    public string SourceName => Source == DownloadSource.Modrinth ? "Modrinth" : "CurseForge";
    public string TypeName => Type == DownloadProjectType.Modpack ? "整合包" : "模组";
}

public sealed class DownloadCategory
{
    public string Name { get; init; } = string.Empty;
    public string? ModrinthSlug { get; init; }
    public int? CurseForgeId { get; init; }
    public DownloadProjectType Type { get; init; }
}

public sealed class DownloadSearchPage
{
    public List<DownloadProject> Projects { get; init; } = [];
    public bool HasMore { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
}

public sealed class DownloadVersion
{
    public DownloadSource Source { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string VersionId { get; init; } = string.Empty;
    public string VersionNumber { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string? DownloadUrl { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha512 { get; init; }
    public long FileSize { get; init; }
    public List<string> GameVersions { get; init; } = [];
    public List<string> Loaders { get; init; } = [];
    public DateTimeOffset? PublishedAt { get; init; }
    public bool IsCompatible { get; init; } = true;
    public List<string> Dependencies { get; init; } = [];

    public string Meta => string.Join(" · ", new[]
    {
        string.Join(", ", GameVersions.Take(3)),
        string.Join(", ", Loaders.Take(2)),
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public sealed record DownloadProgress(
    string Phase,
    int Done,
    int Total,
    string Message = "",
    long Bytes = 0,
    long TotalBytes = 0)
{
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)Bytes / TotalBytes, 0, 1)
        : (Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0);
}

public sealed class DownloadResult
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Sha1 { get; init; } = string.Empty;
    public long Bytes { get; init; }
}

public sealed class PackImportResult
{
    public string PackRoot { get; init; } = string.Empty;
    public int DownloadedFiles { get; set; }
    public int ExtractedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> MissingFiles { get; } = [];
}
