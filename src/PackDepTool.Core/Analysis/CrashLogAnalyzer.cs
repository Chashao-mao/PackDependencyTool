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
    public string SuggestedFix { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public IReadOnlyList<string> RelatedModIds { get; init; } = [];
    public string Confidence => Severity == DiagnosticSeverity.Error ? "高" : "中";
}

public sealed class CrashFacts
{
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string JavaVersion { get; init; } = "";
    public string Memory { get; init; } = "";
    public string PrimaryError { get; init; } = "";
    public string DeepestCause { get; init; } = "";
    public string Feature { get; init; } = "";
    public string FileKind { get; init; } = "";

    public string Summary => string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(FileKind) ? null : FileKind,
        string.IsNullOrWhiteSpace(MinecraftVersion) ? null : "MC " + MinecraftVersion,
        string.IsNullOrWhiteSpace(Loader) ? null : Loader,
        string.IsNullOrWhiteSpace(JavaVersion) ? null : "Java " + JavaVersion,
        string.IsNullOrWhiteSpace(Memory) ? null : "内存 " + Memory,
        string.IsNullOrWhiteSpace(Feature) ? null : "Feature " + Feature,
    }.Where(value => value is not null));
}

public sealed class DiagnosticReport
{
    public string SourcePath { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public CrashFacts? Facts { get; init; }
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
        var facts = ExtractFacts(text, sourcePath);

        void Add(
            int index,
            string rule,
            DiagnosticSeverity severity,
            string title,
            string message,
            string fix = "",
            string source = "")
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
                SuggestedFix = fix,
                SourceUrl = source,
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

            if (Contains(line, "KubeJS Server/ERROR") || Contains(line, "KubeJS/ERROR"))
                Add(index, "kubejs-script", DiagnosticSeverity.Warning,
                    "KubeJS 脚本错误",
                    "KubeJS 脚本在事件中抛异常；这不是本次世界生成崩溃的根因，但会持续刷屏。",
                    "按堆栈中的脚本文件和行号修复；ServerPlayer 的 UUID 建议使用 player.uuid，而不是不确定存在的 getUUID()。",
                    "https://kubejs.com/");

            if (Contains(line, "Tried to load invalid item"))
                Add(index, "invalid-item", DiagnosticSeverity.Info,
                    "无效物品引用",
                    "有数据包/模组写入了 minecraft:air 作为物品；通常与数据包版本或模组 registry 不匹配有关。",
                    "清理旧数据包/配方，检查最近更新过的模组；它不是本次 Feature placement 的根因。");

            if (Contains(line, "AllTheLeaks") || Contains(line, "Leaking objects"))
                Add(index, "alltheleaks", DiagnosticSeverity.Info,
                    "AllTheLeaks 泄漏报告",
                    "这是停服/崩溃后的残留对象报告，属于结果，不是根因。",
                    "先修复崩溃；如果正常运行时仍持续泄漏，再单独排查 AllTheLeaks。",
                    "https://www.curseforge.com/minecraft/mc-mods/alltheleaks");

            if (Contains(line, "SINYTRA CONNECTOR IS PRESENT"))
                Add(index, "sinytra-present", DiagnosticSeverity.Info,
                    "Sinytra Connector 环境",
                    "崩溃报告提示这是 Connector 环境；它可能影响 Fabric 模组的世界生成兼容性，但不是直接栈帧。",
                    "先测直接栈中的 Primal/Lithostitched；若仍复现，再移除或更新 Connector。",
                    "https://github.com/Sinytra/Connector/issues");

            if (Contains(line, "java.lang.OutOfMemoryError"))
            {
                if (Contains(line, "Java heap space"))
                    Add(index, "oom-heap", DiagnosticSeverity.Error,
                        "Java 堆内存不足",
                        "这是真正的堆内存耗尽；提高 -Xmx，或检查是否有模组/资源包大量占内存。",
                        "在设置中提高最大内存；不要只加一点，按模组量分配。",
                        "https://docs.oracle.com/en/java/javase/21/troubleshoot/troubleshoot-memory-leaks.html");
                else if (Contains(line, "Metaspace"))
                    Add(index, "oom-metaspace", DiagnosticSeverity.Error,
                        "Metaspace 耗尽",
                        "大量类/动态生成类耗尽 Metaspace；提高 -XX:MaxMetaspaceSize 并检查重复模组。",
                        "增加 -XX:MaxMetaspaceSize=512m，检查重复或版本错乱的模组。",
                        "https://docs.oracle.com/javase/8/docs/technotes/guides/vm/gctuning/considerations.html");
                else if (Contains(line, "Direct buffer memory"))
                    Add(index, "oom-direct", DiagnosticSeverity.Error,
                        "直接内存耗尽",
                        "不是 Java 堆问题；通常是渲染/网络/本地库分配过多直接内存。",
                        "不要盲目加 -Xmx；检查渲染、光影、原生库，必要时加 -XX:MaxDirectMemorySize。",
                        "https://docs.oracle.com/javase/8/docs/technotes/guides/vm/gctuning/");
                else if (Contains(line, "unable to create native thread") ||
                         Contains(line, "native thread"))
                    Add(index, "oom-native-thread", DiagnosticSeverity.Error,
                        "无法创建本地线程",
                        "这是操作系统线程/句柄限制，不是堆内存不足。",
                        "减少并发模组/线程，检查系统句柄上限；不要用提高 -Xmx 解决。",
                        "https://docs.oracle.com/javase/8/docs/technotes/guides/troubleshoot/tooldescr008.html");
                else
                    Add(index, "oom-unknown", DiagnosticSeverity.Error,
                        "内存不足（类型未识别）",
                        "日志只给出 OutOfMemoryError，未说明是堆、Metaspace、直接内存还是线程；需要看完整堆栈。",
                        "先确认 OOM 类型，再决定加堆内存还是排查本地资源。");
            }

            if (Contains(line, "UnsupportedClassVersionError"))
                Add(index, "java-too-old", DiagnosticSeverity.Error,
                    "Java 版本过低",
                    "模组/加载器编译的 class 版本高于当前 Java；启动器选错了 Java。",
                    "换用与该 MC 版本匹配的 Java（1.21 通常 Java 21），不要用 Java 8 启动新版。",
                    "https://minecraft.wiki/w/Java_Edition_version_history");

            if (Contains(line, "Duplicate mods") || Contains(line, "DuplicateModsFoundException"))
                Add(index, "duplicate-mods", DiagnosticSeverity.Error,
                    "重复模组",
                    "同一个 modId 被多个 JAR 提供，加载器拒绝启动。",
                    "只保留一个版本，检查 mods 目录和嵌套 JAR。",
                    "https://docs.neoforged.net/docs/gettingstarted/");

            if (Contains(line, "Problematic frame") || Contains(line, "EXCEPTION_ACCESS_VIOLATION"))
            {
                var driver = Contains(line, "nvoglv") || Contains(line, "nvidia") ? "NVIDIA"
                    : Contains(line, "atio") || Contains(line, "amd") ? "AMD"
                    : Contains(line, "ig") || Contains(line, "intel") ? "Intel"
                    : "显卡/本地库";
                Add(index, "native-driver", DiagnosticSeverity.Error,
                    "原生崩溃（可能是显卡驱动）",
                    $"崩溃发生在 Java 之外的本地代码，常见于 {driver} 驱动、OpenGL 或渲染模组。",
                    "更新/回退显卡驱动，检查光影与渲染模组；若报错来自模组本地库，逐个排查。",
                    "https://help.mojang.com/customer/portal/articles/1462666");
            }

            if (Contains(line, "Watchdog") || Contains(line, "server thread") && Contains(line, "hang"))
                Add(index, "watchdog", DiagnosticSeverity.Info,
                    "看门狗超时（症状，不一定是根因）",
                    "服务器线程长时间未 tick 会触发看门狗；可能是卡顿、时钟跳变或真正的死锁，不能直接判定模组崩溃。",
                    "先看 tick 耗时和 spark/线程转储；不要仅因 Watchdog 删除模组。",
                    "https://docs.papermc.io/paper/admin/how-to/");

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

        var featureIndex = Array.FindIndex(lines, line =>
            Contains(line, "stream has already been operated upon or closed"));
        if (featureIndex < 0)
            featureIndex = Array.FindIndex(lines, line => Contains(line, "-- Feature --"));
        if (featureIndex >= 0 &&
            Contains(text, "Feature placement") &&
            Contains(text, "PlacedFeature"))
        {
            Add(featureIndex, "feature-placement-stream", DiagnosticSeverity.Error,
                "世界生成特性放置崩溃",
                "区块生成时 PlacedFeature 重复使用了一个已消费/关闭的 Java Stream" +
                (string.IsNullOrWhiteSpace(facts.Feature) ? "。" : $"，触发特性：{facts.Feature}。") +
                " 直接栈中 PlacedFeature 被 lithostitched 的 accessor 和 primal 的 PlacedFeatureMixin 改写；Primal 是首要嫌疑。",
                "先备份世界，移除或更新 Primal；仍复现再依次测试 Lithostitched、Terralith、Sable、Sinytra Connector。不要先加内存。",
                "https://github.com/Sinytra/Connector/issues");
        }

        var summary = findings.Count == 0
            ? "没有识别到确定性的崩溃模式；日志需要人工查看。"
            : $"识别到 {findings.Count} 条线索：错误 {findings.Count(f => f.Severity == DiagnosticSeverity.Error)} · " +
              $"警告 {findings.Count(f => f.Severity == DiagnosticSeverity.Warning)}";
        if (!string.IsNullOrWhiteSpace(facts.Summary))
            summary = facts.Summary + " · " + summary;

        return new DiagnosticReport
        {
            SourcePath = sourcePath,
            Facts = facts,
            Findings = findings,
            Summary = summary,
        };
    }

    private static bool Contains(string line, string text) =>
        line.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static CrashFacts ExtractFacts(string text, string sourcePath)
    {
        string Match(string pattern) =>
            Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline) is { Success: true } match
                ? match.Groups[1].Value.Trim()
                : string.Empty;

        var mc = Match(@"Minecraft Version:\s*([^\r\n]+)");
        if (string.IsNullOrWhiteSpace(mc)) mc = Match(@"--fml\.mcVersion\s+([^\s]+)");
        var loader = Match(@"with Fabric Loader\s+([^\r\n]+)");
        if (string.IsNullOrWhiteSpace(loader))
            loader = Match(@"--fml\.(?:neoForge|forge)Version\s+([^\s]+)");
        var java = Match(@"Java Version:\s*([^,\r\n]+)");
        var memory = Match(@"-Xmx(\d+[mMgG])");
        var primary = Match(@"Exception in thread [^\r\n]*?:\s*([^\r\n]+)");
        if (string.IsNullOrWhiteSpace(primary)) primary = Match(@"Description:\s*([^\r\n]+)");
        var causes = Regex.Matches(text, @"Caused by:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
        var deepest = causes.Count == 0 ? string.Empty : causes[^1].Groups[1].Value.Trim();
        var feature = Match(@"-- Feature --[\s\S]*?ResourceKey\[[^\]]*?\s/\s([^\]]+)\]");
        if (string.IsNullOrWhiteSpace(feature))
            feature = Match(@"Description:\s*ResourceKey\[[^\]]*?\s/\s([^\]]+)\]");
        var kind = text.Contains("---- Minecraft Crash Report ----", StringComparison.OrdinalIgnoreCase)
            ? "崩溃报告"
            : text.Contains("# A fatal error has been detected", StringComparison.OrdinalIgnoreCase)
                ? "hs_err 原生崩溃"
                : Path.GetExtension(sourcePath).Equals(".log", StringComparison.OrdinalIgnoreCase)
                    ? "游戏日志"
                    : "日志";
        return new CrashFacts
        {
            FileKind = kind,
            MinecraftVersion = mc,
            Loader = loader,
            JavaVersion = java,
            Memory = memory,
            PrimaryError = primary,
            DeepestCause = deepest,
            Feature = feature,
        };
    }

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
