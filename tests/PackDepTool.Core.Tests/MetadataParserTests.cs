using PackDepTool.Core.Models;
using PackDepTool.Core.Scanning;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class MetadataParserTests
{
    private readonly ModMetadataParser _parser = new();

    [Fact]
    public void ParsesFabricMetadata()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "id": "cool-mod",
          "version": "1.2.3",
          "name": "Cool Mod",
          "description": "A cool mod",
          "authors": ["Alice", { "name": "Bob" }],
          "environment": "client",
          "provides": ["coolmod-api"],
          "icon": { "16": "icon_16.png", "128": "icon_128.png" },
          "depends": {
            "minecraft": ">=1.20 <1.21",
            "fabricloader": ">=0.15",
            "other-mod": ["1.0.x", "2.0.x"]
          },
          "breaks": { "bad-mod": "*" },
          "recommends": { "nice-mod": ">=3" },
          "suggests": { "extra-mod": "*" },
          "jars": [{ "file": "META-INF/jars/embedded.jar" }]
        }
        """;

        var mod = _parser.ParseFabric(json);

        Assert.Equal("cool-mod", mod.ModId);
        Assert.Equal("Cool Mod", mod.Name);
        Assert.Equal("1.2.3", mod.Version);
        Assert.Equal(LoaderKind.Fabric, mod.Loader);
        Assert.Equal(ModSide.Client, mod.Side);
        Assert.Equal("Alice, Bob", mod.Authors);
        Assert.Contains("coolmod-api", mod.Provides);
        Assert.Equal(">=1.20 <1.21", mod.McVersionRange);
        Assert.Empty(mod.McVersions);
        Assert.Equal("META-INF/jars/embedded.jar", mod.Embedded[0].File);
        Assert.Equal("icon_128.png", mod.IconEntry);

        var required = mod.Dependencies.Single(d => d.ModId == "other-mod");
        Assert.Equal(EdgeType.Requires, required.Type);
        Assert.Equal("1.0.x || 2.0.x", required.VersionRange);
        Assert.Equal(EdgeType.Incompatible, mod.Dependencies.Single(d => d.ModId == "bad-mod").Type);
        Assert.Equal(EdgeType.Recommends, mod.Dependencies.Single(d => d.ModId == "nice-mod").Type);
        Assert.Equal(EdgeType.Optional, mod.Dependencies.Single(d => d.ModId == "extra-mod").Type);
    }

    [Fact]
    public void ParsesForgeTomlWithMultipleModsAndDependencies()
    {
        const string toml = """
        modLoader="javafml"
        loaderVersion="[47,)"
        license="MIT"
        logoFile="logo.png"

        [[mods]]
        modId="alpha"
        version="1.0.0"
        displayName="Alpha"
        description="Alpha mod"
        authors="Alice"

        [[mods]]
        modId="beta"
        version="2.0.0"
        displayName="Beta"

        [[dependencies.alpha]]
        modId="beta"
        mandatory=true
        versionRange="[2.0,3.0)"
        ordering="NONE"
        side="BOTH"

        [[dependencies.alpha]]
        modId="opt"
        mandatory=false
        versionRange="[1.0,)"
        side="CLIENT"
        """;

        var mods = _parser.ParseForgeToml(toml, "mods.toml", neoForge: false);

        Assert.Equal(2, mods.Count);
        var alpha = mods.Single(m => m.ModId == "alpha");
        Assert.Equal("Alpha", alpha.Name);
        Assert.Equal(LoaderKind.Forge, alpha.Loader);
        Assert.Equal("logo.png", alpha.IconEntry);
        Assert.Equal(2, alpha.Dependencies.Count);

        var beta = alpha.Dependencies.Single(d => d.ModId == "beta");
        Assert.Equal(EdgeType.Requires, beta.Type);
        Assert.Equal("[2.0,3.0)", beta.VersionRange);
        Assert.Equal(ModSide.Both, beta.Side);

        var optional = alpha.Dependencies.Single(d => d.ModId == "opt");
        Assert.Equal(EdgeType.Optional, optional.Type);
        Assert.Equal(ModSide.Client, optional.Side);
    }

    [Fact]
    public void ParsesNeoForgeDependencyTypes()
    {
        const string toml = """
        modLoader="javafml"
        loaderVersion="[1,)"
        license="MIT"

        [[mods]]
        modId="neo"
        version="1.0"
        displayName="Neo"

        [[dependencies.neo]]
        modId="oldmod"
        type="incompatible"
        versionRange="*"
        side="BOTH"

        [[dependencies.neo]]
        modId="newlib"
        type="required"
        versionRange="[2.0,)"
        side="BOTH"
        """;

        var mod = _parser.ParseForgeToml(toml, "neoforge.mods.toml", neoForge: true).Single();

        Assert.Equal(LoaderKind.NeoForge, mod.Loader);
        Assert.Equal(EdgeType.Incompatible, mod.Dependencies.Single(d => d.ModId == "oldmod").Type);
        Assert.Equal(EdgeType.Requires, mod.Dependencies.Single(d => d.ModId == "newlib").Type);
    }

    [Fact]
    public void ParsesQuiltMetadata()
    {
        const string json = """
        {
          "schema_version": 1,
          "quilt_loader": {
            "group": "com.example",
            "id": "quilt-mod",
            "version": "3.1.0",
            "metadata": {
              "name": "Quilt Mod",
              "description": "desc",
              "contributors": { "Alice": "Owner", "Bob": "Dev" }
            },
            "depends": [
              { "id": "minecraft", "versions": ">=1.20.1" },
              { "id": "required-lib", "versions": ">=1.0" },
              { "id": "optional-lib", "versions": "*", "optional": true }
            ],
            "breaks": [
              { "id": "bad-lib", "versions": "<2.0" }
            ],
            "provides": ["qapi"]
          }
        }
        """;

        var mod = _parser.ParseQuilt(json);

        Assert.Equal("quilt-mod", mod.ModId);
        Assert.Equal(LoaderKind.Quilt, mod.Loader);
        Assert.Equal("Quilt Mod", mod.Name);
        Assert.Equal("Alice, Bob", mod.Authors);
        Assert.Equal(">=1.20.1", mod.McVersionRange);
        Assert.Equal(EdgeType.Requires, mod.Dependencies.Single(d => d.ModId == "required-lib").Type);
        Assert.Equal(EdgeType.Optional, mod.Dependencies.Single(d => d.ModId == "optional-lib").Type);
        Assert.Equal(EdgeType.Incompatible, mod.Dependencies.Single(d => d.ModId == "bad-lib").Type);
    }

    [Fact]
    public void ParsesLegacyMcmodInfo()
    {
        const string json = """
        [
          {
            "modid": "legacy",
            "name": "Legacy Mod",
            "version": "1.0",
            "mcversion": "1.12.2",
            "authorList": ["Alice"],
            "dependencies": ["forge@[14.23,)", "other@[1.0,2.0)"]
          }
        ]
        """;

        var mod = _parser.ParseLegacyMcmodInfo(json).Single();

        Assert.Equal("legacy", mod.ModId);
        Assert.Equal(LoaderKind.Forge, mod.Loader);
        Assert.Equal("1.12.2", mod.McVersions.Single());
        Assert.Equal("[14.23,)", mod.Dependencies.Single(d => d.ModId == "forge").VersionRange);
        Assert.Equal(2, mod.Dependencies.Count);
    }

    [Fact]
    public void InvalidFabricMetadataThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => _parser.ParseFabric("{}"));
        Assert.Throws<FormatException>(() => _parser.ParseFabric("not json"));
    }
}
