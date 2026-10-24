namespace PackDepTool.Core.Models;

/// <summary>A user authored relation. Persisted verbatim; overrides auto-detected edges.</summary>
public sealed class ManualLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public EdgeType Type { get; set; } = EdgeType.Requires;
    public string? VersionRange { get; set; }
    public ModSide Side { get; set; } = ModSide.Unknown;
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ManualLink Clone() => (ManualLink)MemberwiseClone();
}

/// <summary>On-disk shape of a pack's manual links.</summary>
public sealed class ManualLinkFile
{
    public int Version { get; set; } = 1;
    public string? PackPath { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ManualLink> Links { get; set; } = [];
}

public sealed class ManualLinkImportResult
{
    public List<ManualLink> Links { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}
