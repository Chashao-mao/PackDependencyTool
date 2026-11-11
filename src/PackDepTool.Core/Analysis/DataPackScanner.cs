using System.Text.Json;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

/// <summary>
/// Detects datapack JSON that fails to parse and duplicate resource ids that
/// would make one pack silently override another. This is an offline structural
/// check, not a full registry/item resolver.
/// </summary>
public static class DataPackScanner
{
    private static readonly string[] ContentFolders =
        ["recipes", "loot_tables", "advancements", "functions", "quests", "tags"];

    public static DiagnosticReport Analyze(string packRoot)
    {
        var findings = new List<DiagnosticFinding>();
        var root = Path.GetFullPath(packRoot);
        var datapacks = Path.Combine(root, "datapacks");
        if (!Directory.Exists(datapacks))
            return new DiagnosticReport
            {
                SourcePath = datapacks,
                Summary = "没有 datapacks 目录，跳过配方/战利品/任务扫描。",
            };

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(datapacks, "*.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(datapacks, file).Replace('\\', '/');
            if (!IsContentPath(relative)) continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                findings.Add(new DiagnosticFinding
                {
                    RuleId = "datapack-json",
                    Severity = DiagnosticSeverity.Error,
                    Title = "数据包 JSON 无法解析",
                    Message = $"{relative}：{ex.Message}",
                    Evidence = relative,
                });
                continue;
            }

            var id = ExtractId(relative);
            if (id is null) continue;
            if (ids.TryGetValue(id, out var existing))
            {
                findings.Add(new DiagnosticFinding
                {
                    RuleId = "datapack-duplicate-id",
                    Severity = DiagnosticSeverity.Warning,
                    Title = "数据包资源 ID 重复",
                    Message = $"{id} 同时存在于 {existing} 与 {relative}，后加载的数据包会覆盖前者。",
                    Evidence = $"{existing} / {relative}",
                });
            }
            else
            {
                ids[id] = relative;
            }
        }

        return new DiagnosticReport
        {
            SourcePath = datapacks,
            Summary = findings.Count == 0
                ? "数据包 JSON 均可解析，未发现重复资源 ID。"
                : $"数据包扫描发现 {findings.Count} 项问题。",
            Findings = findings,
        };
    }

    private static bool IsContentPath(string relative)
    {
        var parts = relative.Split('/');
        var dataIndex = Array.FindIndex(parts, part =>
            string.Equals(part, "data", StringComparison.OrdinalIgnoreCase));
        if (dataIndex < 0 || dataIndex + 2 >= parts.Length) return false;
        return ContentFolders.Contains(parts[dataIndex + 2], StringComparer.OrdinalIgnoreCase);
    }

    private static string? ExtractId(string relative)
    {
        var parts = relative.Split('/');
        var dataIndex = Array.FindIndex(parts, part =>
            string.Equals(part, "data", StringComparison.OrdinalIgnoreCase));
        if (dataIndex < 0 || dataIndex + 3 >= parts.Length) return null;
        var namespaceName = parts[dataIndex + 1];
        var folder = parts[dataIndex + 2];
        var name = string.Join('/', parts[(dataIndex + 3)..]);
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        return $"{namespaceName}:{folder}/{name}";
    }
}
