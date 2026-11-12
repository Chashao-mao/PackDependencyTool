using System.Xml.Linq;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Export;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class ExporterTests
{
    private static GraphDocument SampleGraph() => new()
    {
        Meta = new GraphMeta
        {
            McVersion = "1.20.1",
            Loader = "fabric",
            GeneratedAt = "2025-01-01T00:00:00.000Z",
            PackName = "Demo",
        },
        Nodes =
        [
            new ModNode
            {
                Id = "a", Name = "Mod A", Version = "1.0", Loader = LoaderKind.Fabric,
                ProjectIds = new ProjectIds { Modrinth = "abc", CurseForge = "123", Slug = "mod-a" },
                FilePath = "mods/a.jar",
            },
            new ModNode
            {
                Id = "b", Name = "Mod B", Version = "2.0", Loader = LoaderKind.Forge,
                Status = ModStatus.Missing, IsSynthetic = true,
            },
        ],
        Edges =
        [
            new DependencyEdge
            {
                Id = "e1", From = "a", To = "b", Type = EdgeType.Requires,
                VersionRange = "[2.0,)", Side = ModSide.Both, Note = "line1\nline2, with comma",
            },
            new DependencyEdge
            {
                Id = "e2", From = "b", To = "a", Type = EdgeType.Optional, ManualOverride = true,
            },
        ],
    };

    [Fact]
    public void JsonRoundTrips()
    {
        var json = GraphExporter.ToJson(SampleGraph());
        var parsed = JsonDefaults.Deserialize<GraphDocument>(json);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Nodes.Count);
        Assert.Equal("fabric", parsed.Meta.Loader);
        Assert.Equal(EdgeType.Requires, parsed.Edges[0].Type);
        Assert.Equal(ModSide.Both, parsed.Edges[0].Side);
    }

    [Fact]
    public void DotContainsGraphAndStyles()
    {
        var dot = GraphExporter.ToDot(SampleGraph());
        Assert.StartsWith("digraph pack_dependencies", dot);
        Assert.Contains("\"a\" -> \"b\"", dot);
        Assert.Contains("style=dashed", dot);
        Assert.Contains("#F5C542", dot);
    }

    [Fact]
    public void GraphMlIsValidXml()
    {
        var xml = GraphExporter.ToGraphMl(SampleGraph());
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://graphml.graphdrawing.org/xmlns";

        Assert.Equal(ns + "graphml", document.Root!.Name);
        Assert.Equal(2, document.Descendants(ns + "node").Count());
        Assert.Equal(2, document.Descendants(ns + "edge").Count());
        Assert.Contains(document.Descendants(ns + "data"), d => d.Value == "requires");
    }

    [Fact]
    public void CsvEscapesCommasAndNewlines()
    {
        var (nodes, edges) = GraphExporter.ToCsv(SampleGraph());
        Assert.Contains("id,name,nameZh,version", nodes);
        Assert.Contains(",side,sideInferred,category,", nodes);
        Assert.Contains(",requires,optional,incompatible,embeddedDeps,recommends", nodes);
        Assert.Contains("\"line1\nline2, with comma\"", edges);
        Assert.Contains("e1", edges);
        Assert.Contains("e2", edges);
    }

    [Fact]
    public void ModListExportsEmbeddedHierarchyAndUtf8Bom()
    {
        using var temp = new TempDir();
        var graph = new GraphDocument
        {
            Meta = new GraphMeta { PackName = "Hierarchy", McVersion = "1.21.1", Loader = "neoforge" },
            Nodes =
            [
                new ModNode { Id = "parent", Name = "Parent", Version = "1.0", FilePath = "mods/parent.jar" },
                new ModNode { Id = "child", Name = "Child", Version = "2.0", IsEmbedded = true, FilePath = "mods/parent.jar" },
            ],
            Edges =
            [
                new DependencyEdge { Id = "embedded", From = "parent", To = "child", Type = EdgeType.Embedded },
            ],
        };
        var scan = new ScanResult
        {
            RootPath = temp.Path,
            Profile = new PackProfile { PackName = "Hierarchy", McVersion = "1.21.1", Loader = LoaderKind.NeoForge },
        };

        var mdPath = temp.Combine("modlist.md");
        PackListExporter.ExportModListMarkdown(scan, graph, mdPath, includeEmbedded: true);
        var markdown = File.ReadAllText(mdPath);
        Assert.Contains("顶层 1 · 内嵌 1", markdown);
        Assert.Contains("| 层级 |", markdown);
        Assert.Contains("| 内嵌 | ↳ Child", markdown);
        Assert.Contains("| Parent |", markdown);

        var csvPath = temp.Combine("modlist.csv");
        PackListExporter.ExportModListCsv(scan, graph, csvPath, includeEmbedded: true);
        var bytes = File.ReadAllBytes(csvPath);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var csv = File.ReadAllText(csvPath);
        Assert.Contains("level,parentId,parentName", csv);
        Assert.Contains("内嵌,parent,Parent", csv);

        var defaultPath = temp.Combine("modlist-default.md");
        PackListExporter.ExportModListMarkdown(scan, graph, defaultPath);
        Assert.DoesNotContain("↳ Child", File.ReadAllText(defaultPath));
    }

    [Fact]
    public void MarkdownReportSummarisesIssues()
    {
        var issues = new List<AnalysisIssue>
        {
            new()
            {
                Severity = IssueSeverity.Error,
                Kind = IssueKind.MissingDependency,
                Title = "缺失必需依赖",
                Message = "A 需要 B",
                NodeId = "b",
            },
        };

        var report = GraphExporter.ToMarkdownReport(SampleGraph(), issues, rules:
        [
            new LocalRule
            {
                Type = LocalRuleType.IgnoreConflict,
                From = "a",
                To = "b",
                Priority = 3,
                Note = "known conflict",
            },
        ]);

        Assert.Contains("# 整合包依赖分析报告", report);
        Assert.Contains("缺失必需依赖", report);
        Assert.Contains("| 节点数 | 2 |", report);
        Assert.Contains("Demo", report);
        Assert.Contains("本地规则（1 条", report);
        Assert.Contains("known conflict", report);
    }
}
