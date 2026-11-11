using PackDepTool.Core.Analysis;
using PackDepTool.Core.Scanning;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class ChineseModNameTests
{
    [Fact]
    public async Task ReadsZhCnLangAsDisplayName()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("mods"));
        TestJar.Write(temp.Combine("mods", "demo.jar"),
            ("fabric.mod.json", """{"id":"demo","name":"Demo Mod","version":"1.0.0"}"""),
            ("assets/demo/lang/zh_cn.json", """{"modmenu.nameTranslation.demo":"演示模组"}"""));

        var scan = await new ModScanner().ScanAsync(temp.Path);
        var parsed = Assert.Single(scan.Jars).Mods.Single();
        Assert.Equal("演示模组", parsed.LocalizedName);

        var graph = new DependencyGraphBuilder().Build(scan).Graph;
        Assert.Equal("演示模组", graph.FindNode("demo")!.NameZh);
    }
}
