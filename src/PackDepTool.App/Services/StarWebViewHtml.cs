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
  .overview .chart { height:170px; }
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

let clusters = hubs.map((hub, index) => ({
  hub,
  nodes: raw.nodes.filter(n => clusterOf.get(n.id) === index)
}));

// A single prerequisite can own half the pack (e.g. Create). Split any
// overfull cluster at a strong secondary hub so every panel stays readable.
for (let iteration = 0; iteration < 12; iteration++) {
  const largest = clusters.slice().sort((a, b) => b.nodes.length - a.nodes.length)[0];
  if (!largest || largest.nodes.length <= 55) break;
  const ids = new Set(largest.nodes.map(n => n.id));
  const newHub = largest.nodes
    .filter(n => n.id !== largest.hub.id)
    .sort((a, b) => (degree.get(b.id) || 0) - (degree.get(a.id) || 0))[0];
  if (!newHub) break;
  const distances = startId => {
    const result = new Map([[startId, 0]]);
    const q = [startId];
    for (let h = 0; h < q.length; h++) {
      const current = q[h];
      for (const next of adjacency.get(current)) {
        if (!ids.has(next) || result.has(next)) continue;
        result.set(next, result.get(current) + 1);
        q.push(next);
      }
    }
    return result;
  };
  const da = distances(largest.hub.id);
  const db = distances(newHub.id);
  const keep = [largest.hub];
  const split = [newHub];
  for (const node of largest.nodes) {
    if (node.id === largest.hub.id || node.id === newHub.id) continue;
    const a = da.get(node.id) ?? 999;
    const b = db.get(node.id) ?? 999;
    (b < a ? split : keep).push(node);
  }
  if (split.length < 6) break;
  largest.nodes = keep;
  clusters.push({ hub: newHub, nodes: split });
}

const clusterOfNode = new Map();
clusters.forEach((cluster, index) => cluster.nodes.forEach(n => clusterOfNode.set(n.id, index)));
const cross = new Map();
for (const link of raw.links) {
  const a = clusterOfNode.get(link.source);
  const b = clusterOfNode.get(link.target);
  if (a === undefined || b === undefined || a === b) continue;
  const key = a < b ? a + '-' + b : b + '-' + a;
  cross.set(key, (cross.get(key) || 0) + 1);
}

const overview = document.createElement('section');
overview.className = 'galaxy overview';
overview.style.gridColumn = '1 / -1';
overview.innerHTML = '<div class="head"><b>星系关系总览</b><span>跨星系 ' + cross.size + ' 组连接</span></div><div class="chart"></div>';
document.getElementById('grid').appendChild(overview);
const overviewChart = echarts.init(overview.querySelector('.chart'), null, { renderer: 'canvas' });
overviewChart.setOption({
  backgroundColor: 'transparent',
  animation: false,
  tooltip: { backgroundColor: 'rgba(18,23,32,.96)', borderColor: '#334155',
             textStyle: { color: '#e5e7eb', fontSize: 11 } },
  series: [{
    type: 'graph', layout: 'force', roam: true, draggable: true,
    data: clusters.map((c, i) => ({
      id: 'cluster-' + i,
      name: c.hub.name,
      value: c.nodes.length + ' 个模组',
      symbolSize: Math.min(52, 22 + c.nodes.length * 0.5),
      itemStyle: { color: i % 2 ? '#3b82f6' : '#d75a8a' },
      label: { show: true, position: 'bottom', color: '#E5E7EB', fontSize: 10 }
    })),
    links: [...cross].map(([key, value]) => {
      const [a, b] = key.split('-').map(Number);
      return { source: 'cluster-' + a, target: 'cluster-' + b, value,
               lineStyle: { color: '#64748b', width: 1, opacity: 0.45, type: 'dashed', curveness: 0.1 } };
    }),
    edgeSymbol: ['none', 'arrow'], edgeSymbolSize: [0, 5],
    lineStyle: { width: 1, opacity: 0.45, color: '#64748b' },
    force: { repulsion: 300, edgeLength: 100, gravity: 0.15, friction: 0.2, layoutAnimation: false },
    emphasis: { focus: 'none' }
  }]
});

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
      emphasis: { focus: 'none', lineStyle: { width: 1.4, opacity: 0.8 } },
      force: {
        repulsion: 420,
        edgeLength: 95,
        gravity: 0.12,
        friction: 0.2,
        layoutAnimation: false
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
