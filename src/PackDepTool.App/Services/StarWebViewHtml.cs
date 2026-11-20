using Avalonia.Media;
using System.Text.Json;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Models;

namespace PackDepTool.App.Services;

/// <summary>
/// Builds the local ECharts page used by the galaxy view.
/// Each dependency hub owns one small force graph panel, so clusters stay
/// separated while retaining the draggable/zoomable force feel of MC百科.
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
            symbolSize = ReferenceEquals(node, scene.Center) ? 52 : 30,
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
                formatter = "{b}",
                position = "bottom",
                color = "#E5E7EB",
                fontSize = ReferenceEquals(node, scene.Center) ? 13 : 10,
                fontWeight = ReferenceEquals(node, scene.Center) ? "bold" : "normal",
            },
        }).ToList();

        var links = scene.Edges.Select(edge => new
        {
            source = edge.From.Model.Id,
            target = edge.To.Model.Id,
            lineStyle = new
            {
                color = ToHex(edge.Color),
                width = edge.Type == EdgeType.Requires ? 1.6 : 1.0,
                type = edge.Type is EdgeType.Optional or EdgeType.Recommends ? "dashed" : "solid",
                curveness = 0,
            },
        }).ToList();

        var json = JsonSerializer.Serialize(new { nodes, links },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  html,body { margin:0; padding:0; background:#0c0e12; color:#e5e7eb;
              font-family:"Microsoft YaHei","Segoe UI",sans-serif; }
  #grid { display:grid; grid-template-columns:repeat(3,minmax(360px,1fr));
          gap:12px; padding:12px; }
  .galaxy { background:#111722; border:1px solid #253044; border-radius:10px;
            overflow:hidden; display:flex; flex-direction:column; }
  .head { display:flex; align-items:center; justify-content:space-between;
          padding:8px 12px; border-bottom:1px solid #1f2a3c; }
  .head b { font-size:12px; color:#f3f5f8; font-weight:600; }
  .head span { font-size:10px; color:#8492a6; }
  .chart { width:100%; height:320px; }
  .hint { position:fixed; left:14px; bottom:12px; color:#7d8797; font-size:11px; z-index:2; }
  .legend { position:fixed; right:16px; top:14px; background:rgba(20,25,34,.9);
            border:1px solid #263142; border-radius:8px; padding:8px 12px;
            color:#aeb7c5; font-size:11px; line-height:1.8; z-index:3; }
  .dot { display:inline-block; width:8px; height:8px; border-radius:50%; margin-right:6px; }
  @media (max-width:1200px) { #grid { grid-template-columns:repeat(2,minmax(320px,1fr)); } }
</style>
<script src="echarts.min.js"></script>
</head>
<body>
<div class="legend">
  <div><span class="dot" style="background:#d75a8a"></span>中心模组</div>
  <div><span class="dot" style="background:#3b82f6"></span>必需 / 被依赖</div>
  <div><span class="dot" style="background:#22c55e"></span>可选 / 联动</div>
  <div>每个面板可独立拖拽、缩放</div>
</div>
<div id="grid"></div>
<div class="hint">按前置枢纽分组 · 共 {{nodes.Count}} 个模组</div>
<script>
const raw = {{json}};
const byId = new Map(raw.nodes.map(n => [n.id, n]));
const adjacency = new Map(raw.nodes.map(n => [n.id, []]));
const degree = new Map(raw.nodes.map(n => [n.id, 0]));
for (const link of raw.links) {
  degree.set(link.source, (degree.get(link.source) || 0) + 1);
  degree.set(link.target, (degree.get(link.target) || 0) + 1);
  adjacency.get(link.source).push(link.target);
  adjacency.get(link.target).push(link.source);
}
const hubs = raw.nodes
  .slice()
  .sort((a, b) => (degree.get(b.id) || 0) - (degree.get(a.id) || 0))
  .filter(n => (degree.get(n.id) || 0) >= 2)
  .slice(0, Math.min(8, Math.max(2, Math.ceil(raw.nodes.length / 45))));
if (hubs.length === 0 && raw.nodes.length) hubs.push(raw.nodes[0]);

const clusterOf = new Map();
const queue = [];
hubs.forEach((hub, index) => { clusterOf.set(hub.id, index); queue.push(hub.id); });
for (let head = 0; head < queue.length; head++) {
  const current = queue[head];
  const owner = clusterOf.get(current);
  for (const next of adjacency.get(current)) {
    if (clusterOf.has(next)) continue;
    clusterOf.set(next, owner);
    queue.push(next);
  }
}
for (const node of raw.nodes) if (!clusterOf.has(node.id)) clusterOf.set(node.id, 0);

const clusters = hubs.map((hub, index) => ({
  hub,
  nodes: raw.nodes.filter(n => clusterOf.get(n.id) === index)
}));

const grid = document.getElementById('grid');
for (const cluster of clusters) {
  const section = document.createElement('section');
  section.className = 'galaxy';
  const head = document.createElement('div');
  head.className = 'head';
  head.innerHTML = '<b>' + cluster.hub.name + '</b><span>' + cluster.nodes.length + ' 个模组</span>';
  const box = document.createElement('div');
  box.className = 'chart';
  section.appendChild(head);
  section.appendChild(box);
  grid.appendChild(section);

  const ids = new Set(cluster.nodes.map(n => n.id));
  const chart = echarts.init(box, null, { renderer: 'canvas' });
  chart.setOption({
    backgroundColor: 'transparent',
    animation: true,
    animationDuration: 350,
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
      layout: 'force',
      roam: true,
      draggable: true,
      scaleLimit: { min: 0.3, max: 4 },
      data: cluster.nodes.map(n => Object.assign({}, n)),
      links: raw.links.filter(l => ids.has(l.source) && ids.has(l.target)),
      edgeSymbol: ['none', 'arrow'],
      edgeSymbolSize: [0, 6],
      label: { show: true, position: 'bottom', color: '#DCE3EE', fontSize: 9 },
      labelLayout: { hideOverlap: true },
      lineStyle: { color: '#3b82f6', width: 1.1, opacity: 0.55, curveness: 0.08 },
      emphasis: { focus: 'adjacency', lineStyle: { width: 2.2, opacity: 1 } },
      force: {
        repulsion: 420,
        edgeLength: 95,
        gravity: 0.12,
        friction: 0.2,
        layoutAnimation: true
      }
    }]
  });
  chart.on('click', p => {
    if (!p.data) return;
    chart.dispatchAction({ type: 'downplay', seriesIndex: 0 });
    chart.dispatchAction({ type: 'highlight', seriesIndex: 0, dataIndex: p.dataIndex });
  });
  new ResizeObserver(() => chart.resize()).observe(box);
}
window.addEventListener('resize', () => {
  document.querySelectorAll('.chart').forEach(el => {
    const instance = echarts.getInstanceByDom(el);
    if (instance) instance.resize();
  });
});
</script>
</body>
</html>
""";
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
