namespace PackDepTool.Core.Models;

/// <summary>Remote project identities (and the human readable slug) for a mod.</summary>
public sealed class ProjectIds
{
    public string? CurseForge { get; set; }
    public string? Modrinth { get; set; }
    public string? Slug { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(CurseForge) &&
        string.IsNullOrWhiteSpace(Modrinth) &&
        string.IsNullOrWhiteSpace(Slug);

    public ProjectIds Clone() => new()
    {
        CurseForge = CurseForge,
        Modrinth = Modrinth,
        Slug = Slug,
    };
}
