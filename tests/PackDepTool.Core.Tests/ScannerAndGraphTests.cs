using System.IO.Compression;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using PackDepTool.Core.Scanning;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class ScannerAndGraphTests
{
    [Fact]
    public async Task ScansFabricPackAndBuildsAnalysis()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(temp.Combine("modrinth.index.json"), """
        {
          "formatVersion": 1,
          "game": "minecraft",
          "name": "Test Pack",
          "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.11" }
        }
        """);

        TestJar.Write(temp.Combine("mods", "a.jar"), ("fabric.mod.json", """
        {
          "schemaVersion": 1,
          "id": "a",
          "name": "Mod A",
          "version": "1.0.0",
          "depends": { "minecraft": ">=1.20", "missinglib": "[2.0,)" }
        }
        """));

        TestJar.Write(temp.Combine("mods", "b.jar"), ("fabric.mod.json", """
        {
          "schemaVersion": 1,
          "id": "b",
          "name": "Mod B",
          "version": "1.5.0",
          "depends": { "a": ">=1.0" }
        }
        """));

        var scanner = new ModScanner();
        var scan = await scanner.ScanAsync(temp.Path);

        Assert.Equal(2, scan.Jars.Count);
        Assert.Equal("1.20.1", scan.Profile.McVersion);
        Assert.Equal(LoaderKind.Fabric, scan.Profile.Loader);
        Assert.Equal("Test Pack", scan.Profile.PackName);

        var result = new DependencyGraphBuilder().Build(scan);
        var graph = result.Graph;

        Assert.Equal(3, graph.Nodes.Count); // a, b, missinglib
        Assert.NotNull(graph.FindNode("a"));
        Assert.NotNull(graph.FindNode("b"));
        var missing = graph.FindNode("missinglib");
        Assert.NotNull(missing);
        Assert.Equal(ModStatus.Missing, missing!.Status);

        Assert.Contains(graph.Edges, e => e.From == "b" && e.To == "a" && e.Type == EdgeType.Requires);
        Assert.Contains(graph.Edges, e => e.From == "a" && e.To == "missinglib");
        Assert.Contains(result.Issues, i => i.Kind == IssueKind.MissingDependency && i.NodeId == "missinglib");
        Assert.Equal("fabric", graph.Meta.Loader);
        Assert.Equal("1.20.1", graph.Meta.McVersion);
    }

    [Fact]
    public async Task ParsesEmbeddedFabricJar()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("mods"));

        var nestedJar = TestJar.Build(("fabric.mod.json", """
        { "schemaVersion": 1, "id": "nested", "name": "Nested", "version": "1.0.0" }
        """));

        var path = temp.Combine("mods", "parent.jar");
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open()))
            {
                writer.Write("""
                {
                  "schemaVersion": 1,
                  "id": "parent",
                  "name": "Parent",
                  "version": "2.0.0",
                  "jars": [{ "file": "META-INF/jars/nested.jar" }]
                }
                """);
            }

            var nestedEntry = archive.CreateEntry("META-INF/jars/nested.jar");
            using var nestedStream = nestedEntry.Open();
            nestedStream.Write(nestedJar);
        }

        var scan = await new ModScanner().ScanAsync(temp.Path);
        var result = new DependencyGraphBuilder().Build(scan);

        Assert.NotNull(result.Graph.FindNode("nested"));
        Assert.True(result.Graph.FindNode("nested")!.IsEmbedded);
        Assert.Contains(result.Graph.Edges, e => e.From == "parent" && e.To == "nested" && e.Type == EdgeType.Embedded);
    }

    [Fact]
    public async Task ScansForgeTomlJar()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(temp.Combine("manifest.json"), """
        {
          "name": "Forge Pack",
          "minecraft": {
            "version": "1.20.1",
            "modLoaders": [{ "id": "forge-47.2.0", "primary": true }]
          }
        }
        """);

        TestJar.Write(temp.Combine("mods", "alpha.jar"), ("META-INF/mods.toml", """
        modLoader="javafml"
        loaderVersion="[47,)"
        license="MIT"

        [[mods]]
        modId="alpha"
        version="1.0.0"
        displayName="Alpha"

        [[dependencies.alpha]]
        modId="beta"
        mandatory=true
        versionRange="[2.0,)"
        side="BOTH"
        """));

        var scan = await new ModScanner().ScanAsync(temp.Path);
        var result = new DependencyGraphBuilder().Build(scan);

        Assert.Equal(LoaderKind.Forge, scan.Profile.Loader);
        Assert.Equal("47.2.0", scan.Profile.LoaderVersion);
        Assert.Equal("1.20.1", scan.Profile.McVersion);
        Assert.NotNull(result.Graph.FindNode("alpha"));
        var beta = result.Graph.FindNode("beta");
        Assert.NotNull(beta);
        Assert.Equal(ModStatus.Missing, beta!.Status);
    }

    [Fact]
    public void ManualLinksOverrideAutoEdgesAndResolveProvides()
    {
        var scan = new ScanResult
        {
            RootPath = "C:/pack",
            ModsDirectory = "C:/pack/mods",
            Profile = new PackProfile { McVersion = "1.20.1", Loader = LoaderKind.Fabric, PackName = "P" },
        };

        scan.Jars.Add(Jar("mods/a.jar", new ParsedMod
        {
            ModId = "a",
            Name = "A",
            Version = "1.0.0",
            Loader = LoaderKind.Fabric,
            Provides = ["api"],
            Dependencies =
            [
                new ParsedDependency { ModId = "b", Type = EdgeType.Requires, VersionRange = "[3.0,4.0)" },
                new ParsedDependency { ModId = "d", Type = EdgeType.Incompatible },
            ],
        }));
        scan.Jars.Add(Jar("mods/b.jar", new ParsedMod
        {
            ModId = "b", Name = "B", Version = "1.5.0", Loader = LoaderKind.Fabric,
        }));
        scan.Jars.Add(Jar("mods/c.jar", new ParsedMod
        {
            ModId = "c",
            Name = "C",
            Version = "1.0.0",
            Loader = LoaderKind.Fabric,
            Dependencies =
            [
                new ParsedDependency { ModId = "a", Type = EdgeType.Requires },
                new ParsedDependency { ModId = "b", Type = EdgeType.Requires, VersionRange = "[2.0,)" },
            ],
        }));
        scan.Jars.Add(Jar("mods/d.jar", new ParsedMod
        {
            ModId = "d", Name = "D", Version = "1.0.0", Loader = LoaderKind.Fabric,
        }));
        scan.Jars.Add(Jar("mods/e.jar", new ParsedMod
        {
            ModId = "e",
            Name = "E",
            Version = "1.0.0",
            Loader = LoaderKind.Fabric,
            Dependencies = [new ParsedDependency { ModId = "api", Type = EdgeType.Requires }],
        }));

        var manual = new List<ManualLink>
        {
            new() { From = "a", To = "b", Type = EdgeType.Optional, Note = "user override" },
            new() { From = "a", To = "ghost", Type = EdgeType.Requires },
            new() { From = "a", To = "a", Type = EdgeType.Requires },
        };

        var result = new DependencyGraphBuilder().Build(scan, manual);
        var graph = result.Graph;

        var manualEdge = graph.Edges.Single(e => e.From == "a" && e.To == "b");
        Assert.True(manualEdge.ManualOverride);
        Assert.Equal(EdgeType.Optional, manualEdge.Type);
        Assert.DoesNotContain(graph.Edges, e => e.From == "a" && e.To == "b" && !e.ManualOverride);

        Assert.Contains(graph.Edges, e => e.From == "e" && e.To == "a" && e.Type == EdgeType.Requires);

        var ghost = graph.FindNode("ghost");
        Assert.NotNull(ghost);
        Assert.Equal(ModStatus.Missing, ghost!.Status);

        var d = graph.FindNode("d")!;
        Assert.Equal(ModStatus.Conflict, d.Status);
        Assert.Contains(result.Issues, i => i.Kind == IssueKind.Incompatible);
        Assert.Contains(result.Issues, i => i.Kind == IssueKind.MissingDependency && i.NodeId == "ghost");
        Assert.Contains(result.Issues, i => i.Kind == IssueKind.VersionMismatch && i.NodeId == "b");
        Assert.Contains(result.Issues, i => i.Kind == IssueKind.InvalidManualLink);
        Assert.Equal(ModStatus.VersionMismatch, graph.FindNode("b")!.Status);
    }

    [Fact]
    public void DetectsCyclesAndDeduplicatesThem()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "a", Name = "A" },
                new ModNode { Id = "b", Name = "B" },
                new ModNode { Id = "c", Name = "C" },
            ],
            Edges =
            [
                new DependencyEdge { Id = "1", From = "a", To = "b", Type = EdgeType.Requires },
                new DependencyEdge { Id = "2", From = "b", To = "c", Type = EdgeType.Requires },
                new DependencyEdge { Id = "3", From = "c", To = "a", Type = EdgeType.Requires },
                new DependencyEdge { Id = "4", From = "a", To = "c", Type = EdgeType.Optional },
            ],
        };

        var issues = new GraphAnalyzer().Analyze(graph, new PackProfile { McVersion = "1.20.1" });
        var cycles = issues.Where(i => i.Kind == IssueKind.Cycle).ToList();
        Assert.Single(cycles);
        Assert.Contains("A", cycles[0].Message);
    }

    [Fact]
    public void UnknownVersionRangeDoesNotCreateMismatch()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "a", Name = "A", Version = "1.0" },
                new ModNode { Id = "b", Name = "B", Version = "1.0" },
            ],
            Edges =
            [
                new DependencyEdge
                {
                    Id = "1", From = "a", To = "b", Type = EdgeType.Requires, VersionRange = "[[[",
                },
            ],
        };

        var issues = new GraphAnalyzer().Analyze(graph, new PackProfile());
        Assert.DoesNotContain(issues, i => i.Kind == IssueKind.VersionMismatch);
    }

    [Fact]
    public void OptionalVersionMismatchDoesNotMarkTargetAsBroken()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "a", Name = "A", Version = "1.0", Status = ModStatus.Ok },
                new ModNode { Id = "b", Name = "B", Version = "1.0", Status = ModStatus.Ok },
            ],
            Edges =
            [
                new DependencyEdge
                {
                    Id = "1", From = "a", To = "b", Type = EdgeType.Optional, VersionRange = "[2.0,)",
                },
            ],
        };

        var issues = new GraphAnalyzer().Analyze(graph, new PackProfile());
        Assert.Equal(ModStatus.Ok, graph.FindNode("b")!.Status);
        Assert.Contains(issues, issue => issue.Kind == IssueKind.VersionMismatch &&
                                         issue.Severity == IssueSeverity.Info);
    }

    [Fact]
    public void InactiveConflictDeclarationDoesNotSetConflictStatus()
    {
        var scan = new ScanResult
        {
            RootPath = "C:/pack",
            ModsDirectory = "C:/pack/mods",
            Profile = new PackProfile { McVersion = "1.21.1", Loader = LoaderKind.NeoForge },
        };
        scan.Jars.Add(Jar("mods/a.jar", new ParsedMod
        {
            ModId = "a", Name = "A", Version = "1.0", Loader = LoaderKind.NeoForge,
            Dependencies = [new ParsedDependency
            {
                ModId = "b", Type = EdgeType.Incompatible, VersionRange = "[2.0,)",
            }],
        }));
        scan.Jars.Add(Jar("mods/b.jar", new ParsedMod
        {
            ModId = "b", Name = "B", Version = "1.0", Loader = LoaderKind.NeoForge,
        }));

        var result = new DependencyGraphBuilder().Build(scan);
        Assert.Equal(ModStatus.Ok, result.Graph.FindNode("a")!.Status);
        Assert.Equal(ModStatus.Ok, result.Graph.FindNode("b")!.Status);
        Assert.DoesNotContain(result.Issues, issue => issue.Kind == IssueKind.Incompatible);
    }

    private static JarScanResult Jar(string path, ParsedMod mod) => new()
    {
        FilePath = path,
        AbsolutePath = path,
        Sha1 = Guid.NewGuid().ToString("N"),
        Mods = [mod],
    };

    [Fact]
    public async Task ResolvesForgeFileJarVersionPlaceholderFromManifest()
    {
        using var temp = new TempDir();
        TestJar.Write(temp.Combine("mods", "patchouli.jar"),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version: 1.21.1-93\n\n"),
            ("META-INF/neoforge.mods.toml", """
            modLoader="javafml"
            loaderVersion="[1,)"
            license="MIT"
            [[mods]]
            modId="patchouli"
            version="${file.jarVersion}"
            displayName="Patchouli"
            mcVersion="[1.21, 1.21.1)"
            """));

        var scan = await new ModScanner().ScanAsync(temp.Combine(""));
        var mod = Assert.Single(scan.Jars.SelectMany(jar => jar.Mods));
        Assert.Equal("patchouli", mod.ModId);
        Assert.Equal("1.21.1-93", mod.Version);
        Assert.Equal("[1.21, 1.21.1)", mod.McVersionRange);
    }

    [Fact]
    public async Task RecognizesFmlLibraryJarByFilenameInsteadOfUnknownPlaceholder()
    {
        using var temp = new TempDir();
        TestJar.Write(temp.Combine("mods", "kotlinforforge-5.12.0-all.jar"),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nFMLModType: LIBRARY\n\n"));

        var scan = await new ModScanner().ScanAsync(temp.Combine(""));
        var jar = Assert.Single(scan.Jars);
        var mod = Assert.Single(jar.Mods);
        Assert.Equal("kotlinforforge", mod.ModId);
        Assert.Equal("5.12.0", mod.Version);
        Assert.True(jar.Warnings.Count == 0);
    }
}
