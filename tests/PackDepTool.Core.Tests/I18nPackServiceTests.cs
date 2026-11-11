using PackDepTool.Core.Scanning;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class I18nPackServiceTests
{
    [Fact]
    public async Task ReadsCfpaConvertedResourcePackAsFallback()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("mods"));
        TestJar.Write(temp.Combine("mods", "demo.jar"),
            ("fabric.mod.json", """{"id":"demo","name":"Demo","version":"1.0.0"}"""));
        TestJar.Write(temp.Combine("resourcepacks", "Minecraft-Mod-Language-Modpack-Converted-1.21.1.zip"),
            ("assets/demo/lang/zh_cn.json", """{"modmenu.nameTranslation.demo":"演示模组"}"""));

        var scan = await new ModScanner().ScanAsync(temp.Path);
        var parsed = Assert.Single(scan.Jars).Mods.Single();

        Assert.Null(parsed.LocalizedName);
        Assert.Equal("演示模组", parsed.I18nLocalizedName);
    }
}
