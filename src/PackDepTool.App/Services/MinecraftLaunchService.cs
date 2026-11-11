using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace PackDepTool.App.Services;

public sealed class MinecraftLaunchService
{
    public bool IsValidOfflineName(string name) =>
        name.Length is >= 3 and <= 16 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    public async Task<Process> LaunchOfflineAsync(
        string gameRoot,
        string versionName,
        string offlineUsername,
        int maximumRamMb,
        string? javaPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidOfflineName(offlineUsername))
            throw new InvalidOperationException("离线用户名需为 3–16 位字母、数字或下划线。");
        if (!Directory.Exists(gameRoot))
            throw new InvalidOperationException($"游戏目录不存在：{gameRoot}");

        var path = new MinecraftPath(gameRoot);
        var launcher = new MinecraftLauncher(path);
        var java = ResolveJavaPath(gameRoot, javaPath);
        if (java is null)
            throw new InvalidOperationException(
                "未找到可用的 java.exe。请在 设置 → 离线登录 → Java 路径 中指定。");
        var option = new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(offlineUsername),
            MaximumRamMb = Math.Clamp(maximumRamMb, 512, 65536),
            JavaPath = java,
        };
        var process = await launcher.BuildProcessAsync(versionName, option)
            .AsTask()
            .WaitAsync(cancellationToken);
        process.Start();
        return process;
    }

    private static string? ResolveJavaPath(string gameRoot, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var candidate = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var runtime in SafeEnumerateDirectories(Path.Combine(gameRoot, "runtime")))
        {
            var candidate = SafeFindJava(runtime);
            if (candidate is not null) return candidate;
        }

        foreach (var root in new[]
                 {
                     @"C:\Program Files\Java",
                     @"C:\Program Files\Eclipse Adoptium",
                     @"C:\Program Files\Microsoft",
                     @"C:\Program Files\BellSoft",
                 })
        {
            foreach (var directory in SafeEnumerateDirectories(root))
            {
                var candidate = Path.Combine(directory, "bin", "java.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry.Trim(), "java.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
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
