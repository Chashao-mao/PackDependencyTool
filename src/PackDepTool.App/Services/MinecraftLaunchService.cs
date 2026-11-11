using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace PackDepTool.App.Services;

public sealed class MinecraftLaunchService
{
    public bool IsValidOfflineName(string name) =>
        name.Length is >= 3 and <= 16 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    public IReadOnlyList<PackDepTool.Core.Analysis.DiagnosticFinding> Diagnose(
        string gameRoot, string versionName, string? javaPath = null)
    {
        var findings = new List<PackDepTool.Core.Analysis.DiagnosticFinding>();
        var versionDir = Path.Combine(gameRoot, "versions", versionName);
        void Add(PackDepTool.Core.Analysis.DiagnosticSeverity severity, string title, string message) =>
            findings.Add(new PackDepTool.Core.Analysis.DiagnosticFinding
            {
                RuleId = "launch-preflight",
                Severity = severity,
                Title = title,
                Message = message,
                Evidence = versionDir,
            });

        if (!Directory.Exists(versionDir))
            Add(PackDepTool.Core.Analysis.DiagnosticSeverity.Error, "实例目录不存在", versionDir);
        else
        {
            var hasJson = File.Exists(Path.Combine(versionDir, versionName + ".json")) ||
                          File.Exists(Path.Combine(versionDir, "version.json")) ||
                          Directory.EnumerateFiles(versionDir, "*.json", SearchOption.TopDirectoryOnly).Any();
            if (!hasJson)
                Add(PackDepTool.Core.Analysis.DiagnosticSeverity.Error, "版本 JSON 缺失",
                    "实例目录没有版本 JSON，CmlLib 无法构建启动参数。");
            var hasJar = File.Exists(Path.Combine(versionDir, versionName + ".jar")) ||
                         File.Exists(Path.Combine(versionDir, "version.jar"));
            if (!hasJar)
                Add(PackDepTool.Core.Analysis.DiagnosticSeverity.Warning, "客户端 JAR 未在实例目录",
                    "如果该版本依赖继承版本，可忽略；否则需要补全客户端 JAR。");
        }

        if (ResolveJavaPath(gameRoot, javaPath) is null)
            Add(PackDepTool.Core.Analysis.DiagnosticSeverity.Error, "找不到 Java",
                "请在 设置 → 离线登录 → Java 路径 指定 java.exe，或安装 JDK/JRE。");

        var libraries = Path.Combine(gameRoot, "libraries");
        if (!Directory.Exists(libraries) || !Directory.EnumerateFiles(libraries, "*", SearchOption.AllDirectories).Any())
            Add(PackDepTool.Core.Analysis.DiagnosticSeverity.Error, "libraries 目录为空",
                "启动前需要完整的 libraries；请先用启动器补全该实例。");

        var assets = Path.Combine(gameRoot, "assets");
        if (!Directory.Exists(assets))
            Add(PackDepTool.Core.Analysis.DiagnosticSeverity.Error, "assets 目录缺失",
                "启动前需要 assets；请先用启动器补全该实例。");

        return findings;
    }

    public async Task<Process> LaunchOfflineAsync(
        string gameRoot,
        string versionName,
        string instanceRoot,
        string offlineUsername,
        int maximumRamMb,
        string? javaPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidOfflineName(offlineUsername))
            throw new InvalidOperationException("离线用户名需为 3–16 位字母、数字或下划线。");
        if (!Directory.Exists(gameRoot))
            throw new InvalidOperationException($"游戏目录不存在：{gameRoot}");

        var blocking = Diagnose(gameRoot, versionName, javaPath)
            .Where(finding => finding.Severity == PackDepTool.Core.Analysis.DiagnosticSeverity.Error)
            .Select(finding => finding.Title + "：" + finding.Message)
            .ToList();
        if (blocking.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, blocking));

        var path = new MinecraftPath(gameRoot);
        var launcher = new MinecraftLauncher(path);
        var java = ResolveJavaPath(gameRoot, javaPath);
        if (java is null)
            throw new InvalidOperationException(
                "未找到可用的 java.exe。请在 设置 → 离线登录 → Java 路径 中指定。");
        EnsureChineseLanguage(instanceRoot);
        var option = new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(offlineUsername),
            MaximumRamMb = Math.Clamp(maximumRamMb, 512, 65536),
            JavaPath = java,
            ExtraGameArguments =
            [
                new MArgument(["--gameDir", instanceRoot]),
                new MArgument(["--lang", "zh_cn"]),
            ],
        };
        var process = await launcher.BuildProcessAsync(versionName, option)
            .AsTask()
            .WaitAsync(cancellationToken);
        process.Start();
        return process;
    }

    private static void EnsureChineseLanguage(string instanceRoot)
    {
        var path = Path.Combine(instanceRoot, "options.txt");
        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
            var index = lines.FindIndex(line => line.StartsWith("lang:", StringComparison.OrdinalIgnoreCase));
            if (index >= 0) lines[index] = "lang:zh_cn";
            else lines.Add("lang:zh_cn");
            Directory.CreateDirectory(instanceRoot);
            File.WriteAllLines(path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Launch can still proceed; the extra --lang argument also covers language.
        }
    }

    public static string? ResolveJavaPath(string gameRoot, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var candidates = new List<string>();
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            AddCandidate(candidates, Path.Combine(javaHome, "bin", "java.exe"));

        foreach (var runtime in SafeEnumerateDirectories(Path.Combine(gameRoot, "runtime")))
            foreach (var java in SafeEnumerateJava(runtime))
                AddCandidate(candidates, java);

        foreach (var root in new[]
                 {
                     @"C:\Program Files\Java",
                     @"C:\Program Files\Eclipse Adoptium",
                     @"C:\Program Files\Microsoft",
                     @"C:\Program Files\BellSoft",
                 })
        {
            foreach (var directory in SafeEnumerateDirectories(root))
                AddCandidate(candidates, Path.Combine(directory, "bin", "java.exe"));
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            AddCandidate(candidates, Path.Combine(entry.Trim(), "java.exe"));

        if (candidates.Count == 0) return null;
        return candidates
            .Select(path => (Path: path, Major: GetJavaMajor(path)))
            .OrderByDescending(item => item.Major)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .First().Path;
    }

    private static void AddCandidate(List<string> candidates, string path)
    {
        if (File.Exists(path) && !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
            candidates.Add(path);
    }

    private static IEnumerable<string> SafeEnumerateJava(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "java.exe", SearchOption.AllDirectories)
                .Take(8).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int GetJavaMajor(string javaPath)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return 0;
            var text = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            var match = System.Text.RegularExpressions.Regex.Match(text, @"version ""([0-9]+)(?:\.([0-9]+))?");
            if (!match.Success) return 0;
            var major = int.Parse(match.Groups[1].Value);
            return major == 1 && int.TryParse(match.Groups[2].Value, out var legacy) ? legacy : major;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? SafeFindJava(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
