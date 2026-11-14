using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Analysis;

namespace PackDepTool.App.ViewModels;

public sealed class DiagnosticFindingItem
{
    public DiagnosticFindingItem(DiagnosticFinding finding)
    {
        Model = finding;
        SeverityText = finding.Severity switch
        {
            DiagnosticSeverity.Error => "错误",
            DiagnosticSeverity.Warning => "警告",
            _ => "提示",
        };
        SeverityBrush = new SolidColorBrush(finding.Severity switch
        {
            DiagnosticSeverity.Error => GraphPalette.EdgeIncompatible,
            DiagnosticSeverity.Warning => GraphPalette.Warning,
            _ => GraphPalette.Accent,
        });
        RelatedText = finding.RelatedModIds.Count == 0
            ? string.Empty
            : "相关模组：" + string.Join(", ", finding.RelatedModIds);
        FixText = string.IsNullOrWhiteSpace(finding.SuggestedFix)
            ? string.Empty
            : "建议：" + finding.SuggestedFix;
        SourceText = string.IsNullOrWhiteSpace(finding.SourceUrl)
            ? string.Empty
            : "来源：" + finding.SourceUrl;
    }

    public DiagnosticFinding Model { get; }
    public string SeverityText { get; }
    public IBrush SeverityBrush { get; }
    public string RelatedText { get; }
    public string Title => Model.Title;
    public string Message => Model.Message;
    public string Evidence => Model.Evidence;
    public string FixText { get; }
    public string SourceText { get; }
    public bool HasFix => !string.IsNullOrEmpty(FixText);
    public bool HasSource => !string.IsNullOrEmpty(SourceText);
    public string ConfidenceText => $"可信度 {Model.Confidence}";
}

public sealed partial class DiagnosticReportViewModel : ObservableObject
{
    public DiagnosticReportViewModel(DiagnosticReport report)
    {
        Report = report;
        foreach (var finding in report.Findings
                     .OrderBy(finding => finding.Severity)
                     .ThenBy(finding => finding.Title, StringComparer.Ordinal))
            Findings.Add(new DiagnosticFindingItem(finding));
        var verdict = report.Findings.FirstOrDefault(finding => finding.Severity == DiagnosticSeverity.Error)
                      ?? report.Findings.FirstOrDefault();
        VerdictTitle = verdict?.Title ?? "未识别到确定性问题";
        VerdictMessage = verdict?.Message ?? "日志中没有匹配到确定性崩溃模式，建议查看原始日志。";
        HasVerdict = verdict is not null;
        SourceText = string.IsNullOrWhiteSpace(report.SourcePath)
            ? "未指定日志"
            : report.SourcePath;
    }

    public DiagnosticReport Report { get; }
    public ObservableCollection<DiagnosticFindingItem> Findings { get; } = [];
    public string Summary => Report.Summary;
    public string SourceText { get; }
    public string FactsText => Report.Facts?.Summary ?? string.Empty;
    public string VerdictTitle { get; }
    public string VerdictMessage { get; }
    public bool HasVerdict { get; }
    public bool HasFacts => !string.IsNullOrWhiteSpace(FactsText);
    public bool HasFindings => Findings.Count > 0;

    public event Action? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
