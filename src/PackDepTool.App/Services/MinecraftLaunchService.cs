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
        var option = new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(offlineUsername),
            MaximumRamMb = Math.Clamp(maximumRamMb, 512, 65536),
        };
        if (!string.IsNullOrWhiteSpace(javaPath))
            option.JavaPath = javaPath;

        var process = await launcher.BuildProcessAsync(versionName, option)
            .AsTask()
            .WaitAsync(cancellationToken);
        process.Start();
        return process;
    }
}
