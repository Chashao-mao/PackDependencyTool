using PackDepTool.Core.Models;
using PackDepTool.Core.Scanning;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class InstanceCatalogServiceTests
{
    [Fact]
    public async Task ListsInstancesFromVersionsFolder()
    {
        using var temp = new TempDir();
        var versions = Path.Combine(temp.Path, "versions");
        var instance = Path.Combine(versions, "1.21.1-neoforge-21.1.264");
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        await File.WriteAllTextAsync(Path.Combine(instance, "1.21.1-neoforge-21.1.264.json"), """
        {
          "id": "1.21.1-neoforge-21.1.264",
          "inheritsFrom": "1.21.1",
          "libraries": [{ "name": "net.neoforged:neoforge:21.1.264" }]
        }
        """);
        await File.WriteAllBytesAsync(Path.Combine(instance, "mods", "sodium.jar"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(instance, "mods", "old.jar.disabled"), [1]);

        var result = await new InstanceCatalogService().ScanAsync(versions);
        var item = Assert.Single(result.Instances);

        Assert.Equal("1.21.1-neoforge-21.1.264", item.Name);
        Assert.Equal("1.21.1", item.MinecraftVersion);
        Assert.Equal(LoaderKind.NeoForge, item.Loader);
        Assert.Equal(1, item.ModCount);
        Assert.Equal(1, item.DisabledModCount);
    }

    [Fact]
    public void ResolvesGameFolderAndInstanceFolder()
    {
        using var temp = new TempDir();
        var versions = Path.Combine(temp.Path, "versions");
        var instance = Path.Combine(versions, "1.20.1-forge");
        Directory.CreateDirectory(instance);

        Assert.Equal(versions, InstanceCatalogService.ResolveVersionsDirectory(versions));
        Assert.Equal(versions, InstanceCatalogService.ResolveVersionsDirectory(temp.Path));
        Assert.Equal(versions, InstanceCatalogService.ResolveVersionsDirectory(instance));
    }
}
