using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class PackAuditTests
{
    [Fact]
    public void AuditFindsDuplicateModIdsAndHashes()
    {
        using var temp = new TempDir();
        var mods = Path.Combine(temp.Combine("pack"), "mods");
        Directory.CreateDirectory(mods);
        var a = Path.Combine(mods, "a.jar");
        var b = Path.Combine(mods, "b.jar");
        File.WriteAllText(a, "same");
        File.WriteAllText(b, "same");

        var scan = new ScanResult
        {
            RootPath = temp.Combine("pack"),
            ModsDirectory = mods,
            Jars =
            [
                new JarScanResult
                {
                    FilePath = "mods/a.jar",
                    AbsolutePath = a,
                    Sha1 = "abc",
                    Mods = [new ParsedMod { ModId = "demo", Name = "Demo", Version = "1.0" }],
                },
                new JarScanResult
                {
                    FilePath = "mods/b.jar",
                    AbsolutePath = b,
                    Sha1 = "abc",
                    Mods = [new ParsedMod { ModId = "demo", Name = "Demo", Version = "1.0" }],
                },
            ],
        };
        var graph = new GraphDocument();
        graph.Nodes.Add(new ModNode { Id = "demo", Name = "Demo", FilePath = "mods/a.jar" });

        var report = new PackAuditService().Audit(scan, graph);
        Assert.Single(report.DuplicateModIds);
        Assert.Single(report.DuplicateHashes);
        Assert.Equal(1, report.DuplicateJarCount);
    }

    [Fact]
    public void MarkdownExportContainsModTable()
    {
        using var temp = new TempDir();
        var pack = temp.Combine("pack");
        var mods = Path.Combine(pack, "mods");
        Directory.CreateDirectory(mods);
        var scan = new ScanResult { RootPath = pack, ModsDirectory = mods };
        var graph = new GraphDocument();
        graph.Nodes.Add(new ModNode
        {
            Id = "sodium",
            Name = "Sodium",
            Version = "0.5.8",
            Loader = LoaderKind.Fabric,
            FilePath = "mods/sodium.jar",
        });

        var path = Path.Combine(temp.Combine("out"), "modlist.md");
        PackListExporter.ExportModListMarkdown(scan, graph, path);
        var text = File.ReadAllText(path);
        Assert.Contains("| 模组 | modId |", text);
        Assert.Contains("Sodium", text);
        Assert.Contains("mods/sodium.jar", text);
    }
}
