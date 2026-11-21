using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PackDepTool.App.Controls;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using PackDepTool.Core.Scanning;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var pack = Arg(args, "--pack", @"E:\mc_ce\.minecraft\versions\SkyExplorer")
                   ?? @"E:\mc_ce\.minecraft\versions\SkyExplorer";
        var output = Path.GetFullPath(Arg(args, "--out",
            Path.Combine("screenshots", "star-preview.png")));
        var width = int.Parse(Arg(args, "--width", "1600"), CultureInfo.InvariantCulture);
        var height = int.Parse(Arg(args, "--height", "900"), CultureInfo.InvariantCulture);
        var focus = Arg(args, "--focus", null);
        var select = Arg(args, "--select", focus);
        var zoom = double.Parse(Arg(args, "--zoom", "1.05"), CultureInfo.InvariantCulture);

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        Console.WriteLine($"scan: {pack}");
        var scan = new ModScanner().ScanAsync(pack).GetAwaiter().GetResult();
        var built = new DependencyGraphBuilder().Build(scan);
        var graph = built.Graph;

        var nodes = graph.Nodes
            .Where(node => !node.IsSynthetic && !node.IsEmbedded)
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var ids = nodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = graph.Edges
            .Where(edge => ids.Contains(edge.From) && ids.Contains(edge.To))
            .ToList();

        var options = new StarGalaxyOptions();
        var layout = new StarGalaxyLayoutEngine().Compute(nodes, edges, options);
        var scene = GalaxyScene.From(layout, nodes, edges, options);

        AppBuilder.Configure<PreviewApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .SetupWithoutStarting();

        var view = new StarGalaxyView
        {
            Scene = scene,
            Width = width,
            Height = height,
        };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        if (select is not null) view.SelectedNodeId = select;

        // Give the background icon decoder a chance to finish; no window or
        // timer is involved, only dispatcher pumping for this render.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        if (focus is not null) view.FocusNode(focus, zoom);
        else view.FitAll();
        Dispatcher.UIThread.RunJobs();

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(view);
        bitmap.Save(output);
        Console.WriteLine($"saved: {output}");
        return 0;
    }

    private static string? Arg(string[] args, string name, string? fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return fallback;
    }
}

internal sealed class PreviewApp : Application
{
}
