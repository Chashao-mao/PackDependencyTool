using System.IO.Compression;
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
    public void DetectsFeaturePlacementStreamCrash()
    {
        const string log = """
        [13Nov2026 16:46:43.517] [Server thread/ERROR] [net.minecraft.util.thread.BlockableEventLoop/FATAL]: Error executing task
        net.minecraft.ReportedException: Feature placement
        Caused by: java.lang.IllegalStateException: stream has already been operated upon or closed
        -- Feature --
        Details:
        	Description: ResourceKey[minecraft:worldgen/placed_feature / terralith:cave/fungal/hanging_roots_cluster]
        at net.minecraft.world.level.levelgen.placement.PlacedFeature.placeWithContext(PlacedFeature.java:53)
        {pl:mixin:APP:primal.mixins.json:PlacedFeatureMixin from mod primal}
        """;

        var report = CrashLogAnalyzer.Analyze(log, "latest.log");

        Assert.Contains(report.Findings, finding => finding.RuleId == "feature-placement-stream");
        Assert.Equal("terralith:cave/fungal/hanging_roots_cluster", report.Facts?.Feature);
        Assert.Contains(report.Findings, finding => finding.SuggestedFix.Contains("Primal"));
    }

    [Fact]
    public void FeatureRootCauseSurvivesRepeatedNoise()
    {
        var noise = string.Join('\n', Enumerable.Repeat(
            "Tried to load invalid item: 'Item must not be minecraft:air'", 80));
        var log = noise + """

        net.minecraft.ReportedException: Feature placement
        Caused by: java.lang.IllegalStateException: stream has already been operated upon or closed
        -- Feature --
        Details:
        	Description: ResourceKey[minecraft:worldgen/placed_feature / terralith:cave/fungal/hanging_roots_cluster]
        at net.minecraft.world.level.levelgen.placement.PlacedFeature.placeWithContext(PlacedFeature.java:53)
        {pl:mixin:APP:primal.mixins.json:PlacedFeatureMixin from mod primal}
        {pl:mixin:APP:lithostitched.mixins.json:common.PlacedFeatureAccessor from mod lithostitched}
        """;

        var report = CrashLogAnalyzer.Analyze(log, "latest.log");

        Assert.Contains(report.Findings, finding => finding.RuleId == "feature-placement-stream");
        Assert.Equal(1, report.Findings.Count(finding => finding.RuleId == "invalid-item"));
        Assert.Contains(report.Findings, finding => finding.RelatedModIds.Contains("primal"));
    }

    [Fact]
    public async Task ReadsCrashReportAndLatestLogFromZip()
    {
        using var temp = new TempDir();
        var zipPath = temp.Combine("logs.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            Write(zip, "latest.log", "latest");
            Write(zip, "debug.log", "debug");
            Write(zip, "crash-1.txt", "crash");
            Write(zip, "PCL 启动日志.txt", "pcl");
        }

        var sources = await CrashLogBundleReader.ReadAsync(zipPath);

        Assert.Equal("crash-1.txt", sources[0].Name);
        Assert.Equal("latest.log", sources[1].Name);
        Assert.DoesNotContain(sources, source => source.Name == "debug.log");
        Assert.Contains(sources, source => source.Name == "PCL 启动日志.txt");
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    [Fact]
    public void DetectsThaumaturgeJeiApiMismatch()
    {
        const string log = """
        Description: mouseReleased event handler
        java.lang.NoSuchMethodError: 'mezz.jei.api.recipe.transfer.IRecipeTransferError mezz.jei.api.recipe.transfer.IRecipeTransferHandlerHelper.transferRecipeWithInputAlternatives(mezz.jei.api.recipe.transfer.IRecipeTransferHandler, mezz.jei.api.recipe.transfer.IRecipeTransferContext, java.util.List, boolean)'
        at TRANSFORMER/thaumaturge@1.1.0/com.leclowndu93150.thaumaturge.compat.jei.ThaumaturgeJEIPlugin$ArcaneWorkbenchTransferHandler.transferRecipe(ThaumaturgeJEIPlugin.java:355)
        at TRANSFORMER/jei@19.56.0.438/mezz.jei.common.transfer.RecipeTransferService.transferRecipe(RecipeTransferService.java:91)
        -- Affected screen --
        Details:
        	Screen name: com.leclowndu93150.thaumaturge.client.screen.workbench.ArcaneWorkbenchScreen
        """;

        var report = CrashLogAnalyzer.Analyze(log, "crash-client.txt");

        Assert.Contains(report.Findings, finding => finding.RuleId == "thaumaturge-jei-api");
        Assert.Contains(report.Findings, finding => finding.RelatedModIds.Contains("thaumaturge"));
        Assert.Contains(report.Findings, finding => finding.RelatedModIds.Contains("jei"));
        Assert.Contains("ArcaneWorkbenchScreen", report.Facts?.AffectedScreen);
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
