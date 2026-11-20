namespace PackDepTool.Core.Analysis;

public enum JarConflictKind
{
    DuplicateClass,
    MixinTarget,
    AccessTransformer,
    ResourceOverride,
}

/// <summary>
/// A structured content-level conflict between two installed JARs. This is the
/// "bytecode conflict edge" consumed by the mod graph and node details.
/// </summary>
public sealed record JarConflict(
    JarConflictKind Kind,
    bool HighRisk,
    string PrimaryModId,
    string PrimaryName,
    string SecondaryModId,
    string SecondaryName,
    string Evidence)
{
    public string KindText => Kind switch
    {
        JarConflictKind.DuplicateClass => "重复类",
        JarConflictKind.MixinTarget => "Mixin 目标重叠",
        JarConflictKind.AccessTransformer => "Access Transformer 冲突",
        _ => "资源覆盖",
    };

    public string SeverityText => HighRisk ? "硬冲突" : "提示";
}
