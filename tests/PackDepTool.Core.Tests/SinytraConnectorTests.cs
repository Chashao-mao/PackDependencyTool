using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class SinytraConnectorTests
{
    [Fact]
    public void SinytraConnectorSuppressesFabricApiMissingWarning()
    {
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "connector", Name = "信雅互联", Version = "2.0", FilePath = "mods/connector.jar" },
                new ModNode { Id = "fabric-api", Name = "fabric-api", IsSynthetic = true, Status = ModStatus.Missing },
                new ModNode { Id = "demo", Name = "Demo", Version = "1.0", FilePath = "mods/demo.jar" },
            ],
            Edges =
            [
                new DependencyEdge
                {
                    Id = "demo-fabric-api", From = "demo", To = "fabric-api", Type = EdgeType.Requires,
                },
            ],
        };

        var issues = new GraphAnalyzer().Analyze(graph, new PackProfile { McVersion = "1.21.1" });

        Assert.DoesNotContain(issues, issue => issue.Kind == IssueKind.MissingDependency);
        Assert.Equal(ModStatus.Unknown, graph.FindNode("fabric-api")!.Status);
    }
}
