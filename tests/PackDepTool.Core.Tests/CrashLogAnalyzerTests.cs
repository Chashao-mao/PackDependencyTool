using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class CrashLogAnalyzerTests
{
    [Fact]
    public void DetectsKnownCrashPatternsAndRelatedMods()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "sodium", Name = "Sodium", FilePath = "mods/sodium.jar" },
                new ModNode { Id = "iris", Name = "Iris", FilePath = "mods/iris.jar" },
            ],
        };
        const string log = """
        ---- Minecraft Crash Report ----
        Missing or unsupported mandatory dependencies:
            Mod ID: 'fabric-api', Requested by: 'iris'
        Caused by: org.spongepowered.asm.mixin.transformer.MixinTransformerError: mixin apply failed
        java.lang.OutOfMemoryError: Java heap space
        java.lang.NoClassDefFoundError: net/caffeinemc/sodium/Foo
        at net.caffeinemc.sodium.Foo.bar(Foo.java:10)
        """;

        var report = CrashLogAnalyzer.Analyze(log, "crash.txt", graph);

        Assert.Contains(report.Findings, finding => finding.RuleId == "missing-dependency");
        Assert.Contains(report.Findings, finding => finding.RuleId == "mixin");
        Assert.Contains(report.Findings, finding => finding.RuleId == "oom-heap");
        Assert.Contains(report.Findings, finding => finding.RuleId == "class-missing");
        Assert.Contains(report.Findings, finding => finding.RelatedModIds.Contains("sodium"));
        Assert.All(report.Findings, finding => Assert.False(string.IsNullOrWhiteSpace(finding.Evidence)));
        Assert.Equal("崩溃报告", report.Facts?.FileKind);
    }

    [Fact]
    public void UnknownLogDoesNotFabricateFindings()
    {
        var report = CrashLogAnalyzer.Analyze("hello world\nnothing here", "log.txt");
        Assert.Empty(report.Findings);
        Assert.Contains("没有识别", report.Summary);
    }

    [Fact]
    public void DiscoversCrashReportsAndLatestLog()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "crash-reports"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "logs"));
        File.WriteAllText(Path.Combine(temp.Path, "crash-reports", "crash-1.txt"), "report");
        File.WriteAllText(Path.Combine(temp.Path, "logs", "latest.log"), "log");

        var files = CrashLogAnalyzer.Discover(temp.Path);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.Kind == "崩溃报告");
        Assert.Contains(files, file => file.Kind == "最新日志");
    }
}
