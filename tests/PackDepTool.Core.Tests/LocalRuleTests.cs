using System.IO.Compression;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;
using PackDepTool.Core.Scanning;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class LocalRuleTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void IgnoresMissingDependency()
    {
        var scan = CreateScan(
            Mod("a", dependencies: [new ParsedDependency { ModId = "missing", Type = EdgeType.Requires }]));

        var rules = new[]
        {
            new LocalRule { Type = LocalRuleType.IgnoreMissing, From = "a", To = "missing" },
        };

        var result = new DependencyGraphBuilder().Build(scan, [], rules);
        var missing = result.Graph.FindNode("missing");

        Assert.NotNull(missing);
        Assert.Equal(ModStatus.Unknown, missing!.Status);
        Assert.DoesNotContain(result.Issues, issue => issue.Kind == IssueKind.MissingDependency);
    }

    [Fact]
    public void IgnoresConflictAndVersionMismatch()
    {
        var scan = CreateScan(
            Mod("a", dependencies:
            [
                new ParsedDependency { ModId = "b", Type = EdgeType.Requires, VersionRange = "[2.0,)" },
                new ParsedDependency { ModId = "b", Type = EdgeType.Incompatible },
            ]),
            Mod("b", version: "1.0.0"));

        var rules = new[]
        {
            new LocalRule { Type = LocalRuleType.IgnoreConflict, From = "a", To = "b" },
            new LocalRule { Type = LocalRuleType.IgnoreVersionMismatch, From = "a", To = "b" },
        };

        var result = new DependencyGraphBuilder().Build(scan, [], rules);
        var b = result.Graph.FindNode("b")!;

        Assert.Equal(ModStatus.Ok, b.Status);
        Assert.DoesNotContain(result.Issues, issue => issue.Kind == IssueKind.Incompatible);
        Assert.DoesNotContain(result.Issues, issue => issue.Kind == IssueKind.VersionMismatch);
    }

    [Fact]
    public void OverrideVersionRangeWins()
    {
        var scan = CreateScan(
            Mod("a", dependencies:
            [
                new ParsedDependency { ModId = "b", Type = EdgeType.Requires, VersionRange = "[2.0,)" },
            ]),
            Mod("b", version: "1.0.0"));

        var rules = new[]
        {
            new LocalRule
            {
                Type = LocalRuleType.OverrideVersionRange,
                From = "a",
                To = "b",
                Value = ">=1.0",
            },
        };

        var result = new DependencyGraphBuilder().Build(scan, [], rules);
        var edge = result.Graph.Edges.Single(e => e.From == "a" && e.To == "b");

        Assert.Equal(">=1.0", edge.VersionRange);
        Assert.DoesNotContain(result.Issues, issue => issue.Kind == IssueKind.VersionMismatch);
    }

    [Fact]
    public void AliasResolvesMissingDependency()
    {
        var scan = CreateScan(
            Mod("a", dependencies: [new ParsedDependency { ModId = "alias-lib", Type = EdgeType.Requires }]),
            Mod("provider", provides: ["real-lib"]));

        var rules = new[]
        {
            new LocalRule { Type = LocalRuleType.ProvideAlias, From = "alias-lib", To = "provider" },
        };

        var result = new DependencyGraphBuilder().Build(scan, [], rules);

        Assert.Null(result.Graph.FindNode("alias-lib"));
        Assert.Contains(result.Graph.Edges, edge => edge.From == "a" && edge.To == "provider");
    }

    [Fact]
    public void ExcludeRemovesNodeAndEdges()
    {
        var scan = CreateScan(
            Mod("a", dependencies: [new ParsedDependency { ModId = "b", Type = EdgeType.Requires }]),
            Mod("b"));

        var rules = new[]
        {
            new LocalRule { Type = LocalRuleType.ExcludeMod, From = "b" },
        };

        var result = new DependencyGraphBuilder().Build(scan, [], rules);

        Assert.Null(result.Graph.FindNode("b"));
        Assert.DoesNotContain(result.Graph.Edges, edge => edge.From == "b" || edge.To == "b");
        Assert.Contains(result.Issues, issue => issue.Title == "本地规则排除");
    }

    [Fact]
    public void PriorityChoosesAliasProvider()
    {
        var scan = CreateScan(
            Mod("a", dependencies: [new ParsedDependency { ModId = "alias", Type = EdgeType.Requires }]),
            Mod("low"),
            Mod("high"));

        var rules = new[]
        {
            new LocalRule { Type = LocalRuleType.ProvideAlias, From = "alias", To = "low", Priority = 1 },
            new LocalRule { Type = LocalRuleType.ProvideAlias, From = "alias", To = "high", Priority = 10 },
        };

        var result = new DependencyGraphBuilder().Build(scan, [], rules);
        Assert.Contains(result.Graph.Edges, edge => edge.From == "a" && edge.To == "high");
    }

    [Fact]
    public async Task RuleStoreRoundTripAndImportValidation()
    {
        using var temp = new TempDir();
        var store = new LocalRuleStore(new AppPaths(temp.Combine("data")));
        var pack = temp.Combine("pack");

        await store.SaveAsync(pack,
        [
            new LocalRule { Type = LocalRuleType.ExcludeMod, From = " A ", Priority = 5 },
            new LocalRule { Type = LocalRuleType.IgnoreConflict, From = "a", To = "b" },
        ]);

        var loaded = await store.LoadAsync(pack);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("a", loaded[0].From);
        Assert.Equal(5, loaded[0].Priority);

        var exportPath = temp.Combine("rules.json");
        await store.ExportAsync(exportPath, pack, loaded);
        var imported = await store.ImportAsync(exportPath);
        Assert.Empty(imported.Errors);
        Assert.Equal(2, imported.Rules.Count);

        var badPath = temp.Combine("bad-rules.json");
        await File.WriteAllTextAsync(badPath, """
        {
          "rules": [
            { "type": "ignoreMissing", "from": "a", "to": "" },
            { "type": "excludeMod", "from": "" }
          ]
        }
        """);
        var bad = await store.ImportAsync(badPath);
        Assert.Empty(bad.Rules);
        Assert.Equal(2, bad.Errors.Count);
    }

    [Fact]
    public async Task ScannerExtractsIconFromJar()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("mods"));
        var path = temp.Combine("mods", "icon-mod.jar");

        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open()))
            {
                writer.Write("""
                {
                  "schemaVersion": 1,
                  "id": "icon-mod",
                  "name": "Icon Mod",
                  "version": "1.0.0",
                  "icon": "icon.png"
                }
                """);
            }

            using var iconStream = archive.CreateEntry("icon.png").Open();
            iconStream.Write(OnePixelPng);
        }

        var scan = await new ModScanner().ScanAsync(temp.Path);
        var parsed = scan.Jars.Single().Mods.Single();

        Assert.NotNull(parsed.IconData);
        Assert.Equal(OnePixelPng, parsed.IconData);

        var result = new DependencyGraphBuilder().Build(scan);
        Assert.Equal(OnePixelPng, result.Graph.FindNode("icon-mod")!.IconData);
    }

    private static ScanResult CreateScan(params ParsedMod[] mods)
    {
        var scan = new ScanResult
        {
            RootPath = "C:/pack",
            ModsDirectory = "C:/pack/mods",
            Profile = new PackProfile { McVersion = "1.20.1", Loader = LoaderKind.Fabric },
        };

        var index = 0;
        foreach (var mod in mods)
        {
            scan.Jars.Add(new JarScanResult
            {
                FilePath = $"mods/{mod.ModId}.jar",
                AbsolutePath = $"C:/pack/mods/{mod.ModId}.jar",
                Sha1 = $"sha{index++}",
                Mods = [mod],
            });
        }

        return scan;
    }

    private static ParsedMod Mod(
        string id,
        string version = "1.0.0",
        List<ParsedDependency>? dependencies = null,
        List<string>? provides = null) => new()
    {
        ModId = id,
        Name = id,
        Version = version,
        Loader = LoaderKind.Fabric,
        Dependencies = dependencies ?? [],
        Provides = provides ?? [],
        IconData = OnePixelPng,
    };
}
