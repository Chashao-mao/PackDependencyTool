using System.Text.Json.Serialization;

namespace PackDepTool.Core.Models;

/// <summary>Mod loader family detected from jar metadata or the pack profile.</summary>
public enum LoaderKind
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("fabric")] Fabric,
    [JsonStringEnumMemberName("quilt")] Quilt,
    [JsonStringEnumMemberName("forge")] Forge,
    [JsonStringEnumMemberName("neoforge")] NeoForge,
}

/// <summary>Health state of a node inside the analysed graph.</summary>
public enum ModStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("missing")] Missing,
    [JsonStringEnumMemberName("versionMismatch")] VersionMismatch,
    [JsonStringEnumMemberName("conflict")] Conflict,
    [JsonStringEnumMemberName("unknown")] Unknown,
}

/// <summary>Semantic of a dependency edge. Direction is depender -> dependency.</summary>
public enum EdgeType
{
    [JsonStringEnumMemberName("requires")] Requires,
    [JsonStringEnumMemberName("optional")] Optional,
    [JsonStringEnumMemberName("incompatible")] Incompatible,
    [JsonStringEnumMemberName("embedded")] Embedded,
    [JsonStringEnumMemberName("recommends")] Recommends,
    [JsonStringEnumMemberName("manual")] Manual,
}

/// <summary>Where an edge or node came from.</summary>
public enum EdgeSource
{
    [JsonStringEnumMemberName("local")] Local,
    [JsonStringEnumMemberName("curseforge")] CurseForge,
    [JsonStringEnumMemberName("modrinth")] Modrinth,
    [JsonStringEnumMemberName("manual")] Manual,
}

/// <summary>Physical side a dependency applies to.</summary>
public enum ModSide
{
    [JsonStringEnumMemberName("both")] Both,
    [JsonStringEnumMemberName("client")] Client,
    [JsonStringEnumMemberName("server")] Server,
    [JsonStringEnumMemberName("unknown")] Unknown,
}

public enum IssueSeverity
{
    [JsonStringEnumMemberName("info")] Info,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("error")] Error,
}

public enum IssueKind
{
    [JsonStringEnumMemberName("missingDependency")] MissingDependency,
    [JsonStringEnumMemberName("optionalMissing")] OptionalMissing,
    [JsonStringEnumMemberName("incompatible")] Incompatible,
    [JsonStringEnumMemberName("versionMismatch")] VersionMismatch,
    [JsonStringEnumMemberName("mcVersionMismatch")] McVersionMismatch,
    [JsonStringEnumMemberName("cycle")] Cycle,
    [JsonStringEnumMemberName("duplicateModId")] DuplicateModId,
    [JsonStringEnumMemberName("selfDependency")] SelfDependency,
    [JsonStringEnumMemberName("parseWarning")] ParseWarning,
    [JsonStringEnumMemberName("invalidManualLink")] InvalidManualLink,
    [JsonStringEnumMemberName("unknown")] Unknown,
}
