using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class JarContentConflictAnalyzerTests
{
    [Fact]
    public async Task FindsDuplicateClassesResourceOverridesAndMixinTargets()
    {
        using var temp = new TempDir();
        var first = temp.Combine("mods", "first.jar");
        var second = temp.Combine("mods", "second.jar");
        TestJar.Write(first,
            ("com/example/Shared.class", "class"),
            ("assets/demo/lang/en_us.json", "short"),
            ("first.mixins.json", """{"package":"com.example.mixin","mixins":["Target"]}"""));
        TestJar.Write(second,
            ("com/example/Shared.class", "class"),
            ("assets/demo/lang/en_us.json", "a longer value"),
            ("second.mixins.json", """{"package":"com.example.mixin","mixins":["Target"]}"""));

        var scan = new ScanResult { RootPath = temp.Path, ModsDirectory = temp.Combine("mods") };
        scan.Jars.Add(Jar(first, "first", "First"));
        scan.Jars.Add(Jar(second, "second", "Second"));

        var report = await new JarContentConflictAnalyzer().AnalyzeAsync(scan);

        Assert.Contains(report.Findings, finding => finding.RuleId == "duplicate-class");
        Assert.Contains(report.Findings, finding => finding.RuleId == "resource-override");
        Assert.Contains(report.Findings, finding => finding.RuleId == "mixin-target-overlap");
        Assert.Contains(report.Findings, finding => finding.RelatedModIds.Contains("first"));
    }

    [Fact]
    public async Task ReportsDuplicateMixinConfigPath()
    {
        using var temp = new TempDir();
        var first = temp.Combine("mods", "first.jar");
        var second = temp.Combine("mods", "second.jar");
        TestJar.Write(first, ("shared.mixins.json", """{"package":"a","mixins":["One"]}"""));
        TestJar.Write(second, ("shared.mixins.json", """{"package":"b","mixins":["Two"]}"""));

        var scan = new ScanResult { RootPath = temp.Path, ModsDirectory = temp.Combine("mods") };
        scan.Jars.Add(Jar(first, "first", "First"));
        scan.Jars.Add(Jar(second, "second", "Second"));

        var report = await new JarContentConflictAnalyzer().AnalyzeAsync(scan);
        Assert.Contains(report.Findings, finding => finding.RuleId == "duplicate-mixin-config");
    }

    private static JarScanResult Jar(string path, string id, string name) => new()
    {
        FilePath = $"mods/{Path.GetFileName(path)}",
        AbsolutePath = path,
        Mods = [new ParsedMod { ModId = id, Name = name, Version = "1.0" }],
    };
}
