using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class MixinJarConflictTests
{
    [Fact]
    public async Task FindsRealMixinTargetOverlapFromClassAnnotations()
    {
        using var temp = new TempDir();
        var first = temp.Combine("mods", "first.jar");
        var second = temp.Combine("mods", "second.jar");
        var mixin = MixinAnnotationReaderTests.BuildMixinClass();
        TestJar.WriteBytes(first,
            ("first.mixins.json", """{"package":"com.example","mixins":["TestMixin"]}"""u8.ToArray()),
            ("com/example/TestMixin.class", mixin));
        TestJar.WriteBytes(second,
            ("second.mixins.json", """{"package":"com.example","mixins":["TestMixin"]}"""u8.ToArray()),
            ("com/example/TestMixin.class", mixin));

        var scan = new ScanResult { RootPath = temp.Path, ModsDirectory = temp.Combine("mods") };
        scan.Jars.Add(Jar(first, "first", "First"));
        scan.Jars.Add(Jar(second, "second", "Second"));

        var report = await new JarContentConflictAnalyzer().AnalyzeAsync(scan);

        Assert.Contains(report.Conflicts, conflict =>
            conflict.Kind == JarConflictKind.MixinTarget &&
            conflict.Evidence.Contains("net.minecraft.client.Minecraft", StringComparison.OrdinalIgnoreCase));
    }

    private static JarScanResult Jar(string path, string id, string name) => new()
    {
        FilePath = $"mods/{Path.GetFileName(path)}",
        AbsolutePath = path,
        Mods = [new ParsedMod { ModId = id, Name = name, Version = "1.0" }],
    };
}
