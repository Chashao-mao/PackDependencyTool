using System.Xml.Linq;
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
        Assert.Contains(",side,category,", nodes);
        Assert.Contains(",requires,optional,incompatible,embeddedDeps,recommends", nodes);
        Assert.Contains("\"line1\nline2, with comma\"", edges);
        Assert.Contains("e1", edges);
        Assert.Contains("e2", edges);
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
