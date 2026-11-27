using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class LogDoctorAnalyzerTests
{
    [Fact]
    public void TranslatesErrorsAndIsolatesRepeatedNoise()
    {
        var noise = string.Join('\n', Enumerable.Range(0, 6).Select(i =>
            $"[12:00:0{i}] [Render thread/INFO] Loaded texture atlas part {i}"));
        var text = $"""
            [12:00:00] [main/ERROR] Missing dependency: create requires flywheel
            {noise}
            [12:00:09] [main/ERROR] java.lang.ClassNotFoundException: com.example.Missing
            """;
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "create", Name = "Create", Version = "6.0.0" },
                new ModNode { Id = "flywheel", Name = "Flywheel", Version = "0.6.0" },
            ],
        };

        var report = LogDoctorAnalyzer.Analyze(text, "latest.log", graph);

        Assert.Contains(report.Issues, issue => issue.RuleId == "missing-dependency");
        Assert.Contains(report.Issues, issue => issue.Severity == DiagnosticSeverity.Error);
        Assert.True(report.NoiseLines >= 6);
        Assert.Contains(report.Noise, noiseGroup => noiseGroup.Count >= 6);
        Assert.Contains("Create", report.SuspectMods);
        Assert.Contains("隔离", report.Summary);
    }

    [Fact]
    public void DoesNotReportNoiseForCleanInfoOnlyLog()
    {
        var text = string.Join('\n', Enumerable.Range(0, 10).Select(_ =>
            "[12:00:00] [Render thread/INFO] Loaded texture"));

        var report = LogDoctorAnalyzer.Analyze(text, "latest.log");

        Assert.Empty(report.Issues);
        Assert.NotEmpty(report.Noise);
        Assert.Contains("未识别", report.Summary);
    }
}
