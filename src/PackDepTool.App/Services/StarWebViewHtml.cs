using Avalonia.Media;
using System.Text;
using System.Text.Json;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Models;

namespace PackDepTool.App.Services;

/// <summary>
/// Builds the local ECharts page used by the star view. The option mirrors the
/// MC百科 relation star: force layout, draggable nodes, roam, gravity and a
/// pinned center node. Keeping the data generation here means the WebView only
/// ever loads local HTML and never needs network access.
/// </summary>
public static class StarWebViewHtml
{
    public static string Build(StarScene scene, string centerId)
    {
        var nodes = scene.Nodes.Select(node => new
        {
            id = node.Model.Id,
            name = node.Title,
            value = node.Subtitle,
            symbolSize = ReferenceEquals(node, scene.Center) ? 54 : 34,
            @fixed = ReferenceEquals(node, scene.Center),
            x = ReferenceEquals(node, scene.Center) ? 640 : (double?)null,
            y = ReferenceEquals(node, scene.Center) ? 380 : (double?)null,
            category = node.Ring,
            itemStyle = new
            {
                color = ReferenceEquals(node, scene.Center) ? "#D75A8A" : ToHex(node.LoaderColor),
                borderColor = ToHex(node.StatusColor),
                borderWidth = 1.5,
            },
            label = new
            {
                show = true,
                color = "#F3F5F8",
                fontSize = ReferenceEquals(node, scene.Center) ? 13 : 11,
                fontWeight = ReferenceEquals(node, scene.Center) ? "bold" : "normal",
                formatter = "{b}",
                position = "bottom",
            },
        }).ToList();

        var links = scene.Edges.Select(edge => new
        {
            source = edge.From.Model.Id,
            target = edge.To.Model.Id,
            lineStyle = new
            {
                color = ToHex(edge.Color),
                width = edge.Type == EdgeType.Requires ? 2 : 1.2,
                type = edge.Type is EdgeType.Optional or EdgeType.Recommends ? "dashed" : "solid",
                curveness = 0,
            },
        }).ToList();

        var json = JsonSerializer.Serialize(new { nodes, links },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var hidden = scene.HiddenCount > 0
            ? $"<div class=\"hint\">仅显示与中心直接相关的 {scene.Nodes.Count} 个模组，另有 {scene.HiddenCount} 个未显示</div>"
            : string.Empty;

        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<style>
  html,body { margin:0; width:100%; height:100%; overflow:hidden; background:#0c0e12; font-family:"Microsoft YaHei","Segoe UI",sans-serif; }
  #chart { width:100%; height:100%; }
  .hint { position:fixed; left:14px; bottom:12px; color:#7d8797; font-size:11px; z-index:2; }
  .legend { position:fixed; right:16px; top:14px; background:rgba(20,25,34,.88); border:1px solid #263142;
            border-radius:8px; padding:10px 12px; color:#aeb7c5; font-size:11px; line-height:1.9; z-index:2; }
  .dot { display:inline-block; width:8px; height:8px; border-radius:50%; margin-right:6px; }
</style>
<script src="echarts.min.js"></script>
</head>
<body>
<div id="chart"></div>
<div class="legend">
  <div><span class="dot" style="background:#d75a8a"></span>中心模组</div>
  <div><span class="dot" style="background:#3b82f6"></span>必需 / 被依赖</div>
  <div><span class="dot" style="background:#22c55e"></span>可选 / 联动</div>
  <div>拖拽节点 · 滚轮缩放 · 点击节点重新居中</div>
</div>
{{hidden}}
<script>
const raw = {{json}};
const chart = echarts.init(document.getElementById('chart'), null, { renderer: 'canvas' });
chart.setOption({
  backgroundColor: '#0c0e12',
  animationDuration: 450,
  animationEasingUpdate: 'quinticInOut',
  tooltip: {
    backgroundColor: 'rgba(18,23,32,.96)',
    borderColor: '#334155',
    textStyle: { color: '#e5e7eb', fontSize: 11 },
    formatter: p => p.dataType === 'node'
      ? '<b>' + p.data.name + '</b><br/>' + p.data.value
      : (p.data.source + ' → ' + p.data.target)
  },
  series: [{
    type: 'graph',
    layout: 'force',
    roam: true,
    draggable: true,
    focusNodeAdjacency: true,
    data: raw.nodes,
    links: raw.links,
    edgeSymbol: ['none', 'arrow'],
    edgeSymbolSize: [0, 7],
    label: { show: true, position: 'bottom', fontSize: 11, color: '#e5e7eb' },
    lineStyle: { color: '#3b82f6', width: 1.4, opacity: 0.8, curveness: 0 },
    emphasis: { focus: 'adjacency', lineStyle: { width: 3, opacity: 1 } },
    force: {
      initLayout: 'circular',
      repulsion: 900,
      edgeLength: 220,
      gravity: 0.1,
      friction: 0.2,
      layoutAnimation: true,
      attract: { damping: 0.9 }
    }
  }]
});
chart.on('click', p => {
  if (p.dataType !== 'node' || !p.data || !p.data.id) return;
  window.location.href = 'pdt://recenter?' + encodeURIComponent(p.data.id);
});
window.addEventListener('resize', () => chart.resize());
</script>
</body>
</html>
""";
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
