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
            symbol = node.IconDataUrl is null ? "circle" : "image://" + node.IconDataUrl,
            symbolSize = ReferenceEquals(node, scene.Center) ? 54 : 30,
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
                formatter = "{b}\n{c}",
                position = "bottom",
                color = "#E5E7EB",
                fontSize = ReferenceEquals(node, scene.Center) ? 13 : 11,
                fontWeight = ReferenceEquals(node, scene.Center) ? "bold" : "normal",
                lineHeight = 15,
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
const degree = new Map(raw.nodes.map(n => [n.id, 0]));
const parentOf = new Map();
for (const link of raw.links) {
  degree.set(link.source, (degree.get(link.source) || 0) + 1);
  degree.set(link.target, (degree.get(link.target) || 0) + 1);
  if (!parentOf.has(link.source)) parentOf.set(link.source, link.target);
}
const childrenOf = new Map(raw.nodes.map(n => [n.id, []]));
for (const [child, parent] of parentOf) {
  if (childrenOf.has(parent)) childrenOf.get(parent).push(child);
}
const depth = new Map(raw.nodes.map(n => [n.id, 0]));
const roots = raw.nodes.filter(n => !parentOf.has(n.id)).map(n => n.id);
const queue = roots.length ? roots.slice() : [raw.nodes[0] && raw.nodes[0].id];
for (const id of queue) depth.set(id, 0);
while (queue.length) {
  const current = queue.shift();
  for (const child of (childrenOf.get(current) || [])) {
    if (depth.get(child) > depth.get(current) + 1) continue;
    depth.set(child, depth.get(current) + 1);
    queue.push(child);
  }
}
const layers = new Map();
for (const node of raw.nodes) {
  const d = depth.get(node.id) ?? 0;
  if (!layers.has(d)) layers.set(d, []);
  layers.get(d).push(node);
}
const maxDepth = Math.max(1, ...layers.keys());
for (const [d, list] of layers) {
  const radius = d === 0 ? 0 : 130 + d * Math.max(80, 520 / maxDepth);
  for (let i = 0; i < list.length; i++) {
    const angle = list.length === 1 ? 0 : (Math.PI * 2 * i / list.length) - Math.PI / 2;
    list[i].x = Math.cos(angle) * radius;
    list[i].y = Math.sin(angle) * radius;
    list[i].symbolSize = d === 0 ? 46 : Math.min(30, 10 + Math.min(14, (degree.get(list[i].id) || 0) * 1.2));
    list[i].label = {
      show: d <= 1 || (degree.get(list[i].id) || 0) >= 6,
      position: 'bottom',
      color: '#E5E7EB',
      fontSize: d === 0 ? 13 : 10,
      fontWeight: d === 0 ? 'bold' : 'normal',
      formatter: '{b}'
    };
  }
}
raw.nodes.forEach((node, index) => {
  const d = depth.get(node.id) ?? 0;
  if (d === 0) {
    const angle = (Math.PI * 2 * index / Math.max(1, layers.get(0).length)) - Math.PI / 2;
    node.x = Math.cos(angle) * 36;
    node.y = Math.sin(angle) * 36;
  }
});
const chart = echarts.init(document.getElementById('chart'), null, { renderer: 'canvas' });
chart.setOption({
  backgroundColor: '#0c0e12',
  animation: false,
  tooltip: {
    backgroundColor: 'rgba(18,23,32,.96)',
    borderColor: '#334155',
    textStyle: { color: '#e5e7eb', fontSize: 11 },
    formatter: p => p.dataType === 'node'
      ? '<b>' + p.data.name + '</b><br/>' + (p.data.value || '')
      : (p.data.source + ' → ' + p.data.target)
  },
  series: [{
    type: 'graph',
    layout: 'none',
    zoom: 0.62,
    roam: true,
    draggable: true,
    focusNodeAdjacency: true,
    data: raw.nodes,
    links: raw.links,
    edgeSymbol: ['none', 'arrow'],
    edgeSymbolSize: [0, 6],
    label: { show: false },
    lineStyle: { color: '#3b82f6', width: 0.9, opacity: 0.35, curveness: 0.08 },
    emphasis: { focus: 'adjacency', lineStyle: { width: 2.2, opacity: 1 } }
  }]
});
chart.on('click', p => {
  if (!p.data || !p.data.id || p.data.id === '__pack__') return;
  try { if (window.pdt && typeof pdt.recenter === 'function') pdt.recenter(p.data.id); } catch (e) {}
});
window.addEventListener('resize', () => chart.resize());
</script>
</body>
</html>
""";
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
