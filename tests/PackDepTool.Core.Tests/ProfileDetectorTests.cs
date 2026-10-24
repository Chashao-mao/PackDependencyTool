using PackDepTool.Core.Models;
using PackDepTool.Core.Scanning;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class ProfileDetectorTests
{
    [Fact]
    public async Task DetectsModrinthPack()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(temp.Combine("modrinth.index.json"), """
        {
          "formatVersion": 1,
          "game": "minecraft",
          "name": "Fabulously Optimized",
          "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.11" }
        }
        """);

        var profile = new PackProfileDetector().Detect(temp.Path);

        Assert.Equal("Fabulously Optimized", profile.PackName);
        Assert.Equal("1.20.1", profile.McVersion);
        Assert.Equal(LoaderKind.Fabric, profile.Loader);
        Assert.Equal("0.15.11", profile.LoaderVersion);
        Assert.Equal("modrinth.index.json", profile.DetectedFrom);
        Assert.False(profile.IsInferred);
    }

    [Fact]
    public async Task DetectsPrismInstance()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(temp.Combine("mmc-pack.json"), """
        {
          "formatVersion": 1,
          "components": [
            { "uid": "net.minecraft", "version": "1.21.1" },
            { "uid": "net.neoforged", "version": "21.1.80" }
          ]
        }
        """);

        var profile = new PackProfileDetector().Detect(temp.Path);

        Assert.Equal("1.21.1", profile.McVersion);
        Assert.Equal(LoaderKind.NeoForge, profile.Loader);
        Assert.Equal("21.1.80", profile.LoaderVersion);
    }

    [Fact]
    public async Task DetectsCurseForgeManifest()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(temp.Combine("manifest.json"), """
        {
          "name": "CF Pack",
          "minecraft": {
            "version": "1.20.1",
            "modLoaders": [{ "id": "forge-47.2.0", "primary": true }]
          }
        }
        """);

        var profile = new PackProfileDetector().Detect(temp.Path);

        Assert.Equal("CF Pack", profile.PackName);
        Assert.Equal("1.20.1", profile.McVersion);
        Assert.Equal(LoaderKind.Forge, profile.Loader);
        Assert.Equal("47.2.0", profile.LoaderVersion);
    }

    [Fact]
    public void InfersFromModMetadataWhenNoLauncherFileExists()
    {
        using var temp = new TempDir();
        var mods = new List<ParsedMod>
        {
            new() { ModId = "a", Loader = LoaderKind.Fabric, McVersions = ["1.20.1"] },
            new() { ModId = "b", Loader = LoaderKind.Fabric, McVersions = ["1.20.1"] },
            new() { ModId = "c", Loader = LoaderKind.Forge, McVersions = ["1.20.1"] },
        };

        var profile = new PackProfileDetector().Detect(temp.Path, mods);

        Assert.Equal(LoaderKind.Fabric, profile.Loader);
        Assert.Equal("1.20.1", profile.McVersion);
        Assert.True(profile.IsInferred);
    }
}
