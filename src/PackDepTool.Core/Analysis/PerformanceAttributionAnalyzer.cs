using System.Globalization;
using System.Text.RegularExpressions;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

/// <summary>
/// Extracts performance attribution from spark-style profiler output and vanilla
/// performance log lines. Percentages are treated as observations, never as
/// proof that a mod is the only cause.
/// </summary>
public static class PerformanceAttributionAnalyzer
{
    private static readonly Regex PercentFrame = new(
        @"(?<pct>\d+(?:[.,]\d+)?)\s*%\s+(?<frame>(?:[A-Za-z_$][\w$]*\.)+[A-Za-z_$][\w$#<>]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Tps = new(
        @"(?<value>\d+(?:[.,]\d+)?)\s*TPS", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Mspt = new(
        @"(?<value>\d+(?:[.,]\d+)?)\s*ms(?:/t(?:ick)?| per tick|pt)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static DiagnosticReport Analyze(string text, string sourcePath, GraphDocument? graph = null)
    {
        var findings = new List<DiagnosticFinding>();
        var hotspots = new Dictionary<string, (double Percent, string Evidence)>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');

        foreach (var line in lines)
        {
            var match = PercentFrame.Match(line);
            if (match.Success)
            {
                var percent = ParseNumber(match.Groups["pct"].Value);
                var frame = match.Groups["frame"].Value;
                if (hotspots.TryGetValue(frame, out var existing))
                {
                    if (percent > existing.Percent)
                        hotspots[frame] = (percent, line.Trim());
                }
                else
                {
                    hotspots[frame] = (percent, line.Trim());
                }
            }

            if (line.Contains("Can't keep up!", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Running", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("ms behind", StringComparison.OrdinalIgnoreCase))
            {
                AddOnce(findings, "tick-lag", DiagnosticSeverity.Warning,
                    "tick 落后",
                    "日志显示服务端/客户端 tick 无法跟上目标速度；结合 spark 热点和实体/区块数量判断。", line);
            }

            var tps = Tps.Match(line);
            if (tps.Success)
            {
                var value = ParseNumber(tps.Groups["value"].Value);
                if (value is > 0 and < 18)
                    AddOnce(findings, "low-tps", DiagnosticSeverity.Warning,
                        "TPS 低于目标",
                        $"日志报告 TPS={value:0.##}，低于 20；需要结合 MSPT 和热点定位。", line);
            }

            var mspt = Mspt.Match(line);
            if (mspt.Success)
            {
                var value = ParseNumber(mspt.Groups["value"].Value);
                if (value > 50)
                    AddOnce(findings, "high-mspt", DiagnosticSeverity.Warning,
                        "单 tick 耗时过高",
                        $"日志报告 {value:0.##} ms/tick，超过 50ms；可能导致 tick 落后。", line);
            }
        }

        foreach (var (frame, hotspot) in hotspots.OrderByDescending(pair => pair.Value.Percent).Take(20))
        {
            if (hotspot.Percent < 1) continue;
            findings.Add(new DiagnosticFinding
            {
                RuleId = "performance-hotspot",
                Severity = hotspot.Percent >= 25 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                Title = $"性能热点：{ShortFrame(frame)}",
                Message = $"采样占比约 {hotspot.Percent:0.##}%。这只是采样线索，不等于唯一原因。",
                Evidence = hotspot.Evidence,
                RelatedModIds = FindRelatedMods(frame, graph),
            });
        }

        var summary = findings.Count == 0
            ? "没有识别到性能采样、TPS/MSPT 或 tick 落后数据。"
            : $"识别到 {findings.Count} 条性能线索" +
              (hotspots.Count > 0 ? $"，最高热点约 {hotspots.Values.Max(item => item.Percent):0.##}%。" : "。");

        return new DiagnosticReport
        {
            SourcePath = sourcePath,
            Summary = summary,
            Findings = findings,
        };
    }

    public static IReadOnlyList<DiagnosticFinding> AnalyzeFindings(
        string text, string sourcePath, GraphDocument? graph = null) =>
        Analyze(text, sourcePath, graph).Findings;

    private static IReadOnlyList<string> FindRelatedMods(string frame, GraphDocument? graph)
    {
        if (graph is null) return [];
        var normalizedFrame = frame.ToLowerInvariant();
        return graph.Nodes
            .Where(node => !node.IsSynthetic && node.Id.Length >= 4 &&
                           normalizedFrame.Contains(node.Id.ToLowerInvariant(), StringComparison.Ordinal))
            .Select(node => node.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static string ShortFrame(string frame)
    {
        var parts = frame.Split('.');
        return parts.Length <= 3 ? frame : string.Join('.', parts[^3..]);
    }

    private static double ParseNumber(string value) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;

    private static void AddOnce(
        List<DiagnosticFinding> findings,
        string rule,
        DiagnosticSeverity severity,
        string title,
        string message,
        string evidence)
    {
        if (findings.Any(finding => finding.RuleId == rule)) return;
        findings.Add(new DiagnosticFinding
        {
            RuleId = rule,
            Severity = severity,
            Title = title,
            Message = message,
            Evidence = evidence.Trim(),
        });
    }
}
