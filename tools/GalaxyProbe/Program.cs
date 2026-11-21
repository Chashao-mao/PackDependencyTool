using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using PackDepTool.Core.Scanning;

var pack = args.Length > 0 ? args[0] : @"E:\mc_ce\.minecraft\versions\SkyExplorer";
var scan = await new ModScanner().ScanAsync(pack);
var built = new DependencyGraphBuilder().Build(scan);
var graph = built.Graph;

var nodes = graph.Nodes
    .Where(node => !node.IsSynthetic && !node.IsEmbedded)
    .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
    .Select(group => group.First())
    .ToList();
var ids = nodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
var edges = graph.Edges.Where(edge => ids.Contains(edge.From) && ids.Contains(edge.To)).ToList();

var stopwatch = System.Diagnostics.Stopwatch.StartNew();
var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges);
stopwatch.Stop();

Console.WriteLine($"pack={pack}");
Console.WriteLine($"nodes={nodes.Count} edges={edges.Count} galaxies={layout.Galaxies.Count} layoutMs={stopwatch.ElapsedMilliseconds}");
Console.WriteLine($"bounds={layout.Bounds.X:F0},{layout.Bounds.Y:F0} {layout.Bounds.Width:F0}x{layout.Bounds.Height:F0}");
foreach (var galaxy in layout.Galaxies)
{
    Console.WriteLine(
        $"G{galaxy.Index + 1,-2} hub={galaxy.HubId,-32} nodes={galaxy.NodeIds.Count,3} " +
        $"r={galaxy.Radius,6:F0} internal={galaxy.InternalEdges,3} cross={galaxy.CrossEdges,3}");
}

var labels = layout.Nodes.Values.Count(node => node.LabelVisible);
Console.WriteLine($"visibleLabels={labels}/{nodes.Count}");
var ringCounts = layout.Nodes.Values.GroupBy(node => node.Ring)
    .OrderBy(group => group.Key)
    .Select(group => $"ring{group.Key}={group.Count()}");
Console.WriteLine(string.Join(" ", ringCounts));
var crossLinks = layout.CrossLinks;
Console.WriteLine($"crossLinks={crossLinks.Count} totalCrossEdges={crossLinks.Sum(link => link.Count)}");
