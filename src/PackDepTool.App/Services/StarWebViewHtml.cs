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
const byId = new Map(raw.nodes.map(n => [n.id, Object.assign({}, n, { children: [] })]));
const parentOf = new Map();
for (const link of raw.links) {
  if (!parentOf.has(link.source) && byId.has(link.source) && byId.has(link.target)) {
    parentOf.set(link.source, link.target);
  }
}
const childrenOf = new Map(raw.nodes.map(n => [n.id, []]));
const roots = [];
for (const node of raw.nodes) {
  const parent = parentOf.get(node.id);
  if (parent) childrenOf.get(parent).push(node.id);
  else roots.push(node.id);
}
if (roots.length === 0 && raw.nodes.length) roots.push(raw.nodes[0].id);

function attach(id, seen, depth) {
  if (seen.has(id)) return null;
  seen.add(id);
  const node = byId.get(id);
  if (!node) return null;
  const childIds = (childrenOf.get(id) || []).filter(child => !seen.has(child));
  node.children = childIds.map(child => attach(child, seen, depth + 1)).filter(Boolean);
  node.symbolSize = Math.min(30, 7 + (childrenOf.get(id) || []).length * 1.8);
  node.label = {
    show: depth <= 1 || (childrenOf.get(id) || []).length > 1,
    position: 'top',
    color: '#E5E7EB',
    fontSize: depth <= 1 ? 12 : 10,
    fontWeight: depth <= 1 ? 'bold' : 'normal'
  };
  return node;
}
const seen = new Set();
const galaxy = {
  id: '__pack__',
  name: '整合包',
  value: raw.nodes.length + ' 个模组',
  symbolSize: 30,
  itemStyle: { color: '#D75A8A', borderColor: '#F3F5F8', borderWidth: 2 },
  label: { show: true, position: 'top', color: '#F3F5F8', fontSize: 14, fontWeight: 'bold' },
  children: roots.map(id => attach(id, seen, 1)).filter(Boolean)
};
const chart = echarts.init(document.getElementById('chart'), null, { renderer: 'canvas' });
chart.setOption({
  backgroundColor: '#0c0e12',
  animationDuration: 900,
  animationEasingUpdate: 'quinticInOut',
  tooltip: {
    backgroundColor: 'rgba(18,23,32,.96)',
    borderColor: '#334155',
    textStyle: { color: '#e5e7eb', fontSize: 11 },
    formatter: p => '<b>' + p.data.name + '</b><br/>' + (p.data.value || '')
  },
  series: [{
    type: 'tree',
    layout: 'radial',
    data: [galaxy],
    roam: true,
    expandAndCollapse: false,
    initialTreeDepth: -1,
    symbol: 'circle',
    edgeShape: 'curve',
    lineStyle: { color: '#3b82f6', width: 1.1, opacity: 0.55, curveness: 0.15 },
    label: { show: true, position: 'top', color: '#E5E7EB', fontSize: 10 },
    emphasis: { focus: 'descendant', lineStyle: { width: 2.5, opacity: 1 } },
    animationDuration: 900,
    top: '6%', left: '6%', right: '6%', bottom: '6%'
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
