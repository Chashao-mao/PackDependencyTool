using PackDepTool.Core.Scanning;

namespace PackDepTool.Core.Analysis;

public sealed class PackMergeResult
{
    public string SourceRoot { get; init; } = string.Empty;
    public string TargetRoot { get; init; } = string.Empty;
    public int CopiedMods { get; init; }
    public int SkippedDuplicateMods { get; init; }
    public int CopiedConfigs { get; init; }
    public int KeptExistingConfigs { get; init; }
    public string? ReportPath { get; init; }
    public List<string> Conflicts { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    public string Summary =>
        $"复制 {CopiedMods} 个模组，跳过 {SkippedDuplicateMods} 个重复，复制 {CopiedConfigs} 个配置";
}

/// <summary>
/// Merges one pack into another by modId: target wins on duplicate mods,
/// config files are non-destructive by default, and a report records conflicts.
/// This is intentionally conservative; it does not migrate MC/loader versions.
/// </summary>
public sealed class PackMergeService
{
    private static readonly string[] ConfigDirectories = ["config", "defaultconfigs", "kubejs", "scripts"];

    public async Task<PackMergeResult> MergeAsync(
        string sourceRoot,
        string targetRoot,
        bool overwriteConfigs = false,
        CancellationToken cancellationToken = default)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        targetRoot = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
        if (!Directory.Exists(targetRoot)) throw new DirectoryNotFoundException(targetRoot);
        if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("源整合包和目标整合包不能相同。");

        var scanner = new ModScanner();
        var source = await scanner.ScanAsync(sourceRoot, cancellationToken: cancellationToken);
        var target = await scanner.ScanAsync(targetRoot, cancellationToken: cancellationToken);
        var targetMods = target.Jars.SelectMany(jar => jar.Mods)
            .Where(mod => !string.IsNullOrWhiteSpace(mod.ModId))
            .GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var targetModsDirectory = Path.Combine(targetRoot, "mods");
        Directory.CreateDirectory(targetModsDirectory);
        var copied = 0;
        var skipped = 0;
        var conflicts = new List<string>();
        var warnings = new List<string>();

        foreach (var jar in source.Jars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mods = jar.Mods.Where(mod => !string.IsNullOrWhiteSpace(mod.ModId)).ToList();
            if (mods.Count == 0)
            {
                warnings.Add($"未识别模组元数据，跳过：{jar.FilePath}");
                continue;
            }

            var duplicate = mods.FirstOrDefault(mod => targetMods.ContainsKey(mod.ModId));
            if (duplicate is not null)
            {
                skipped++;
                var targetMod = targetMods[duplicate.ModId];
                conflicts.Add($"{duplicate.Name}（{duplicate.ModId}）：源 {duplicate.Version} / 目标 {targetMod.Version}");
                continue;
            }

            var sourceFile = jar.AbsolutePath;
            var destination = Path.Combine(targetModsDirectory, Path.GetFileName(sourceFile));
            if (File.Exists(destination))
            {
                skipped++;
                conflicts.Add($"{Path.GetFileName(sourceFile)}：目标已有同名文件");
                continue;
            }

            try
            {
                File.Copy(sourceFile, destination);
                copied++;
                foreach (var mod in mods)
                    targetMods[mod.ModId] = mod;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Path.GetFileName(sourceFile)}：{ex.Message}");
            }
        }

        var copiedConfigs = 0;
        var keptConfigs = 0;
        foreach (var directory in ConfigDirectories)
        {
            var sourceDirectory = Path.Combine(sourceRoot, directory);
            if (!Directory.Exists(sourceDirectory)) continue;
            copiedConfigs += CopyConfigTree(
                sourceDirectory, Path.Combine(targetRoot, directory), overwriteConfigs,
                ref keptConfigs, warnings, cancellationToken);
        }

        var reportPath = Path.Combine(targetRoot, "packdep-merge-report.md");
        var report = string.Join(Environment.NewLine, new[]
        {
            "# 整合包合并报告",
            "",
            $"- 源：`{sourceRoot}`",
            $"- 目标：`{targetRoot}`",
            $"- {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            $"- {copied} 个模组已复制",
            $"- {skipped} 个重复已跳过（目标优先）",
            $"- {copiedConfigs} 个配置已复制，{keptConfigs} 个已有配置保留",
            "",
            "## 冲突 / 跳过",
            conflicts.Count == 0 ? "没有。" : string.Join(Environment.NewLine, conflicts.Select(item => "- " + item)),
            "",
            "## 警告",
            warnings.Count == 0 ? "没有。" : string.Join(Environment.NewLine, warnings.Select(item => "- " + item)),
            "",
            "> 该合并只按 modId 去重，不迁移 Minecraft / 加载器版本；合并后请重新扫描并做依赖体检。",
        });
        File.WriteAllText(reportPath, report);

        return new PackMergeResult
        {
            SourceRoot = sourceRoot,
            TargetRoot = targetRoot,
            CopiedMods = copied,
            SkippedDuplicateMods = skipped,
            CopiedConfigs = copiedConfigs,
            KeptExistingConfigs = keptConfigs,
            ReportPath = reportPath,
            Conflicts = conflicts,
            Warnings = warnings,
        };
    }

    private static int CopyConfigTree(
        string source, string destination, bool overwrite, ref int kept,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && !overwrite)
            {
                kept++;
                continue;
            }
            try
            {
                File.Copy(file, target, overwrite);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{relative}：{ex.Message}");
            }
        }
        return copied;
    }
}
