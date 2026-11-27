using System.Text.RegularExpressions;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public sealed record LogDoctorIssue(
    string RuleId,
    DiagnosticSeverity Severity,
    string Title,
    string PlainExplanation,
    string Culprit,
    string SuggestedFix,
    int Count,
    string Evidence);

public sealed record LogDoctorNoise(string Signature, int Count, string Sample, string Reason);

public sealed class LogDoctorReport
{
    public string Summary { get; init; } = string.Empty;
    public List<LogDoctorIssue> Issues { get; init; } = [];
    public List<LogDoctorNoise> Noise { get; init; } = [];
    public List<string> SuspectMods { get; init; } = [];
    public int TotalLines { get; init; }
    public int NoiseLines { get; init; }
    public string SourcePath { get; init; } = string.Empty;
}

/// <summary>
/// Human-readable log triage. It deliberately reuses the conservative crash
/// rules, translates known findings, and groups repeated benign lines so real
/// errors are not buried by spam.
/// </summary>
public static class LogDoctorAnalyzer
{
    private static readonly Regex TimePrefix = new(
        @"^\s*\[[^\]]{4,40}\]\s*", RegexOptions.Compiled);
    private static readonly Regex ThreadLevel = new(
        @"^\[[^\]]*(?:INFO|WARN|ERROR|DEBUG|TRACE)[^\]]*\]\s*", RegexOptions.Compiled);
    private static readonly Regex VolatileParts = new(
        @"\b(?:[0-9a-f]{8}-[0-9a-f-]{20,}|\d+|0x[0-9a-f]+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static LogDoctorReport Analyze(string text, string sourcePath, GraphDocument? graph = null)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var baseReport = CrashLogAnalyzer.Analyze(text, sourcePath, graph);
        var issues = new List<LogDoctorIssue>();
        foreach (var finding in baseReport.Findings)
        {
            var culprit = ResolveCulprits(finding, graph);
            issues.Add(new LogDoctorIssue(
                finding.RuleId,
                finding.Severity,
                TranslateTitle(finding.RuleId, finding.Title),
                Explain(finding.RuleId, finding.Message),
                culprit,
                string.IsNullOrWhiteSpace(finding.SuggestedFix)
                    ? FixFor(finding.RuleId)
                    : finding.SuggestedFix,
                1,
                finding.Evidence));
        }

        var groups = new Dictionary<string, (int Count, string Sample, bool Error)>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var signature = Signature(line);
            if (signature.Length < 8) continue;
            var isError = line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("Exception", StringComparison.OrdinalIgnoreCase);
            if (groups.TryGetValue(signature, out var existing))
                groups[signature] = (existing.Count + 1, existing.Sample, existing.Error || isError);
            else
                groups[signature] = (1, line, isError);
        }

        var noise = groups
            .Where(pair => pair.Value.Count >= 3 && !pair.Value.Error)
            .OrderByDescending(pair => pair.Value.Count)
            .Take(12)
            .Select(pair => new LogDoctorNoise(
                pair.Key,
                pair.Value.Count,
                pair.Value.Sample.Length > 240 ? pair.Value.Sample[..240] + "…" : pair.Value.Sample,
                "重复的低风险日志；保留计数但不再淹没关键错误"))
            .ToList();

        var suspects = issues
            .Where(issue => issue.Severity >= DiagnosticSeverity.Warning)
            .SelectMany(issue => issue.Culprit.Split('、', StringSplitOptions.RemoveEmptyEntries))
            .GroupBy(id => id.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .Take(8)
            .ToList();

        var noiseLines = noise.Sum(item => item.Count);
        var summary = issues.Count == 0
            ? $"未识别到明确错误；{lines.Length} 行日志中隔离了 {noiseLines} 行重复低风险日志。"
            : $"识别到 {issues.Count} 个问题，隔离 {noiseLines} 行重复日志；" +
              (suspects.Count == 0
                  ? "暂未定位到明确模组。"
                  : $"最可疑的模组：{string.Join("、", suspects.Take(4))}。");

        return new LogDoctorReport
        {
            Summary = summary,
            Issues = issues,
            Noise = noise,
            SuspectMods = suspects,
            TotalLines = lines.Length,
            NoiseLines = noiseLines,
            SourcePath = sourcePath,
        };
    }

    public static DiagnosticReport ToDiagnosticReport(this LogDoctorReport doctor)
    {
        var findings = doctor.Issues.Select(issue => new DiagnosticFinding
        {
            RuleId = issue.RuleId,
            Severity = issue.Severity,
            Title = issue.Title,
            Message = issue.PlainExplanation +
                      (string.IsNullOrWhiteSpace(issue.Culprit)
                          ? string.Empty
                          : $"\n可疑模组：{issue.Culprit}"),
            Evidence = issue.Evidence,
            SuggestedFix = issue.SuggestedFix,
            RelatedModIds = issue.Culprit.Split('、', StringSplitOptions.RemoveEmptyEntries).ToList(),
        }).ToList();

        if (doctor.Noise.Count > 0)
        {
            findings.Add(new DiagnosticFinding
            {
                RuleId = "log-noise",
                Severity = DiagnosticSeverity.Info,
                Title = $"已隔离 {doctor.NoiseLines} 行重复噪声",
                Message = "这些重复行不是当前需要处理的主因，已从关键问题中隔离：\n" +
                          string.Join("\n", doctor.Noise.Take(6)
                              .Select(item => $"{item.Count}× {item.Signature}")),
                Evidence = string.Join("\n\n", doctor.Noise.Take(3).Select(item => item.Sample)),
            });
        }

        return new DiagnosticReport
        {
            SourcePath = doctor.SourcePath,
            Summary = doctor.Summary,
            Findings = findings,
        };
    }

    private static string TranslateTitle(string rule, string fallback) => rule switch
    {
        "missing-dependency" => "缺少必需前置，游戏无法完成加载",
        "incompatible" => "模组组合被判定为不兼容",
        "mixin" => "Mixin 注入失败，通常由版本或目标类变化引起",
        "class-not-found" => "运行时找不到类，通常是模组版本不匹配",
        "no-such-method" => "运行时找不到方法，通常是前置/本体版本错位",
        "out-of-memory" => "内存不足",
        "tick-lag" or "low-tps" or "high-mspt" => "性能不足，游戏 tick 跟不上",
        _ => fallback,
    };

    private static string Explain(string rule, string fallback) => rule switch
    {
        "missing-dependency" => "某个模组声明它必须依赖另一个模组，但当前实例里没有装。",
        "incompatible" => "两个模组明确声明不能一起运行，或加载器检查到了冲突。",
        "mixin" => "模组试图修改游戏代码时找不到预期的方法或类，常见于版本不匹配或前置过期。",
        "class-not-found" => "游戏加载到一半找不到某个类，说明某个模组/前置版本不配套。",
        "no-such-method" => "代码调用了不存在的方法，通常是本体或前置版本比模组要求的旧/新。",
        "out-of-memory" => "分配的内存不足以加载当前整合包。",
        "tick-lag" or "low-tps" or "high-mspt" => "服务端/客户端每 tick 的处理时间超过了目标，需要减少实体、区块或高开销模组。",
        _ => fallback,
    };

    private static string FixFor(string rule) => rule switch
    {
        "missing-dependency" => "按日志里的 modId 补装前置，并确认版本范围。",
        "incompatible" => "先移除冲突组合中的一个模组，再逐个回加定位。",
        "mixin" => "把相关模组和前置升级到支持当前 Minecraft 版本的版本。",
        "class-not-found" or "no-such-method" => "对齐本体、加载器和前置版本；不要只升级报错模组。",
        "out-of-memory" => "提高分配内存，并检查是否有重复加载或超大整合包。",
        "tick-lag" or "low-tps" or "high-mspt" => "用性能报告定位高耗时模组，先处理高开销与高冲突交叉项。",
        _ => "结合证据行检查对应模组版本和前置。",
    };

    private static string ResolveCulprits(DiagnosticFinding finding, GraphDocument? graph)
    {
        var ids = finding.RelatedModIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0 || graph is null) return string.Join("、", ids);

        var names = graph.Nodes
            .Where(node => ids.Contains(node.Id, StringComparer.OrdinalIgnoreCase))
            .Select(node => node.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? string.Join("、", ids) : string.Join("、", names);
    }

    private static bool IsNoise(string line) =>
        line.Contains("INFO", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Loaded ", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Skipping", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("advancement", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("recipe", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("texture", StringComparison.OrdinalIgnoreCase);

    private static string Signature(string line)
    {
        var value = TimePrefix.Replace(line, string.Empty);
        value = ThreadLevel.Replace(value, string.Empty);
        value = VolatileParts.Replace(value, "#");
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }
}
