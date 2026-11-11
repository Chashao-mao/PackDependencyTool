using System.Text.RegularExpressions;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

public sealed class DiagnosticFinding
{
    public DiagnosticSeverity Severity { get; init; }
    public string RuleId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public IReadOnlyList<string> RelatedModIds { get; init; } = [];
    public string Confidence => Severity == DiagnosticSeverity.Error ? "高" : "中";
}

public sealed class DiagnosticReport
{
    public string SourcePath { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public List<DiagnosticFinding> Findings { get; init; } = [];
}

public sealed class DiagnosticLogFile
{
    public string Path { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public DateTimeOffset ModifiedAt { get; init; }
    public long Size { get; init; }
}

/// <summary>
/// Finds crash reports and latest logs, then applies deliberately conservative
/// rules. Findings cite the exact log line as evidence; unmatched logs produce
/// no fabricated diagnosis.
/// </summary>
public static class CrashLogAnalyzer
{
    private const int MaxLines = 20000;

    public static IReadOnlyList<DiagnosticLogFile> Discover(string packRoot)
    {
        var files = new List<DiagnosticLogFile>();
        AddDirectory(files, Path.Combine(packRoot, "crash-reports"), "*.txt", "崩溃报告");
        AddDirectory(files, Path.Combine(packRoot, "logs"), "latest.log", "最新日志");
        AddDirectory(files, Path.Combine(packRoot, "logs"), "debug.log", "调试日志");
        return files.OrderByDescending(file => file.ModifiedAt).ToList();
    }

    private static void AddDirectory(
        List<DiagnosticLogFile> files, string directory, string pattern, string kind)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(path);
                files.Add(new DiagnosticLogFile
                {
                    Path = info.FullName,
                    Kind = kind,
                    ModifiedAt = info.LastWriteTimeUtc,
                    Size = info.Length,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing/unreadable log directory is not an analysis failure.
        }
    }

    public static DiagnosticReport Analyze(
        string text,
        string sourcePath,
        GraphDocument? graph = null)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > MaxLines)
            lines = lines[..MaxLines];

        var findings = new List<DiagnosticFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(
            int index,
            string rule,
            DiagnosticSeverity severity,
            string title,
            string message)
        {
            if (findings.Count >= 50) return;
            var evidenceStart = Math.Max(0, index - 2);
            var evidenceEnd = Math.Min(lines.Length - 1, index + 2);
            var evidence = string.Join(Environment.NewLine,
                lines[evidenceStart..(evidenceEnd + 1)].Select(line => line.TrimEnd()));
            var key = rule + "\u0000" + evidence;
            if (!seen.Add(key)) return;

            findings.Add(new DiagnosticFinding
            {
                RuleId = rule,
                Severity = severity,
                Title = title,
                Message = message,
                Evidence = evidence,
                RelatedModIds = ExtractRelatedMods(text, graph),
            });
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (Contains(line, "Missing or unsupported mandatory dependencies") ||
                Contains(line, "Missing dependency") ||
                Contains(line, "requires") && Contains(line, "which is not installed"))
            {
                Add(index, "missing-dependency", DiagnosticSeverity.Error,
                    "缺失必需依赖",
                    "日志明确报告必需依赖未安装；建议按日志中的 modId 补装或检查依赖版本。");
            }

            if (Contains(line, "Incompatible mod set") ||
                Contains(line, "incompatible mods") ||
                Contains(line, "conflicting mods"))
            {
                Add(index, "incompatible", DiagnosticSeverity.Error,
                    "模组声明或实际不兼容",
                    "日志中出现不兼容报告；优先检查无法启动的组合，而不是所有声明冲突。");
            }

            if (Contains(line, "Mixin apply failed") ||
                Contains(line, "MixinTransformerError") ||
                Contains(line, "org.spongepowered.asm.mixin.transformer"))
            {
                Add(index, "mixin", DiagnosticSeverity.Error,
                    "Mixin 注入失败",
                    "通常由目标类被其他模组改写、Mixin 版本不匹配或模组版本与 MC 不匹配引起。");
            }

            if (Contains(line, "java.lang.OutOfMemoryError"))
            {
                Add(index, "out-of-memory", DiagnosticSeverity.Error,
                    "内存不足",
                    "日志显示 Java 堆耗尽；需要提高 -Xmx 或排查内存泄漏，不是模组依赖问题。");
            }

            if (Contains(line, "NoClassDefFoundError") ||
                Contains(line, "ClassNotFoundException"))
            {
                Add(index, "class-missing", DiagnosticSeverity.Warning,
                    "类缺失",
                    "常见于依赖未安装、模组版本不匹配或 Java 版本错误；结合前置和堆栈判断。");
            }

            if (Contains(line, "NoSuchMethodError") ||
                Contains(line, "NoSuchFieldError"))
            {
                Add(index, "api-mismatch", DiagnosticSeverity.Warning,
                    "模组 / API 版本不匹配",
                    "堆栈显示调用了当前版本不存在的成员，优先检查前置和加载器版本。");
            }

            if (Contains(line, "Failed to load mods") ||
                Contains(line, "Loading errors encountered"))
            {
                Add(index, "mod-load", DiagnosticSeverity.Warning,
                    "模组加载失败",
                    "日志中存在加载错误；查看相邻堆栈和涉及的模组文件。");
            }

            if (Contains(line, "GLFW error") || Contains(line, "Failed to create window"))
            {
                Add(index, "graphics", DiagnosticSeverity.Warning,
                    "图形初始化失败",
                    "优先检查显卡驱动、OpenGL 版本和渲染模组，不是依赖缺失。");
            }
        }

        var summary = findings.Count == 0
            ? "没有识别到确定性的崩溃模式；日志需要人工查看。"
            : $"识别到 {findings.Count} 条线索：错误 {findings.Count(f => f.Severity == DiagnosticSeverity.Error)} · " +
              $"警告 {findings.Count(f => f.Severity == DiagnosticSeverity.Warning)}";

        return new DiagnosticReport
        {
            SourcePath = sourcePath,
            Findings = findings,
            Summary = summary,
        };
    }

    private static bool Contains(string line, string text) =>
        line.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ExtractRelatedMods(string text, GraphDocument? graph)
    {
        if (graph is null) return [];

        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes.Where(node => !node.IsSynthetic))
        {
            if (!string.IsNullOrWhiteSpace(node.Id) &&
                Regex.IsMatch(text, $@"(?<![\w-]){Regex.Escape(node.Id)}(?![\w-])",
                    RegexOptions.IgnoreCase))
                related.Add(node.Id);

            if (!string.IsNullOrWhiteSpace(node.FilePath))
            {
                var fileName = Path.GetFileName(node.FilePath);
                if (!string.IsNullOrWhiteSpace(fileName) &&
                    text.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                    related.Add(node.Id);
            }
        }

        return related.Take(12).ToList();
    }
}
