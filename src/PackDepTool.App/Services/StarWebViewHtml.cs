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
const adjacency = new Map(raw.nodes.map(n => [n.id, []]));
for (const link of raw.links) {
  degree.set(link.source, (degree.get(link.source) || 0) + 1);
  degree.set(link.target, (degree.get(link.target) || 0) + 1);
  adjacency.get(link.source).push(link.target);
  adjacency.get(link.target).push(link.source);
}

// Pick the strongest prerequisites as galaxy cores.
const hubs = raw.nodes
  .slice()
  .sort((a, b) => (degree.get(b.id) || 0) - (degree.get(a.id) || 0))
  .filter(n => (degree.get(n.id) || 0) >= 2)
  .slice(0, Math.min(8, Math.max(2, Math.ceil(raw.nodes.length / 45))));
if (hubs.length === 0 && raw.nodes.length) hubs.push(raw.nodes[0]);

const clusterOf = new Map();
const depth = new Map();
const queue = [];
hubs.forEach((hub, index) => {
  clusterOf.set(hub.id, index);
  depth.set(hub.id, 0);
  queue.push(hub.id);
});
for (let head = 0; head < queue.length; head++) {
  const current = queue[head];
  const owner = clusterOf.get(current);
  const d = depth.get(current) || 0;
  for (const next of adjacency.get(current)) {
    if (clusterOf.has(next)) continue;
    clusterOf.set(next, owner);
    depth.set(next, d + 1);
    queue.push(next);
  }
}
for (const node of raw.nodes) {
  if (!clusterOf.has(node.id)) {
    clusterOf.set(node.id, 0);
    depth.set(node.id, 1);
  }
}

const clusters = hubs.map((hub, index) => ({
  hub,
  members: raw.nodes.filter(n => clusterOf.get(n.id) === index && n.id !== hub.id)
}));
const maxMemberCount = Math.max(1, ...clusters.map(c => c.members.length));
const clusterRadius = Math.max(180, maxMemberCount * 26 / (2 * Math.PI));
const columns = Math.min(3, clusters.length);
const rows = Math.ceil(clusters.length / columns);
const cell = clusterRadius * 2.2 + 80;
for (let index = 0; index < clusters.length; index++) {
  const cluster = clusters[index];
  const cx = ((index % columns) - (columns - 1) / 2) * cell;
  const cy = (Math.floor(index / columns) - (rows - 1) / 2) * cell;
  cluster.hub.x = cx;
  cluster.hub.y = cy;
  cluster.hub.symbolSize = 52;
  cluster.hub.label = {
    show: true, position: 'top', color: '#F3F5F8', fontSize: 13,
    fontWeight: 'bold', formatter: '{b}'
  };

  const rings = [];
  for (const node of cluster.members) {
    const d = Math.max(1, (depth.get(node.id) || 1) - (depth.get(cluster.hub.id) || 0));
    if (!rings[d]) rings[d] = [];
    rings[d].push(node);
  }
  for (const dText of Object.keys(rings)) {
    const d = Number(dText);
    const list = rings[d];
    const radius = 100 + d * 85 + list.length * 8 / (2 * Math.PI);
    for (let i = 0; i < list.length; i++) {
      const angle = 2 * Math.PI * i / list.length + d * 0.4;
      list[i].x = cx + Math.cos(angle) * radius;
      list[i].y = cy + Math.sin(angle) * radius;
      const deg = degree.get(list[i].id) || 0;
      list[i].symbolSize = Math.min(24, 11 + Math.min(10, deg * 0.7));
      list[i].label = {
        show: deg >= 8,
        position: 'bottom', color: '#E5E7EB', fontSize: 9,
        fontWeight: 'normal', formatter: '{b}'
      };
    }
  }
}

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
    zoom: 0.46,
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
