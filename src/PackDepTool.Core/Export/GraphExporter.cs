using System.Globalization;
using System.Text;
using System.Xml.Linq;
using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;

namespace PackDepTool.Core.Export;

public enum ExportFormat
{
    Json,
    Dot,
    GraphMl,
    Csv,
    MarkdownReport,
}

public static class GraphExporter
{
    private const string GraphMlNamespace = "http://graphml.graphdrawing.org/xmlns";

    private static readonly Dictionary<LoaderKind, string> LoaderColors = new()
    {
        [LoaderKind.Fabric] = "#4FB3A8",
        [LoaderKind.Quilt] = "#B36BE0",
        [LoaderKind.Forge] = "#E06C3C",
        [LoaderKind.NeoForge] = "#D9A21B",
        [LoaderKind.Unknown] = "#8A94A6",
    };

    private static readonly Dictionary<EdgeType, string> EdgeColors = new()
    {
        [EdgeType.Requires] = "#6EA8FE",
        [EdgeType.Optional] = "#7A869A",
        [EdgeType.Incompatible] = "#E5534B",
        [EdgeType.Embedded] = "#8B5CF6",
        [EdgeType.Recommends] = "#34D399",
        [EdgeType.Manual] = "#F5C542",
    };

    public static string ToJson(GraphDocument graph) => JsonDefaults.Serialize(graph);

    public static string ToDot(GraphDocument graph)
    {
        var builder = new StringBuilder();
        builder.AppendLine("digraph pack_dependencies {");
        builder.AppendLine("  graph [rankdir=LR, bgcolor=\"transparent\", fontname=\"Microsoft YaHei\", pad=0.3, nodesep=0.35, ranksep=0.8];");
        builder.AppendLine("  node [shape=box, style=\"rounded,filled\", fontname=\"Microsoft YaHei\", fontsize=11, color=\"#3A465C\", fillcolor=\"#1B2330\", fontcolor=\"#E6EDF7\", penwidth=1.2];");
        builder.AppendLine("  edge [fontname=\"Microsoft YaHei\", fontsize=9, color=\"#7A869A\", arrowsize=0.7];");

        foreach (var node in graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            var color = node.Status switch
            {
                ModStatus.Missing or ModStatus.Conflict => "#E5534B",
                ModStatus.VersionMismatch => "#F0A63C",
                ModStatus.Unknown => "#8A94A6",
                _ => LoaderColors.GetValueOrDefault(node.Loader, "#8A94A6"),
            };

            var label = $"{node.Name}\\n{node.Id}\\n{node.DisplayVersion}";
            var style = node.IsSynthetic ? "rounded,dashed,filled" : "rounded,filled";
            builder.Append("  \"").Append(EscapeDot(node.Id)).Append("\" [label=\"")
                .Append(EscapeDot(label)).Append("\", color=\"").Append(color)
                .Append("\", style=\"").Append(style).Append("\", penwidth=")
                .Append(node.IsSynthetic ? "1.6" : "1.2").AppendLine("];");
        }

        foreach (var edge in graph.Edges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal))
        {
            var color = edge.ManualOverride ? "#F5C542" : EdgeColors.GetValueOrDefault(edge.Type, "#7A869A");
            var style = edge.Type switch
            {
                EdgeType.Optional or EdgeType.Recommends => "dashed",
                EdgeType.Embedded => "dotted",
                _ => "solid",
            };

            var label = edge.Type.ToString().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(edge.VersionRange)) label += "\\n" + edge.VersionRange;
            if (edge.ManualOverride) label += "\\n(manual)";

            builder.Append("  \"").Append(EscapeDot(edge.From)).Append("\" -> \"")
                .Append(EscapeDot(edge.To)).Append("\" [label=\"").Append(EscapeDot(label))
                .Append("\", color=\"").Append(color).Append("\", style=").Append(style)
                .Append(", penwidth=").Append(edge.Type == EdgeType.Incompatible ? "2.0" : "1.4").AppendLine("];");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    public static string ToGraphMl(GraphDocument graph)
    {
        XNamespace ns = GraphMlNamespace;

        XElement Key(string id, string target, string name, string type) =>
            new(ns + "key",
                new XAttribute("id", id),
                new XAttribute("for", target),
                new XAttribute("attr.name", name),
                new XAttribute("attr.type", type));

        var keys = new[]
        {
            Key("n_name", "node", "name", "string"),
            Key("n_version", "node", "version", "string"),
            Key("n_loader", "node", "loader", "string"),
            Key("n_status", "node", "status", "string"),
            Key("n_source", "node", "source", "string"),
            Key("n_file", "node", "filePath", "string"),
            Key("n_mc", "node", "mcVersions", "string"),
            Key("n_cf", "node", "curseforgeId", "string"),
            Key("n_mr", "node", "modrinthId", "string"),
            Key("n_slug", "node", "slug", "string"),
            Key("n_embedded", "node", "embedded", "boolean"),
            Key("e_type", "edge", "type", "string"),
            Key("e_range", "edge", "versionRange", "string"),
            Key("e_side", "edge", "side", "string"),
            Key("e_source", "edge", "source", "string"),
            Key("e_confidence", "edge", "confidence", "double"),
            Key("e_note", "edge", "note", "string"),
            Key("e_manual", "edge", "manualOverride", "boolean"),
            Key("g_mc", "graph", "mcVersion", "string"),
            Key("g_loader", "graph", "loader", "string"),
            Key("g_generated", "graph", "generatedAt", "string"),
        };

        var graphElement = new XElement(ns + "graph",
            new XAttribute("id", "pack_dependencies"),
            new XAttribute("edgedefault", "directed"),
            Data(ns, "g_mc", graph.Meta.McVersion),
            Data(ns, "g_loader", graph.Meta.Loader),
            Data(ns, "g_generated", graph.Meta.GeneratedAt));

        foreach (var node in graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            graphElement.Add(new XElement(ns + "node",
                new XAttribute("id", node.Id),
                Data(ns, "n_name", node.Name),
                Data(ns, "n_version", node.Version),
                Data(ns, "n_loader", node.Loader.ToString().ToLowerInvariant()),
                Data(ns, "n_status", node.Status.ToString().ToLowerInvariant()),
                Data(ns, "n_source", node.Source.ToString().ToLowerInvariant()),
                Data(ns, "n_file", node.FilePath),
                Data(ns, "n_mc", string.Join(",", node.McVersions)),
                Data(ns, "n_cf", node.ProjectIds.CurseForge),
                Data(ns, "n_mr", node.ProjectIds.Modrinth),
                Data(ns, "n_slug", node.Slug ?? node.ProjectIds.Slug),
                Data(ns, "n_embedded", node.IsEmbedded ? "true" : "false")));
        }

        foreach (var edge in graph.Edges.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            graphElement.Add(new XElement(ns + "edge",
                new XAttribute("id", edge.Id),
                new XAttribute("source", edge.From),
                new XAttribute("target", edge.To),
                Data(ns, "e_type", edge.Type.ToString().ToLowerInvariant()),
                Data(ns, "e_range", edge.VersionRange),
                Data(ns, "e_side", edge.Side.ToString().ToLowerInvariant()),
                Data(ns, "e_source", edge.Source.ToString().ToLowerInvariant()),
                Data(ns, "e_confidence", edge.Confidence.ToString("0.##", CultureInfo.InvariantCulture)),
                Data(ns, "e_note", edge.Note),
                Data(ns, "e_manual", edge.ManualOverride ? "true" : "false")));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "graphml", keys, graphElement));

        return document.Declaration + Environment.NewLine + document.ToString();
    }

    public static (string NodesCsv, string EdgesCsv) ToCsv(GraphDocument graph)
    {
        var nodes = new StringBuilder();
        nodes.AppendLine("id,name,version,loader,status,source,filePath,mcVersions,curseforgeId,modrinthId,slug,embedded,missing");
        foreach (var node in graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            nodes.AppendLine(string.Join(',',
                Csv(node.Id), Csv(node.Name), Csv(node.Version), Csv(node.Loader.ToString().ToLowerInvariant()),
                Csv(node.Status.ToString().ToLowerInvariant()), Csv(node.Source.ToString().ToLowerInvariant()),
                Csv(node.FilePath), Csv(string.Join('|', node.McVersions)), Csv(node.ProjectIds.CurseForge),
                Csv(node.ProjectIds.Modrinth), Csv(node.Slug ?? node.ProjectIds.Slug),
                Csv(node.IsEmbedded ? "true" : "false"), Csv(node.IsSynthetic ? "true" : "false")));
        }

        var edges = new StringBuilder();
        edges.AppendLine("id,from,to,type,versionRange,side,source,confidence,note,manualOverride");
        foreach (var edge in graph.Edges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal))
        {
            edges.AppendLine(string.Join(',',
                Csv(edge.Id), Csv(edge.From), Csv(edge.To), Csv(edge.Type.ToString().ToLowerInvariant()),
                Csv(edge.VersionRange), Csv(edge.Side.ToString().ToLowerInvariant()),
                Csv(edge.Source.ToString().ToLowerInvariant()),
                Csv(edge.Confidence.ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(edge.Note), Csv(edge.ManualOverride ? "true" : "false")));
        }

        return (nodes.ToString(), edges.ToString());
    }

    public static string ToMarkdownReport(
        GraphDocument graph,
        IReadOnlyList<AnalysisIssue> issues,
        PackProfile? profile = null,
        IReadOnlyList<LocalRule>? rules = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 整合包依赖分析报告");
        builder.AppendLine();
        builder.AppendLine($"- 生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 整合包：{(string.IsNullOrWhiteSpace(graph.Meta.PackName) ? "unknown" : graph.Meta.PackName)}");
        builder.AppendLine($"- Minecraft：{graph.Meta.McVersion}");
        builder.AppendLine($"- 加载器：{graph.Meta.Loader}{(string.IsNullOrWhiteSpace(graph.Meta.LoaderVersion) ? string.Empty : " " + graph.Meta.LoaderVersion)}");
        if (profile?.DetectedFrom is not null)
            builder.AppendLine($"- 识别来源：{profile.DetectedFrom}{(profile.IsInferred ? "（推断）" : string.Empty)}");
        builder.AppendLine();

        builder.AppendLine("## 概览");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 数值 |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| 节点数 | {graph.Nodes.Count} |");
        builder.AppendLine($"| 关系数 | {graph.Edges.Count} |");
        builder.AppendLine($"| 缺失依赖 | {graph.Nodes.Count(n => n.Status == ModStatus.Missing)} |");
        builder.AppendLine($"| 版本不匹配 | {graph.Nodes.Count(n => n.Status == ModStatus.VersionMismatch)} |");
        builder.AppendLine($"| 冲突 | {graph.Nodes.Count(n => n.Status == ModStatus.Conflict)} |");
        builder.AppendLine($"| 手动关系 | {graph.Edges.Count(e => e.ManualOverride)} |");
        builder.AppendLine($"| 错误 | {issues.Count(i => i.Severity == IssueSeverity.Error)} |");
        builder.AppendLine($"| 警告 | {issues.Count(i => i.Severity == IssueSeverity.Warning)} |");
        builder.AppendLine($"| 提示 | {issues.Count(i => i.Severity == IssueSeverity.Info)} |");
        builder.AppendLine();

        var edgeTypes = graph.Edges.GroupBy(e => e.Type).OrderByDescending(g => g.Count());
        builder.AppendLine("### 关系类型分布");
        builder.AppendLine();
        builder.AppendLine("| 类型 | 数量 |");
        builder.AppendLine("| --- | ---: |");
        foreach (var group in edgeTypes)
            builder.AppendLine($"| {EdgeTypeName(group.Key)} | {group.Count()} |");
        builder.AppendLine();

        AppendIssueSection(builder, "错误", issues, IssueSeverity.Error);
        AppendIssueSection(builder, "警告", issues, IssueSeverity.Warning);
        AppendIssueSection(builder, "提示", issues, IssueSeverity.Info);

        var manual = graph.Edges.Where(e => e.ManualOverride).OrderBy(e => e.From, StringComparer.Ordinal).ToList();
        builder.AppendLine($"## 手动关系（{manual.Count}）");
        builder.AppendLine();
        if (manual.Count == 0)
        {
            builder.AppendLine("暂无手动关系。");
        }
        else
        {
            builder.AppendLine("| 起点 | 终点 | 类型 | 版本范围 | 侧 | 备注 |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var edge in manual)
            {
                builder.AppendLine($"| {Md(edge.From)} | {Md(edge.To)} | {EdgeTypeName(edge.Type)} | " +
                                   $"{Md(edge.VersionRange ?? "-")} | {SideName(edge.Side)} | {Md(edge.Note ?? "-")} |");
            }
        }
        builder.AppendLine();

        var ruleList = rules?.ToList() ?? [];
        builder.AppendLine($"## 本地规则（{ruleList.Count} 条，启用 {ruleList.Count(rule => rule.Enabled)} 条）");
        builder.AppendLine();
        if (ruleList.Count == 0)
        {
            builder.AppendLine("未配置本地规则。");
        }
        else
        {
            builder.AppendLine("| 类型 | 表达式 | 值 | 优先级 | 状态 | 备注 |");
            builder.AppendLine("| --- | --- | --- | ---: | --- | --- |");
            foreach (var rule in ruleList.OrderByDescending(rule => rule.Priority).ThenBy(rule => rule.Type))
            {
                builder.AppendLine($"| {LocalRuleText.TypeName(rule.Type)} | {Md(LocalRuleText.Describe(rule))} | " +
                                   $"{Md(rule.Value ?? "-")} | {rule.Priority} | {(rule.Enabled ? "启用" : "停用")} | " +
                                   $"{Md(rule.Note ?? "-")} |");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 缺失依赖清单");
        builder.AppendLine();
        var missing = graph.Nodes.Where(n => n.Status == ModStatus.Missing).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        if (missing.Count == 0)
        {
            builder.AppendLine("未发现缺失的必需依赖。");
        }
        else
        {
            builder.AppendLine("| modId | 名称 | 来源 | 说明 |");
            builder.AppendLine("| --- | --- | --- | --- |");
            foreach (var node in missing)
            {
                builder.AppendLine($"| {Md(node.Id)} | {Md(node.Name)} | {node.Source.ToString().ToLowerInvariant()} | " +
                                   $"{Md(node.StatusReason ?? "未安装")} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("> 由 Pack Dependency Tool 生成。联网补全信息来自 CurseForge / Modrinth 官方 API；" +
                           "无法确定的信息标记为 unknown，不会编造。");
        return builder.ToString();
    }

    public static async Task<string> ExportAsync(
        GraphDocument graph,
        IReadOnlyList<AnalysisIssue> issues,
        string path,
        ExportFormat format,
        PackProfile? profile = null,
        IReadOnlyList<LocalRule>? rules = null,
        CancellationToken cancellationToken = default)
    {
        switch (format)
        {
            case ExportFormat.Json:
                await AtomicFile.WriteTextAsync(path, ToJson(graph), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return path;

            case ExportFormat.Dot:
                await AtomicFile.WriteTextAsync(path, ToDot(graph), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return path;

            case ExportFormat.GraphMl:
                await AtomicFile.WriteTextAsync(path, ToGraphMl(graph), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return path;

            case ExportFormat.MarkdownReport:
                await AtomicFile.WriteTextAsync(
                        path, ToMarkdownReport(graph, issues, profile, rules), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return path;

            case ExportFormat.Csv:
                var (nodes, edges) = ToCsv(graph);
                var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                var name = Path.GetFileNameWithoutExtension(path);
                Directory.CreateDirectory(directory);
                var nodesPath = Path.Combine(directory, $"{name}.nodes.csv");
                var edgesPath = Path.Combine(directory, $"{name}.edges.csv");
                await AtomicFile.WriteTextAsync(nodesPath, nodes, writeBom: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await AtomicFile.WriteTextAsync(edgesPath, edges, writeBom: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return $"{nodesPath}{Environment.NewLine}{edgesPath}";

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    private static XElement Data(XNamespace ns, string key, string? value) =>
        new(ns + "data", new XAttribute("key", key), value ?? string.Empty);

    private static void AppendIssueSection(
        StringBuilder builder, string title, IReadOnlyList<AnalysisIssue> issues, IssueSeverity severity)
    {
        var selected = issues.Where(i => i.Severity == severity).ToList();
        builder.AppendLine($"## {title}（{selected.Count}）");
        builder.AppendLine();
        if (selected.Count == 0)
        {
            builder.AppendLine("无。");
            builder.AppendLine();
            return;
        }

        var index = 1;
        foreach (var issue in selected.Take(500))
        {
            builder.AppendLine($"### {index++}. {issue.Title}");
            builder.AppendLine();
            builder.AppendLine(issue.Message);
            builder.AppendLine();
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(issue.NodeId)) meta.Add($"节点：`{issue.NodeId}`");
            if (!string.IsNullOrWhiteSpace(issue.RelatedNodeId)) meta.Add($"相关节点：`{issue.RelatedNodeId}`");
            meta.Add($"类型：{issue.Kind}");
            builder.AppendLine(string.Join(" · ", meta));
            builder.AppendLine();
        }

        if (selected.Count > 500)
        {
            builder.AppendLine($"> 其余 {selected.Count - 500} 条已省略。");
            builder.AppendLine();
        }
    }

    private static string EdgeTypeName(EdgeType type) => type switch
    {
        EdgeType.Requires => "必需（requires）",
        EdgeType.Optional => "可选（optional）",
        EdgeType.Incompatible => "不兼容（incompatible）",
        EdgeType.Embedded => "内嵌（embedded）",
        EdgeType.Recommends => "推荐（recommends）",
        EdgeType.Manual => "手动（manual）",
        _ => type.ToString(),
    };

    private static string SideName(ModSide side) => side switch
    {
        ModSide.Both => "双端",
        ModSide.Client => "客户端",
        ModSide.Server => "服务端",
        _ => "未知",
    };

    private static string Md(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string EscapeDot(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", string.Empty).Replace("\n", "\\n");
}
