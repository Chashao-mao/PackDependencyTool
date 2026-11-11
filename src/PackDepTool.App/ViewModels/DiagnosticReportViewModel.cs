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
    }

    public DiagnosticFinding Model { get; }
    public string SeverityText { get; }
    public IBrush SeverityBrush { get; }
    public string RelatedText { get; }
    public string Title => Model.Title;
    public string Message => Model.Message;
    public string Evidence => Model.Evidence;
    public string ConfidenceText => $"可信度 {Model.Confidence}";
}

public sealed partial class DiagnosticReportViewModel : ObservableObject
{
    public DiagnosticReportViewModel(DiagnosticReport report)
    {
        Report = report;
        foreach (var finding in report.Findings)
            Findings.Add(new DiagnosticFindingItem(finding));
        SourceText = string.IsNullOrWhiteSpace(report.SourcePath)
            ? "未指定日志"
            : report.SourcePath;
    }

    public DiagnosticReport Report { get; }
    public ObservableCollection<DiagnosticFindingItem> Findings { get; } = [];
    public string Summary => Report.Summary;
    public string SourceText { get; }
    public bool HasFindings => Findings.Count > 0;

    public event Action? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
