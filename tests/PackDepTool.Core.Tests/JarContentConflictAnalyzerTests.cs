using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class JarContentConflictAnalyzerTests
{
    [Fact]
    public async Task FindsDuplicateClassesAndResourceOverrides()
    {
        using var temp = new TempDir();
        var first = temp.Combine("mods", "first.jar");
        var second = temp.Combine("mods", "second.jar");
        TestJar.Write(first,
            ("com/example/Shared.class", "class"),
            ("assets/demo/lang/en_us.json", "short"));
        TestJar.Write(second,
            ("com/example/Shared.class", "class"),
            ("assets/demo/lang/en_us.json", "a longer value"));

        var scan = new ScanResult { RootPath = temp.Path, ModsDirectory = temp.Combine("mods") };
        scan.Jars.Add(Jar(first, "first", "First"));
        scan.Jars.Add(Jar(second, "second", "Second"));

        var report = await new JarContentConflictAnalyzer().AnalyzeAsync(scan);

        Assert.Contains(report.Findings, finding => finding.RuleId == "duplicate-class");
        Assert.Contains(report.Findings, finding => finding.RuleId == "resource-override");
        Assert.Contains(report.Findings, finding => finding.RelatedModIds.Contains("second"));
        Assert.Contains(report.Conflicts, conflict => conflict.Kind == JarConflictKind.DuplicateClass);
    }

    [Fact]
    public async Task ReportsAccessTransformerConflict()
    {
        using var temp = new TempDir();
        var first = temp.Combine("mods", "first.jar");
        var second = temp.Combine("mods", "second.jar");
        TestJar.Write(first, ("META-INF/accesstransformer.cfg", "public a.B"));
        TestJar.Write(second, ("META-INF/accesstransformer.cfg", "public c.D"));

        var scan = new ScanResult { RootPath = temp.Path, ModsDirectory = temp.Combine("mods") };
        scan.Jars.Add(Jar(first, "first", "First"));
        scan.Jars.Add(Jar(second, "second", "Second"));

        var report = await new JarContentConflictAnalyzer().AnalyzeAsync(scan);
        Assert.Contains(report.Findings, finding => finding.RuleId == "duplicate-access-transformer");
        Assert.Contains(report.Conflicts, conflict => conflict.Kind == JarConflictKind.AccessTransformer);
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
