using PackDepTool.Core.Analysis;

namespace PackDepTool.App.ViewModels;

public enum HealthWizardAction
{
    Close,
    Missing,
    Conflict,
    Backup,
    Workspace,
}

public sealed class HealthWizardViewModel
{
    public HealthWizardViewModel(PackAuditReport report)
    {
        Report = report;
        Summary = $"{report.ModCount} 个模组 · {report.JarCount} 个 JAR · {report.TotalSizeText}";
        MissingText = $"缺失依赖 {report.MissingCount}";
        ConflictText = $"冲突 {report.ConflictCount}";
        MismatchText = $"版本不符 {report.MismatchCount}";
        DuplicateText = $"重复 JAR {report.DuplicateJarCount}";
        HasProblems = report.HasProblems;
    }

    public PackAuditReport Report { get; }
    public string Summary { get; }
    public string MissingText { get; }
    public string ConflictText { get; }
    public string MismatchText { get; }
    public string DuplicateText { get; }
    public bool HasProblems { get; }
    public string Recommendation => Report.MissingCount > 0
        ? "先补缺失依赖，再处理冲突和版本不符。"
        : Report.ConflictCount + Report.MismatchCount > 0
            ? "优先处理冲突和版本不符，再进入模组列表批量操作。"
            : "没有阻断性问题，可以直接进入模组列表。";
}
