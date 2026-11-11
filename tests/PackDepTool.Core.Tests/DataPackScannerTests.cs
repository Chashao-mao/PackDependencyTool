using PackDepTool.Core.Analysis;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class DataPackScannerTests
{
    [Fact]
    public void ReportsInvalidJsonAndDuplicateIds()
    {
        using var temp = new TempDir();
        var packs = temp.Combine("datapacks", "a", "data", "demo", "recipes");
        Directory.CreateDirectory(packs);
        File.WriteAllText(Path.Combine(packs, "cake.json"), """{"type":"minecraft:crafting_shaped"}""");
        File.WriteAllText(Path.Combine(packs, "broken.json"), """{"type":""");

        var second = temp.Combine("datapacks", "b", "data", "demo", "recipes");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "cake.json"), """{"type":"minecraft:crafting_shapeless"}""");

        var report = DataPackScanner.Analyze(temp.Path);
        Assert.Contains(report.Findings, finding => finding.RuleId == "datapack-json");
        Assert.Contains(report.Findings, finding => finding.RuleId == "datapack-duplicate-id");
    }

    [Fact]
    public void MissingDatapacksDirectoryIsNotAnError()
    {
        using var temp = new TempDir();
        var report = DataPackScanner.Analyze(temp.Path);
        Assert.Empty(report.Findings);
        Assert.Contains("没有 datapacks", report.Summary);
    }
}
