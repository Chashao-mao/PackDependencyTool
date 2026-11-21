using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class ProfilerReportTests
{
    [Fact]
    public void ParsesCaliperCsv()
    {
        const string csv = "name,ms,file\nsodium,120.5,sodium.jar\ncreate,30,create.jar\n";
        var report = CaliperReportParser.TryParse(csv, "caliper.csv");
        Assert.NotNull(report);
        Assert.Contains(report!.Findings, finding => finding.Title.Contains("sodium"));
        Assert.Contains(report.Findings, finding => finding.RuleId == "caliper-load-cost");
    }

    [Fact]
    public void MapsCaliperRowsToInstalledModIds()
    {
        var graph = new GraphDocument
        {
            Nodes = [new ModNode { Id = "sodium", Name = "Sodium" }],
        };
        var entries = new[] { new CaliperLoadEntry("sodium", "sodium.jar", 120) };

        var costs = CaliperReportParser.ToOwnCosts(entries, graph);

        Assert.Equal(120, costs["sodium"]);
    }

    [Fact]
    public void ParsesCaliperJson()
    {
        const string json = """[{"name":"sodium","ms":120.5}]""";
        var report = CaliperReportParser.TryParse(json, "caliper.json");
        Assert.NotNull(report);
        Assert.Contains(report!.Findings, finding => finding.Title.Contains("sodium"));
    }
}
