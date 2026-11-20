using PackDepTool.App.Services;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Models;

var nodes = new List<StarNodeVm>();
for (var i = 0; i < 30; i++)
{
    var model = new ModNode
    {
        Id = "mod" + i,
        Name = "Mod " + i,
        Version = "1." + i,
        Loader = i % 3 == 0 ? LoaderKind.NeoForge : LoaderKind.Fabric,
    };
    nodes.Add(new StarNodeVm { Model = model, Ring = i % 3 });
}
var center = nodes[0];
var edges = new List<StarEdgeVm>();
for (var i = 1; i < nodes.Count; i++)
{
    edges.Add(new StarEdgeVm
    {
        From = nodes[i],
        To = nodes[(i - 1) / 2],
        Type = EdgeType.Requires,
    });
}
var scene = new StarScene { Center = center, Nodes = nodes, Edges = edges };
var path = args.Length > 0 ? args[0] : "galaxy-probe.html";
File.WriteAllText(path, StarWebViewHtml.Build(scene, center.Model.Id));
Console.WriteLine(path);
