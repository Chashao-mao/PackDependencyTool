using System.IO.Compression;
using System.Text.Json;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

/// <summary>
/// On-demand JAR content scan for overlap conflicts. It deliberately walks only
/// archive entries and small mixin configs, not full class/resource bytes, so a
/// 500-mod pack remains practical to inspect.
/// </summary>
public sealed class JarContentConflictAnalyzer
{
    private const int MaxFindings = 80;
    private const int MaxMixinConfigBytes = 2 * 1024 * 1024;

    public Task<DiagnosticReport> AnalyzeAsync(
        ScanResult scan,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Analyze(scan, cancellationToken), cancellationToken);

    public DiagnosticReport Analyze(ScanResult scan, CancellationToken cancellationToken = default)
    {
        var findings = new List<DiagnosticFinding>();
        var classes = new Dictionary<string, JarOwner>(StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<string, JarOwner>(StringComparer.OrdinalIgnoreCase);
        var mixinConfigs = new Dictionary<string, JarOwner>(StringComparer.OrdinalIgnoreCase);
        var mixinTargets = new Dictionary<string, List<JarOwner>>(StringComparer.OrdinalIgnoreCase);

        foreach (var jar in scan.Jars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(jar.AbsolutePath)) continue;

            var mod = jar.Mods.FirstOrDefault();
            var owner = new JarOwner(jar.FilePath, mod?.ModId ?? string.Empty, mod?.Name ?? jar.FilePath);
            try
            {
                using var archive = ZipFile.OpenRead(jar.AbsolutePath);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(path) || path.EndsWith('/')) continue;

                    var isMixin = IsMixinConfig(path);
                    if (isMixin)
                    {
                        Register(mixinConfigs, path, owner, (existing, current) =>
                            AddFinding(findings, "duplicate-mixin-config", DiagnosticSeverity.Warning,
                                "重复的 Mixin 配置路径",
                                $"`{path}` 同时存在于 {existing.Jar} 和 {current.Jar}，加载器只会读取其中一个。",
                                path, owner));
                        TryReadMixinTargets(entry, path, owner, mixinTargets);
                        continue;
                    }

                    if (path.EndsWith(".class", StringComparison.OrdinalIgnoreCase) &&
                        !path.StartsWith("META-INF/versions/", StringComparison.OrdinalIgnoreCase) &&
                        !path.EndsWith("module-info.class", StringComparison.OrdinalIgnoreCase) &&
                        !path.EndsWith("package-info.class", StringComparison.OrdinalIgnoreCase))
                    {
                        Register(classes, path, owner, (existing, current) =>
                            AddFinding(findings, "duplicate-class", DiagnosticSeverity.Error,
                                "重复的类路径",
                                $"`{path}` 同时存在于 {existing.Jar} 和 {current.Jar}，可能导致类加载冲突。",
                                path, owner));
                    }
                    else if ((path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
                              path.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) &&
                             entry.Length >= 0)
                    {
                        Register(resources, path, owner, (existing, current) =>
                        {
                            if (entry.Length == existing.Length) return;
                            AddFinding(findings, "resource-override", DiagnosticSeverity.Info,
                                "资源路径被覆盖",
                                $"`{path}` 在 {existing.Jar}（{existing.Length} 字节）和 " +
                                $"{current.Jar}（{entry.Length} 字节）中大小不同；运行时会使用其中一个。",
                                path, owner);
                        }, entry.Length);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AddFinding(findings, "jar-unreadable", DiagnosticSeverity.Warning,
                    "JAR 无法读取", $"{jar.FilePath}：{ex.Message}", jar.FilePath, owner);
            }
        }

        foreach (var (target, owners) in mixinTargets
                     .Where(pair => pair.Value.Select(owner => owner.Jar)
                         .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                     .Take(MaxFindings))
        {
            var distinct = owners
                .GroupBy(owner => owner.Jar, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            AddFinding(findings, "mixin-target-overlap", DiagnosticSeverity.Warning,
                "多个模组注入同一 Mixin 目标",
                $"目标类 `{target}` 被 {string.Join("、", distinct.Select(owner => owner.ModName))} 同时注入，" +
                "需要确认 Mixin 优先级和是否兼容。",
                target, distinct[0], distinct.SelectMany(owner => owner.ModIds).Distinct().ToList());
        }

        var summary = findings.Count == 0
            ? "没有发现重复类、资源覆盖或 Mixin 目标重叠。"
            : $"发现 {findings.Count} 条内容冲突线索：错误 " +
              $"{findings.Count(finding => finding.Severity == DiagnosticSeverity.Error)} · 警告 " +
              $"{findings.Count(finding => finding.Severity == DiagnosticSeverity.Warning)} · 提示 " +
              $"{findings.Count(finding => finding.Severity == DiagnosticSeverity.Info)}";

        return new DiagnosticReport
        {
            SourcePath = scan.RootPath,
            Summary = summary,
            Findings = findings.Take(MaxFindings).ToList(),
        };
    }

    private static void Register(
        Dictionary<string, JarOwner> map,
        string path,
        JarOwner owner,
        Action<JarOwner, JarOwner> onDuplicate,
        long length = 0)
    {
        var current = owner with { Length = length };
        if (map.TryGetValue(path, out var existing))
        {
            onDuplicate(existing, current);
            return;
        }
        map[path] = current;
    }

    private static void TryReadMixinTargets(
        ZipArchiveEntry entry,
        string path,
        JarOwner owner,
        Dictionary<string, List<JarOwner>> targets)
    {
        if (entry.Length > MaxMixinConfigBytes) return;
        try
        {
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var package = root.TryGetProperty("package", out var packageValue) &&
                          packageValue.ValueKind == JsonValueKind.String
                ? packageValue.GetString() ?? string.Empty
                : string.Empty;

            foreach (var listName in new[] { "mixins", "client", "server" })
            {
                if (!root.TryGetProperty(listName, out var list) ||
                    list.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var mixin = item.GetString();
                    if (string.IsNullOrWhiteSpace(mixin)) continue;
                    var target = string.IsNullOrWhiteSpace(package)
                        ? mixin
                        : $"{package}.{mixin}";
                    if (!targets.TryGetValue(target, out var owners))
                        targets[target] = owners = [];
                    if (owners.All(existing => !string.Equals(existing.Jar, owner.Jar,
                            StringComparison.OrdinalIgnoreCase)))
                        owners.Add(owner);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       JsonException or InvalidDataException)
        {
            // A malformed mixin config is already reported by its owning mod's parser.
        }
    }

    private static bool IsMixinConfig(string path) =>
        path.EndsWith(".mixins.json", StringComparison.OrdinalIgnoreCase) ||
        (path.StartsWith("mixins.", StringComparison.OrdinalIgnoreCase) &&
         path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    private static void AddFinding(
        List<DiagnosticFinding> findings,
        string rule,
        DiagnosticSeverity severity,
        string title,
        string message,
        string evidence,
        JarOwner owner,
        IReadOnlyList<string>? modIds = null)
    {
        if (findings.Count >= MaxFindings) return;
        findings.Add(new DiagnosticFinding
        {
            RuleId = rule,
            Severity = severity,
            Title = title,
            Message = message,
            Evidence = evidence,
            RelatedModIds = modIds ?? (string.IsNullOrWhiteSpace(owner.ModId) ? [] : [owner.ModId]),
        });
    }

    private sealed record JarOwner(string Jar, string ModId, string ModName, long Length = 0)
    {
        public IEnumerable<string> ModIds =>
            string.IsNullOrWhiteSpace(ModId) ? [] : [ModId];
    }
}
