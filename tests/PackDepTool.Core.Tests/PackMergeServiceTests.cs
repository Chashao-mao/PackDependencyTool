using PackDepTool.Core.Analysis;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class PackMergeServiceTests
{
    [Fact]
    public async Task MergesMissingModsAndKeepsTargetConfig()
    {
        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "source");
        var target = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(Path.Combine(source, "mods"));
        Directory.CreateDirectory(Path.Combine(target, "mods"));
        Directory.CreateDirectory(Path.Combine(source, "config"));
        Directory.CreateDirectory(Path.Combine(target, "config"));

        TestJar.Write(Path.Combine(source, "mods", "shared.jar"),
            ("fabric.mod.json", """{"id":"shared","name":"Shared","version":"2.0"}"""));
        TestJar.Write(Path.Combine(source, "mods", "new.jar"),
            ("fabric.mod.json", """{"id":"newmod","name":"New","version":"1.0"}"""));
        TestJar.Write(Path.Combine(target, "mods", "shared.jar"),
            ("fabric.mod.json", """{"id":"shared","name":"Shared","version":"1.0"}"""));
        File.WriteAllText(Path.Combine(source, "config", "new.toml"), "new");
        File.WriteAllText(Path.Combine(source, "config", "keep.toml"), "source");
        File.WriteAllText(Path.Combine(target, "config", "keep.toml"), "target");

        var result = await new PackMergeService().MergeAsync(source, target);

        Assert.Equal(1, result.CopiedMods);
        Assert.Equal(1, result.SkippedDuplicateMods);
        Assert.True(File.Exists(Path.Combine(target, "mods", "new.jar")));
        Assert.Equal("target", File.ReadAllText(Path.Combine(target, "config", "keep.toml")));
        Assert.True(File.Exists(result.ReportPath));
    }
}
