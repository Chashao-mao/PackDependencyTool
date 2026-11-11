using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class ServerPackServiceTests
{
    private static GraphDocument Graph() => new()
    {
        Nodes =
        [
            new ModNode { Id = "both", Name = "Both", Side = ModSide.Both, FilePath = "mods/both.jar" },
            new ModNode { Id = "server", Name = "Server", Side = ModSide.Server, FilePath = "mods/server.jar" },
            new ModNode { Id = "client", Name = "Client", Side = ModSide.Client, FilePath = "mods/client.jar" },
            new ModNode { Id = "unknown", Name = "Unknown", Side = ModSide.Unknown, FilePath = "mods/unknown.jar" },
        ],
    };

    [Fact]
    public void PlanExcludesClientOnlyAndKeepsUnknownWithFlag()
    {
        var plan = new ServerPackService().CreatePlan(Graph());
        Assert.Equal(3, plan.IncludedCount);
        Assert.Equal(1, plan.ClientOnlyCount);
        Assert.Equal(1, plan.UnknownSideCount);
        Assert.Contains(plan.Entries, entry => entry.Node.Id == "client" && !entry.Included);
    }

    [Fact]
    public void BuildCopiesIncludedModsAndConfig()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("mods"));
        Directory.CreateDirectory(temp.Combine("config"));
        File.WriteAllText(temp.Combine("mods", "both.jar"), "both");
        File.WriteAllText(temp.Combine("mods", "server.jar"), "server");
        File.WriteAllText(temp.Combine("mods", "client.jar"), "client");
        File.WriteAllText(temp.Combine("mods", "unknown.jar"), "unknown");
        File.WriteAllText(temp.Combine("config", "test.toml"), "config");

        var service = new ServerPackService();
        var result = service.Build(service.CreatePlan(Graph()), temp.Path,
            temp.Combine("server"), includeConfigs: true);

        Assert.Equal(3, result.CopiedMods);
        Assert.Equal(1, result.SkippedMods);
        Assert.True(File.Exists(temp.Combine("server", "mods", "both.jar")));
        Assert.False(File.Exists(temp.Combine("server", "mods", "client.jar")));
        Assert.True(File.Exists(temp.Combine("server", "config", "test.toml")));
        Assert.True(File.Exists(temp.Combine("server", "server-pack-plan.txt")));
    }

    [Fact]
    public void GateChecksEulaPortAndMemory()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("eula.txt"), "eula=true");
        File.WriteAllText(temp.Combine("server.properties"), "server-port=25565\n");
        File.WriteAllText(temp.Combine("user_jvm_args.txt"), "-Xmx4G\n");
        File.WriteAllText(temp.Combine("server.jar"), "jar");

        var checks = new ServerPreflightService().Check(temp.Path, null);
        Assert.Contains(checks, check => check.Name == "EULA" && check.Status == GateStatus.Pass);
        Assert.Contains(checks, check => check.Name == "端口" && check.Status == GateStatus.Pass);
        Assert.Contains(checks, check => check.Name == "内存" && check.Message == "-Xmx4G");
        Assert.Contains(checks, check => check.Name == "Java" && check.Status == GateStatus.Warn);
    }
}
