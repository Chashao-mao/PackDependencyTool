using System.Text.RegularExpressions;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public sealed class ServerPackEntry
{
    public ModNode Node { get; init; } = new();
    public bool Included { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string FileName => Path.GetFileName(Node.FilePath);
}

public sealed class ServerPackPlan
{
    public List<ServerPackEntry> Entries { get; init; } = [];
    public int IncludedCount => Entries.Count(entry => entry.Included);
    public int ClientOnlyCount => Entries.Count(entry => !entry.Included);
    public int UnknownSideCount => Entries.Count(entry =>
        entry.Included && entry.Node.Side == ModSide.Unknown);
}

public sealed class ServerPackBuildResult
{
    public string Destination { get; init; } = string.Empty;
    public int CopiedMods { get; init; }
    public int SkippedMods { get; init; }
    public int CopiedConfigFiles { get; init; }
    public List<string> Failures { get; init; } = [];
}

public sealed class ServerPackService
{
    public ServerPackPlan CreatePlan(GraphDocument graph)
    {
        var entries = new List<ServerPackEntry>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes
                     .Where(node => !node.IsSynthetic && !string.IsNullOrWhiteSpace(node.FilePath))
                     .OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!seenFiles.Add(node.FilePath)) continue;
            var include = node.Side != ModSide.Client;
            entries.Add(new ServerPackEntry
            {
                Node = node,
                Included = include,
                Reason = include
                    ? node.Side == ModSide.Unknown ? "侧未知，保留并提示" : "服务端可用"
                    : "仅客户端，排除",
            });
        }

        return new ServerPackPlan { Entries = entries };
    }

    public ServerPackBuildResult Build(
        ServerPackPlan plan,
        string sourceRoot,
        string destination,
        bool includeConfigs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            throw new InvalidOperationException("整合包目录不存在。");
        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException("请选择服务端包输出目录。");

        var target = Path.GetFullPath(destination);
        Directory.CreateDirectory(target);
        var mods = Path.Combine(target, "mods");
        Directory.CreateDirectory(mods);
        var copied = 0;
        var skipped = 0;
        var failures = new List<string>();

        foreach (var entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Included)
            {
                skipped++;
                continue;
            }

            try
            {
                var source = Path.IsPathRooted(entry.Node.FilePath)
                    ? entry.Node.FilePath
                    : Path.Combine(sourceRoot, entry.Node.FilePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                {
                    failures.Add($"{entry.FileName}：源文件不存在");
                    continue;
                }

                File.Copy(source, Path.Combine(mods, Path.GetFileName(source)), overwrite: true);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{entry.FileName}：{ex.Message}");
            }
        }

        var configCopied = 0;
        if (includeConfigs)
        {
            foreach (var folder in new[] { "config", "defaultconfigs", "scripts" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(sourceRoot, folder);
                if (!Directory.Exists(source)) continue;
                configCopied += CopyDirectory(source, Path.Combine(target, folder), failures);
            }
        }

        var excluded = plan.Entries.Where(entry => !entry.Included).Select(entry => entry.FileName);
        File.WriteAllText(Path.Combine(target, "server-pack-plan.txt"),
            "PackDependencyTool server pack plan" + Environment.NewLine +
            $"included: {copied}" + Environment.NewLine +
            "excluded (client-only):" + Environment.NewLine +
            string.Join(Environment.NewLine, excluded.Select(name => "  " + name)));

        return new ServerPackBuildResult
        {
            Destination = target,
            CopiedMods = copied,
            SkippedMods = skipped,
            CopiedConfigFiles = configCopied,
            Failures = failures,
        };
    }

    private static int CopyDirectory(string source, string destination, List<string> failures)
    {
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            try
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }
        return copied;
    }
}

public enum GateStatus
{
    Pass,
    Warn,
    Fail,
}

public sealed record ServerGateCheck(GateStatus Status, string Name, string Message, string Fix);

public sealed class ServerPreflightService
{
    private static readonly Regex MemoryPattern = new(@"-Xmx(\d+)([MmGg])", RegexOptions.Compiled);

    public IReadOnlyList<ServerGateCheck> Check(string instanceRoot, string? javaPath)
    {
        var checks = new List<ServerGateCheck>();
        var eulaPath = Path.Combine(instanceRoot, "eula.txt");
        if (!File.Exists(eulaPath))
            checks.Add(new ServerGateCheck(GateStatus.Fail, "EULA",
                "未找到 eula.txt", "首次启动服务端后填写 eula=true"));
        else
        {
            var text = File.ReadAllText(eulaPath);
            var accepted = Regex.IsMatch(text, @"eula\s*=\s*true", RegexOptions.IgnoreCase);
            checks.Add(new ServerGateCheck(accepted ? GateStatus.Pass : GateStatus.Fail, "EULA",
                accepted ? "已接受 EULA" : "EULA 尚未接受",
                accepted ? "无需处理" : "将 eula.txt 中的 eula 改为 true"));
        }

        var propertiesPath = Path.Combine(instanceRoot, "server.properties");
        if (!File.Exists(propertiesPath))
        {
            checks.Add(new ServerGateCheck(GateStatus.Warn, "端口",
                "未找到 server.properties", "首次启动服务端后会生成默认端口 25565"));
        }
        else
        {
            var port = ReadProperty(propertiesPath, "server-port");
            var valid = int.TryParse(port, out var number) && number is > 0 and <= 65535;
            checks.Add(new ServerGateCheck(valid ? GateStatus.Pass : GateStatus.Warn, "端口",
                valid ? $"server-port={number}" : "server-port 缺失或无效",
                valid ? "无需处理" : "设置 1–65535 之间且未被占用的端口"));
        }

        var serverJar = Directory.EnumerateFiles(instanceRoot, "*.jar", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Contains("server", StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetFileName(path).Contains("forge", StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetFileName(path).Contains("neoforge", StringComparison.OrdinalIgnoreCase));
        checks.Add(new ServerGateCheck(serverJar is not null ? GateStatus.Pass : GateStatus.Warn,
            "服务端 JAR", serverJar is null ? "未找到明确的 server/loader JAR" : Path.GetFileName(serverJar),
            serverJar is null ? "确认启动脚本指向实际服务端 JAR" : "无需处理"));

        checks.Add(new ServerGateCheck(
            !string.IsNullOrWhiteSpace(javaPath) && File.Exists(javaPath) ? GateStatus.Pass : GateStatus.Warn,
            "Java", string.IsNullOrWhiteSpace(javaPath) ? "未配置 Java 路径" : javaPath,
            "选择与服务端版本匹配的 java.exe"));

        var memory = ReadMemory(instanceRoot);
        checks.Add(new ServerGateCheck(memory is null ? GateStatus.Warn : GateStatus.Pass,
            "内存", memory is null ? "未找到 -Xmx 设置" : $"-Xmx{memory}",
            memory is null ? "在启动参数中设置 -Xmx，例如 4G" : "无需处理"));

        return checks;
    }

    private static string? ReadProperty(string path, string key)
    {
        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            if (string.Equals(line[..index].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return line[(index + 1)..].Trim();
        }
        return null;
    }

    private static string? ReadMemory(string root)
    {
        foreach (var name in new[] { "user_jvm_args.txt", "variables.txt", "start.bat", "start.sh" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            var match = MemoryPattern.Match(File.ReadAllText(path));
            if (match.Success) return match.Groups[1].Value + match.Groups[2].Value.ToUpperInvariant();
        }
        return null;
    }
}
