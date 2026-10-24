namespace PackDepTool.Core.Models;

public sealed class AnalysisIssue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public IssueSeverity Severity { get; set; }
    public IssueKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? NodeId { get; set; }
    public string? EdgeId { get; set; }
    public string? RelatedNodeId { get; set; }
}
