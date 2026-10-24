using System.Text;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

/// <summary>Human-readable pack-author exports (Markdown / CSV).</summary>
public static class PackListExporter
{
    public static string ExportModListMarkdown(ScanResult scan, GraphDocument graph, string path)
    {
        var builder = new StringBuilder();
        var name = scan.Profile.PackName ?? Path.GetFileName(scan.RootPath);
        builder.AppendLine($"# {name}");
        builder.AppendLine();
        builder.AppendLine($"- 加载器：{scan.Profile.DisplayLoader}");
        builder.AppendLine($"- Minecraft：{scan.Profile.McVersion}");
        builder.AppendLine($"- 模组：{graph.Nodes.Count(node => !node.IsSynthetic)}");
        builder.AppendLine($"- 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine();
        builder.AppendLine("| 模组 | modId | 分类 | 版本 | 加载器 | 状态 | 文件 |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

        foreach (var node in graph.Nodes
                     .Where(node => !node.IsSynthetic)
                     .OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            builder.AppendLine(
                $"| {Escape(node.Name)} | {Escape(node.Id)} | {Escape(ModCategoryText.Name(node.Category))} | " +
                $"{Escape(node.DisplayVersion)} | " +
                $"{Escape(GraphText.Loader(node.Loader))} | {Escape(GraphText.Status(node.Status))} | " +
                $"{Escape(node.FilePath)} |");
        }

        builder.AppendLine();
        builder.AppendLine("由 Pack Dependency Tool 生成。");
        WriteText(path, builder);
        return path;
    }

    public static string ExportMissingMarkdown(ScanResult scan, GraphDocument graph, string path)
    {
        var builder = new StringBuilder();
        var missing = graph.Nodes.Where(node => node.Status == ModStatus.Missing).ToList();
        builder.AppendLine("# 缺失依赖清单");
        builder.AppendLine();
        builder.AppendLine($"- 整合包：{scan.Profile.PackName ?? Path.GetFileName(scan.RootPath)}");
        builder.AppendLine($"- 加载器：{scan.Profile.DisplayLoader} · Minecraft {scan.Profile.McVersion}");
        builder.AppendLine($"- 缺失项：{missing.Count}");
        builder.AppendLine();
        if (missing.Count == 0)
        {
            builder.AppendLine("没有检测到缺失依赖。");
        }
        else
        {
            builder.AppendLine("| 缺失模组 | modId | 来源 |");
            builder.AppendLine("| --- | --- | --- |");
            foreach (var node in missing.OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase))
                builder.AppendLine($"| {Escape(node.Name)} | {Escape(node.Id)} | 必需依赖 |");
        }

        WriteText(path, builder);
        return path;
    }

    public static string ExportModListCsv(ScanResult scan, GraphDocument graph, string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("name,modId,category,version,loader,status,file,chineseName");
        foreach (var node in graph.Nodes.Where(node => !node.IsSynthetic)
                     .OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            builder.AppendLine(string.Join(',',
                Csv(node.Name),
                Csv(node.Id),
                Csv(ModCategoryText.Name(node.Category)),
                Csv(node.DisplayVersion),
                Csv(GraphText.Loader(node.Loader)),
                Csv(GraphText.Status(node.Status)),
                Csv(node.FilePath),
                Csv(node.NameZh ?? string.Empty)));
        }

        WriteText(path, builder);
        return path;
    }

    public static string ExportAuditMarkdown(PackAuditReport report, string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {report.PackName} 整合包检查");
        builder.AppendLine();
        builder.AppendLine($"- 环境：{report.Profile}");
        builder.AppendLine($"- 模组：{report.ModCount} · JAR：{report.JarCount}");
        builder.AppendLine($"- 加载器：{report.LoaderText}");
        builder.AppendLine($"- 侧分布：{report.SideText}");
        builder.AppendLine($"- 功能分类：{report.CategoryText}");
        builder.AppendLine($"- 缺失：{report.MissingCount} · 冲突：{report.ConflictCount} · 版本不符：{report.MismatchCount}");
        builder.AppendLine($"- 禁用：{report.DisabledCount} · 重复 modId：{report.DuplicateModIds.Count} · 重复文件：{report.DuplicateHashCount}");
        builder.AppendLine();
        if (report.DuplicateModIds.Count > 0)
        {
            builder.AppendLine("## 重复 modId");
            builder.AppendLine();
            foreach (var group in report.DuplicateModIds)
            {
                builder.AppendLine($"### {group.Name} ({group.ModId})");
                foreach (var file in group.Files)
                    builder.AppendLine($"- `{file.RelativePath}` · {file.SizeText}");
                builder.AppendLine();
            }
        }

        if (report.DuplicateHashes.Count > 0)
        {
            builder.AppendLine("## 完全相同的 JAR");
            builder.AppendLine();
            foreach (var group in report.DuplicateHashes)
            {
                builder.AppendLine($"- SHA1 `{group.Sha1[..Math.Min(12, group.Sha1.Length)]}…` · {group.SizeText}");
                foreach (var file in group.Files)
                    builder.AppendLine($"  - `{file.RelativePath}`");
            }
            builder.AppendLine();
        }

        if (report.MissingMods.Count > 0)
        {
            builder.AppendLine("## 缺失依赖");
            builder.AppendLine();
            foreach (var missing in report.MissingMods)
                builder.AppendLine($"- {missing.Name} ({missing.Id}) · {missing.Loader}");
            builder.AppendLine();
        }

        WriteText(path, builder);
        return path;
    }

    private static void WriteText(string path, StringBuilder builder)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string Csv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n'))
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

internal static class GraphText
{
    public static string Loader(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "未知",
    };

    public static string Status(ModStatus status) => status switch
    {
        ModStatus.Ok => "正常",
        ModStatus.Missing => "缺失",
        ModStatus.Conflict => "冲突",
        ModStatus.VersionMismatch => "版本不符",
        _ => "未知",
    };
}
