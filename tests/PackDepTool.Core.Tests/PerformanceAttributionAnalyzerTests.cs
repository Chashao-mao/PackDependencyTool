using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class PerformanceAttributionAnalyzerTests
{
    [Fact]
    public void ExtractsSparkHotspotsAndRelatedMod()
    {
        const string log = """
        [Spark] Profiler results:
        [Spark] 42.5% com.simibubi.create.content.kinetics.Foo.tick
        [Spark] 12.0% net.minecraft.server.MinecraftServer.tick
        20.0 TPS
        """;
        var graph = new GraphDocument
        {
            Nodes = [new ModNode { Id = "create", Name = "Create", FilePath = "mods/create.jar" }],
        };

        var report = PerformanceAttributionAnalyzer.Analyze(log, "spark.log", graph);
        var hotspot = Assert.Single(report.Findings,
            finding => finding.RuleId == "performance-hotspot" &&
                       finding.Title.Contains("Foo.tick"));
        Assert.Contains("create", hotspot.RelatedModIds);
    }

    [Fact]
    public void ReportsTickLagAndHighMsptOnce()
    {
        const string log = """
        [Server thread/WARN]: Can't keep up! Is the server overloaded?
        [Server] 80.0 ms/tick
        [Server] 80.0 ms/tick
        """;

        var report = PerformanceAttributionAnalyzer.Analyze(log, "latest.log");
        Assert.Contains(report.Findings, finding => finding.RuleId == "tick-lag");
        Assert.Single(report.Findings, finding => finding.RuleId == "high-mspt");
    }

    [Fact]
    public void UnknownLogHasNoFabricatedAttribution()
    {
        var report = PerformanceAttributionAnalyzer.Analyze("hello world", "log.txt");
        Assert.Empty(report.Findings);
        Assert.Contains("没有识别", report.Summary);
    }
}
