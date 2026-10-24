using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task ManualLinksSurviveSaveAndReload()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Combine("data"));
        var store = new ManualLinkStore(paths);
        var packRoot = temp.Combine("pack");

        await store.SaveAsync(packRoot,
        [
            new ManualLink
            {
                From = " A ",
                To = "B",
                Type = EdgeType.Incompatible,
                VersionRange = "[1.0,)",
                Side = ModSide.Client,
                Note = "test",
            },
        ]);

        Assert.True(File.Exists(store.GetStorePath(packRoot)));

        var loaded = await store.LoadAsync(packRoot);
        Assert.Single(loaded);
        Assert.Equal("a", loaded[0].From);
        Assert.Equal("b", loaded[0].To);
        Assert.Equal(EdgeType.Incompatible, loaded[0].Type);
        Assert.Equal(ModSide.Client, loaded[0].Side);

        // Second save uses the atomic replace path.
        await store.SaveAsync(packRoot, loaded);
        Assert.Single(await store.LoadAsync(packRoot));
    }

    [Fact]
    public async Task ManualLinksExportAndImport()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Combine("data"));
        var store = new ManualLinkStore(paths);
        var packRoot = temp.Combine("pack");
        var exportPath = temp.Combine("manual-links.json");

        await store.ExportAsync(exportPath, packRoot,
        [
            new ManualLink { From = "a", To = "b", Type = EdgeType.Requires },
            new ManualLink { From = "b", To = "c", Type = EdgeType.Optional, Note = "n" },
        ]);

        var result = await store.ImportAsync(exportPath);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Links.Count);
        Assert.Equal("a", result.Links[0].From);
    }

    [Fact]
    public async Task ImportCollectsInvalidRows()
    {
        using var temp = new TempDir();
        var store = new ManualLinkStore(new AppPaths(temp.Combine("data")));
        var path = temp.Combine("bad.json");

        await File.WriteAllTextAsync(path, """
        {
          "version": 1,
          "links": [
            { "from": "a", "to": "b", "type": "requires" },
            { "from": "x", "to": "x", "type": "requires" },
            { "from": "", "to": "y", "type": "optional" }
          ]
        }
        """);

        var result = await store.ImportAsync(path);

        Assert.Single(result.Links);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task PackKeyIsStableAndPathSpecific()
    {
        var key1 = ManualLinkStore.PackKey(@"C:\Packs\One\");
        var key2 = ManualLinkStore.PackKey(@"C:\Packs\One");
        var key3 = ManualLinkStore.PackKey(@"C:\Packs\Two");

        Assert.Equal(key1, key2);
        Assert.NotEqual(key1, key3);
        Assert.Equal(16, key1.Length);
    }

    [Fact]
    public async Task AtomicFileReplacesContent()
    {
        using var temp = new TempDir();
        var path = temp.Combine("file.txt");

        await AtomicFile.WriteTextAsync(path, "one");
        Assert.Equal("one", File.ReadAllText(path));

        await AtomicFile.WriteTextAsync(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal("two", AtomicFile.TryReadText(path));
    }

    [Fact]
    public async Task SettingsRoundTrip()
    {
        using var temp = new TempDir();
        var store = new SettingsStore(new AppPaths(temp.Combine("data")));

        var settings = store.Load();
        settings.Theme = "Light";
        settings.NetworkEnabled = false;
        settings.RecentPacks.Add(@"C:\Packs\One");

        await store.SaveAsync(settings);
        var loaded = store.Load();

        Assert.Equal("Light", loaded.Theme);
        Assert.False(loaded.NetworkEnabled);
        Assert.Single(loaded.RecentPacks);
    }
}
