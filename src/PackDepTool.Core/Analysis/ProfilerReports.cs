using System.Globalization;
using System.Text.Json;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public sealed record ProfilerReportFile(string Path, string Kind, DateTimeOffset ModifiedAt, long Size);

public static class ProfilerReportDiscovery
{
    private static readonly string[] Directories =
    [
        "logs/spark", "config/spark", "spark", "spark-reports",
        "logs/caliper", "caliper", "caliper-reports", "config/caliper",
        "logs",
    ];

    public static IReadOnlyList<ProfilerReportFile> Discover(string packRoot)
    {
        var results = new List<ProfilerReportFile>();
        foreach (var relative in Directories)
        {
            var directory = Path.Combine(packRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(path);
                    var extension = Path.GetExtension(path);
                    if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var lower = name.ToLowerInvariant();
                    if (!(lower.Contains("spark") || lower.Contains("caliper") ||
                          lower.Contains("profiler") || lower.Contains("profile")))
                        continue;
                    var info = new FileInfo(path);
                    results.Add(new ProfilerReportFile(
                        info.FullName,
                        lower.Contains("caliper") ? "Caliper" : "Spark",
                        info.LastWriteTimeUtc,
                        info.Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return results
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(file => file.Kind == "Caliper")
            .ThenByDescending(file => file.ModifiedAt)
            .ToList();
    }
}

public sealed record CaliperLoadEntry(string Name, string? FileName, double Milliseconds);

public static class CaliperReportParser
{
    public static IReadOnlyList<CaliperLoadEntry>? TryParseEntries(string text) =>
        TryParseJson(text) ?? TryParseCsv(text);

    /// <summary>Maps profiler rows to installed mod ids by id/name substring.</summary>
    public static IReadOnlyDictionary<string, double> ToOwnCosts(
        IEnumerable<CaliperLoadEntry> entries,
        GraphDocument graph)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes.Where(node => !node.IsSynthetic && !node.IsEmbedded))
        {
            var id = node.Id.ToLowerInvariant();
            var name = node.Name.ToLowerInvariant();
            var best = entries
                .Where(entry =>
                {
                    var text = ((entry.Name ?? string.Empty) + " " + (entry.FileName ?? string.Empty)).ToLowerInvariant();
                    return text.Contains(id) || (name.Length >= 3 && text.Contains(name));
                })
                .Select(entry => entry.Milliseconds)
                .DefaultIfEmpty(0)
                .Max();
            if (best > 0) result[node.Id] = best;
        }
        return result;
    }

    public static DiagnosticReport? TryParse(string text, string sourcePath, GraphDocument? graph = null)
    {
        var entries = TryParseEntries(text);
        if (entries is null || entries.Count == 0) return null;

        var findings = new List<DiagnosticFinding>();
        foreach (var entry in entries.OrderByDescending(item => item.Milliseconds).Take(20))
        {
            if (entry.Milliseconds < 1 || string.IsNullOrWhiteSpace(entry.Name)) continue;
            findings.Add(new DiagnosticFinding
            {
                RuleId = "caliper-load-cost",
                Severity = entry.Milliseconds >= 500 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                Title = $"加载耗时：{entry.Name}",
                Message = $"{entry.Name} 加载约 {entry.Milliseconds:0.##} ms。",
                Evidence = entry.FileName ?? entry.Name,
                RelatedModIds = FindModIds(entry.Name, graph),
            });
        }
        if (findings.Count == 0) return null;
        return new DiagnosticReport
        {
            SourcePath = sourcePath,
            Summary = $"Caliper：{entries.Count} 个模组，最慢 {entries.Max(item => item.Milliseconds):0.##} ms。",
            Findings = findings,
        };
    }

    private static List<CaliperLoadEntry>? TryParseJson(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('[')) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var entries = new List<CaliperLoadEntry>();
            Collect(document.RootElement, entries);
            return entries;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Collect(JsonElement element, List<CaliperLoadEntry> entries)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Collect(item, entries);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        string? name = null;
        string? fileName = null;
        double milliseconds = 0;
        foreach (var property in element.EnumerateObject())
        {
            var key = property.Name.ToLowerInvariant();
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                if (key is "name" or "mod" or "modid" or "mod_name") name ??= property.Value.GetString();
                if (key is "file" or "filename" or "file_name") fileName ??= property.Value.GetString();
            }
            else if (property.Value.ValueKind == JsonValueKind.Number)
            {
                if (key is "ms" or "milliseconds" or "time" or "duration" or "load_time" or "loadtime")
                    milliseconds = property.Value.GetDouble();
            }
            else if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                Collect(property.Value, entries);
            }
        }
        if (name is not null && milliseconds > 0)
            entries.Add(new CaliperLoadEntry(name, fileName, milliseconds));
    }

    private static List<CaliperLoadEntry>? TryParseCsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        if (lines.Count < 2) return null;
        var header = lines[0].Split(',');
        int nameIndex = -1, timeIndex = -1, fileIndex = -1;
        for (var i = 0; i < header.Length; i++)
        {
            var key = header[i].Trim().Trim('"').ToLowerInvariant();
            if (key is "name" or "mod" or "modid" or "mod_name") nameIndex = i;
            else if (key is "ms" or "milliseconds" or "time" or "duration" or "load_time" or "loadtime") timeIndex = i;
            else if (key is "file" or "filename" or "file_name") fileIndex = i;
        }
        if (nameIndex < 0 || timeIndex < 0) return null;

        var entries = new List<CaliperLoadEntry>();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (cells.Length <= Math.Max(nameIndex, timeIndex)) continue;
            if (!double.TryParse(cells[timeIndex].Trim().Trim('"').Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            entries.Add(new CaliperLoadEntry(
                cells[nameIndex].Trim().Trim('"'),
                fileIndex >= 0 && cells.Length > fileIndex ? cells[fileIndex].Trim().Trim('"') : null,
                value));
        }
        return entries;
    }

    private static IReadOnlyList<string> FindModIds(string name, GraphDocument? graph)
    {
        if (graph is null) return [];
        var lower = name.ToLowerInvariant();
        return graph.Nodes
            .Where(node => !node.IsSynthetic && node.Id.Length >= 3 &&
                           (lower.Contains(node.Id.ToLowerInvariant()) ||
                            lower.Contains(node.Name.ToLowerInvariant())))
            .Select(node => node.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

}
