# ModLens 星图（Star Galaxy）原生重写 · 实施计划

> 状态：**已实现**，隔离在 worktree `F:\DSH-Workspace\PackDependencyTool-star`、
> 分支 `feat/native-star-galaxy`。主仓库保持 clean；HTML 星图路径已删除。
> 目标：用 Avalonia + SkiaSharp 原生控件替换 WebView/ECharts 星图，构建“多星系”依赖宇宙。
> 范围：**只动星图模块**。扫描、诊断、模组列表、依赖图（GraphView）核心逻辑不改。

---

## 0. 一句话方案

新建纯算法 `StarGalaxyLayout`（Core，可单测）负责“找枢纽 → 均衡分星系 → 星系内分层 → 碰撞/标签避让”的**一次性布局**；
新建 `StarGalaxyView`（App，SkiaSharp 直绘）只做**相机变换 + 视口裁剪 + 绘制 + 命中测试**；
删除 WebView/ECharts 路径。交互只改变换矩阵或单节点坐标，空闲零定时器、零动画。

---

## 1. 已确认的现状（调研结论）

### 1.1 当前星图确实走 WebView

| 位置 | 内容 |
| --- | --- |
| `src/PackDepTool.App/Views/MainWindow.axaml:864-866` | `<ContentControl x:Name="StarHost" />`，星图页面唯一宿主 |
| `src/PackDepTool.App/Views/MainWindow.axaml.cs:54-107` | `EnsureStarWeb/ReloadStarWeb/StarBridge`：每次场景变化**重写 HTML + 重新导航** |
| 同文件 `:148-153` | `StarScene` / `SelectedViewOption` 变化 → `QueueStarWebReload()`（50ms 延迟定时器） |
| `src/PackDepTool.App/Services/StarWebViewHtml.cs` | 285 行 ECharts 页面，3 列小图面板 |
| `src/PackDepTool.App/Program.cs:31-37` | `WebView.Settings.OsrEnabled = true`（常驻 CEF） |
| `src/PackDepTool.App/PackDepTool.App.csproj:29,38` | `WebViewControl-Avalonia` + `Assets/echarts.min.js` |
| `tools/StarProbe/` | 仅供生成 WebView HTML 的调试工具 |

### 1.2 现有原生控件是旧方案且未使用

- `src/PackDepTool.App/Controls/StarGraphView.cs`（291 行）：
  - `Render()` 里 **每次重绘都 `Layout(scene)` 重排**，并重建 `_hitRects`、`_positions`；
  - 每条边每帧 `new StreamGeometry()` + `new Pen` + `new SolidColorBrush`；
  - 只支持 16 个节点（ViewModel 传 `StarNodeLimit = 16`）；
  - 单击节点直接 `RecenterRequested`（等于单击就重建），不符合“点击只选中”。
- 该控件**没有被任何 XAML 引用**，属于死代码。

### 1.3 数据与语义（已核对源码）

- `GraphDocument`：`Nodes: List<ModNode>`、`Edges: List<DependencyEdge>`。
- `ModNode`：`Id / Name / NameZh / Version / Loader / Status / IconData(byte[]) / IsSynthetic / IsEmbedded`。
- `DependencyEdge`：`From → To`（依赖方 → 被依赖方）、`Type ∈ {Requires, Optional, Recommends, Incompatible, Embedded, Manual}`、`ManualOverride`。
- 已安装模组口径：`!IsSynthetic && !IsEmbedded`（与 `InstalledModCount` 一致）；270 个。
- `StarScene` 目前承载 `Center / Nodes / Edges / HiddenCount`，`StarNodeVm` 计算标题与颜色。

### 1.4 构建/测试基线

- **SDK 坑**：`global.json` 要求 `9.0.319`，本机 `C:\Program Files\dotnet` 无 SDK；可用 SDK 在
  `C:\Users\Lenovo\.dotnet\sdk\9.0.100`。SDK 解析基于 **cwd**，因此：
  ```bash
  cd /c/Users/Lenovo
  /c/Users/Lenovo/.dotnet/dotnet.exe build  F:/DSH-Workspace/PackDependencyTool/PackDependencyTool.sln -c Debug --nologo -v q
  /c/Users/Lenovo/.dotnet/dotnet.exe test   F:/DSH-Workspace/PackDependencyTool/tests/PackDepTool.Core.Tests/PackDepTool.Core.Tests.csproj -c Debug --nologo -v q
  ```
  不要修改 `global.json`。
- 基线：构建 0 error / 31 warning；测试 **178 passed**（264 ms）。
- 数据目录：`E:\PackDependencyTool\data`（`settings.json`、`star/`、`icons/` 等）。
- 现有 UI 主题资源：`GraphBackground #0E1014`（暗）/ `#F7F9FC`（亮），`PanelBackground`、`CardBorder` 等。

### 1.5 现有 ViewModel 中的星图耦合点

| 位置 | 内容 | 处理 |
| --- | --- | --- |
| `MainWindowViewModel.cs:319` | `[ObservableProperty] StarScene? _starScene` | 替换为 `GalaxyScene?` |
| `:2041-2042` | `SelectedNodeItem` 变化时 `BuildStarScene()` | **删除**（选中不得重排） |
| `:2069-2090` | 切到 star 时 `ShowDetailsPanel=false; BuildStarScene()` | 改为构建 galaxy scene |
| `:4768-4790` | `LayoutAsync` 里 star 用 `LayoutAlgorithm.Star`、`StarNodeLimit=16` | 删除 star 分支 |
| `:4854-4880` | `BuildScene` 里 star 可见性过滤 `_starVisibleIds` | 删除，星图不再复用 graph layout |
| `:4992-5002` | star 时 3 次 `FitToView` 延迟定时器 | 删除 |
| `:5644-5730` | `RecenterStar` + `BuildStarScene`（16 节点星形） | 重写为 `BuildGalaxyScene` |
| `:5933` | 搜索变化 → `RefreshView()`（全量重建） | star 视图下直接返回，交给控件高亮 |

`GraphLayoutEngine`（`Core/Analysis`，442 行）**保持不动**。新算法独立成文件。

---

## 2. 目标架构

### 2.1 文件清单

**新增**

| 文件 | 职责 |
| --- | --- |
| `src/PackDepTool.Core/Analysis/StarGalaxyLayout.cs` | 星系聚类 / 均衡拆分 / 星系内分层 / 碰撞与标签避让；纯 Core，无 Avalonia |
| `src/PackDepTool.Core/Analysis/StarGalaxyCamera.cs` | 纯数学相机：屏幕↔世界、鼠标中心缩放、平移、Fit（可单测） |
| `src/PackDepTool.App/ViewModels/GalaxyScene.cs` | 渲染就绪场景：节点（含布局结果 + ModNode）、星系、边、跨星系聚合 |
| `src/PackDepTool.App/Controls/StarGalaxyView.cs` | SkiaSharp 自绘控件 + 输入状态机 + 总览面板 |
| `src/PackDepTool.App/Controls/GalaxyRenderCache.cs` | 图标解码缓存（SKImage）、画笔/字体复用、路径桶 |
| `tests/PackDepTool.Core.Tests/StarGalaxyLayoutTests.cs` | 布局/相机/标签避让单测（约 12 个） |

**删除**

- `src/PackDepTool.App/Controls/StarGraphView.cs`
- `src/PackDepTool.App/Services/StarWebViewHtml.cs`
- `src/PackDepTool.App/ViewModels/StarScene.cs`（由 `GalaxyScene.cs` 取代）
- `tools/StarProbe/`（WebView HTML 调试工具）
- `Assets/echarts.min.js` 及 csproj 中的 `None Update`

**修改**

- `src/PackDepTool.App/PackDepTool.App.csproj`：移除 `WebViewControl-Avalonia`、`echarts` 项
- `src/PackDepTool.App/Program.cs`：移除 `WebView.Settings.OsrEnabled`
- `src/PackDepTool.App/Views/MainWindow.axaml`：`StarHost` → `StarGalaxyView`
- `src/PackDepTool.App/Views/MainWindow.axaml.cs`：删除 WebView 字段/方法/桥；接控件事件
- `src/PackDepTool.App/ViewModels/MainWindowViewModel.cs`：场景构建、选中/双击同步、移除 star 分支

### 2.2 分层原则

```
GraphDocument ──► StarGalaxyLayoutEngine (Core, 纯函数, 一次)
                        │
                        ▼
                  GalaxyScene (App VM, 不可变快照)
                        │
                        ▼
            StarGalaxyView (SKCanvas: 变换 + 裁剪 + 绘制)
```

- **布局只在数据/筛选/双击聚焦时算一次**；选中、hover、缩放、平移、单节点拖动都不重排。
- 视图只读场景 + 自己的相机；不向 ViewModel 回写坐标。

---

## 3. 布局算法（`StarGalaxyLayout.cs`）

### 3.1 输入/输出

```csharp
public sealed class StarGalaxyOptions
{
    public int TargetPerGalaxy { get; set; } = 48;   // 270 / 48 → 6 个星系
    public int MaxGalaxyNodes  { get; set; } = 55;   // 硬上限
    public int MinGalaxyNodes  { get; set; } = 8;
    public double NodeRadius   { get; set; } = 22;
    public double HubRadius    { get; set; } = 30;
    public double BaseRingRadius { get; set; } = 250;
    public double RingGap      { get; set; } = 140;
    public double MinNodeSpacing { get; set; } = 74; // 直径 44 + 标签呼吸
    public double GalaxyGap    { get; set; } = 420;
    public double LabelFontSize { get; set; } = 11;
    public double LabelHeight  { get; set; } = 16;
    public int MaxDepthRings   { get; set; } = 6;
    public string? FocusNodeId { get; set; }         // 双击重生成
    public int Seed            { get; set; } = 20261120;
}

public sealed class StarGalaxyLayout
{
    public IReadOnlyList<StarGalaxyCluster> Galaxies { get; init; }
    public IReadOnlyDictionary<string, StarGalaxyNodePlacement> Nodes { get; init; }
    public IReadOnlyList<StarGalaxyCrossLink> CrossLinks { get; init; }
    public LayoutRect Bounds { get; init; }          // 世界坐标包围盒
}
```

`StarGalaxyCluster`：`Index / HubId / HubTitle / CenterX / CenterY / Radius / NodeIds / InternalEdges / CrossEdges`。
`StarGalaxyNodePlacement`：`Id / GalaxyIndex / Ring / X / Y / IsHub / Degree / LabelVisible / LabelRect`。
`StarGalaxyCrossLink`：`FromGalaxy / ToGalaxy / Count / RequiresCount`（**无向对唯一化**）。

### 3.2 枢纽识别与分星系

1. 建无向邻接（忽略 `Incompatible` 与自环，按 `(From,To)` 去重）。
2. 节点得分：
   `score = Σ weight(type)`，`Requires=3, Embedded=2, Recommends/Optional/Manual=1`；
   `degree = 去重邻居数`。
3. 目标星系数：`target = clamp(ceil(n / TargetPerGalaxy), 1, 8)`；270 → **6**。
4. 选 hub（贪心，确定性）：
   - 按 `(score desc, degree desc, Id ordinal)` 排序；
   - 跳过与已选 hub 的 BFS 距离 `< 2` 的候选，保证星系核分散；
   - 若凑不满 target，放宽距离约束再取。
5. 多源 BFS 分配：每个节点归最近 hub；等距按 hub 顺序；孤立节点先挂起。
6. 均衡拆分：任一星系 `> MaxGalaxyNodes` 时：
   - 取最大星系，找**次级枢纽** = 星系内 `score` 最高、且内部邻居 ≥2、非主 hub 的节点；
   - 从次级枢纽做局部 BFS，把“到次级枢纽更近”的那部分切出去成新星系；
   - 新星系 `< MinGalaxyNodes` 时改为把最远尾节点迁到最近的有容量星系；
   - 循环直到全部 ≤ 55。
7. 小星系合并：`< MinGalaxyNodes` 的并入最近且有容量的星系（无容量则保留）。
8. 孤立节点：按“到最近星系中心的距离”塞入容量最小的星系；保证 **所有节点恰好出现一次**。
9. `FocusNodeId`：存在时强制星系 0 的 hub = 该节点，先收 1 跳邻居（≤ cap），其余节点再走常规流程；双击行为即此路径。
10. 星系顺序：focus 优先，其余按 `(score desc, Id)` 稳定排序。

### 3.3 星系内分层

1. 星系内 BFS 深度（无向）作为 `Ring`；hub 为 0。
2. 每环节点排序：ring 1 按 `Id`；ring k 按“父节点角度，再 Id”，让子节点靠近父节点方位。
3. 环半径：
   ```text
   rBase = BaseRingRadius + (ring-1) * RingGap
   rFit  = MinNodeSpacing / (2 * sin(π / count))     // 保证同环弦长 ≥ 间距
   r     = max(rBase, rFit)
   ```
   深度超过 `MaxDepthRings` 的节点并入最后一环（均分角度）。
4. 角度：`angle = -π/2 + 2π*i/count + ringPhase`，`ringPhase` 由 `seed+galaxyIndex` 派生的小偏移，避免所有环径向对齐。
5. 星系中心：网格排布 `cols = ceil(sqrt(galaxyCount))`（6 → 3×2），
   `dx = 2*maxGalaxyRadius + GalaxyGap`，`dy = 2*maxGalaxyRadius + GalaxyGap*0.9`；
   先算局部坐标，再整体平移到正象限。
6. 碰撞消解（每星系 O(k²)，k≤55）：固定 hub，其余节点两两检查 `dist < MinNodeSpacing`，沿法线各推一半；迭代 2 次；最后不再改变环归属。
7. `galaxy.Radius = max(dist(center,node)) + NodeRadius + 40`（含标签）。

### 3.4 标签避让（布局阶段一次完成）

- 估算文本宽度（无需字体）：
  `width = Σ rune: CJK/全角 = 1.0em, 其他 = 0.55em`，夹在 `[44, 170]`；标题优先 `NameZh → Name → Id`。
- 候选位置顺序：**下方**（`x - w/2, y + r + 4`）→ 上方 → 右 → 左。
- 优先级：hub 先，其次 `degree desc / Id`。
- 拒绝条件：与已放置标签矩形（inflate 2）相交，或与任一节点圆包围盒（inflate 2）相交；全失败则 `LabelVisible=false`。
- 视口只决定“画不画”，不改变避让结果；hover 标签用**光标旁 tooltip**，不参与避让。
- 绘制时按 `LabelRect.Width` 用 SKFont 实测截断 + `…`，保证不越界。

### 3.5 跨星系聚合

- 对每条边，若两端星系不同，按无序星系对 `(min,max)` 聚合：
  `Count++`；`RequiresCount += type==Requires ? 1 : 0`。
- 主图默认**不画**跨星系边；仅选中/hover 节点时画该节点的跨星系对端（虚线 + 高亮）。
- 总览面板用聚合对绘制（线宽 `1 + log2(count)`，透明度随 count）。

---

## 4. 相机（`StarGalaxyCamera.cs`，纯数学）

```csharp
public readonly record struct StarGalaxyCamera(double Zoom, double PanX, double PanY)
{
    public LayoutPoint ScreenToWorld(double sx, double sy);
    public LayoutPoint WorldToScreen(double wx, double wy);
    public StarGalaxyCamera ZoomAt(double sx, double sy, double factor, double min, double max);
    public StarGalaxyCamera Panned(double dx, double dy);
    public static StarGalaxyCamera Fit(LayoutRect world, double viewW, double viewH, double padding);
}
```

- `ZoomAt` 必须保持光标下世界点不动：
  `world = (screen - pan)/zoom;  newPan = screen - world*newZoom`。
- 缩放范围 `[0.15, 4.0]`。
- 视图尺寸变化时若 `_userAdjusted == false` 才重新 `Fit`；用户操作过后保持。

---

## 5. 渲染（`StarGalaxyView.cs` + SkiaSharp）

### 5.1 接入方式

```csharp
protected override void Render(DrawingContext context)
    => context.Custom(new GalaxyDrawOperation(this, BuildFrame()));
```

`GalaxyDrawOperation : ICustomDrawOperation`，在 `Render(ImmediateDrawingContext ctx)` 中：

```csharp
if (!ctx.TryGetFeature<ISkiaSharpApiLeaseFeature>(out var feature)) { /* 画一行降级提示 */ return; }
using var lease = feature.Lease();
DrawSkia(lease.SkCanvas);
```

- 直接用 `SKCanvas`：`Save/Scale/Translate/ClipRect`，无位图回传、无中间 RenderTarget。
- 注意：lease canvas 已含 DPI 变换；字体按逻辑像素设定，实测校正（见 §11 风险）。
- 所有 `SKPaint/SKFont/SKPathEffect` 在 `GalaxyRenderCache` 中按颜色/线型复用；每帧不 `new`。

### 5.2 场景构建（每次 `Scene` 变化一次）

`GalaxyScene` → 视图内部 `RenderData`：

- 节点数组：`Id / 世界坐标 / 半径 / 填充色 / 边框色 / 状态色 / 标签矩形 / showLabel / galaxyIndex / ring / isHub / 命中矩形`；
- 边桶：`Requires / OptionalRecommends / Embedded / Manual / Incompatible` 各一条 `SKPath`（世界坐标，一次性构建）；
- 星系圆盘：每个星系的 `SKShader` 径向渐变（缓存）；
- 图标：**后台解码**，按批 32 个 `Task.Run`；`SKImage.FromEncodedData(bytes)` → 缩放到 64×64 → 存入 LRU（上限 360）；每批完成后 `Dispatcher.UIThread.Post(InvalidateVisual)`；`CancellationToken` 在 `Detach` 时取消。
- 首帧：图标未就绪画占位圆环；布局与场景构建本身 ≤ 20ms。

### 5.3 每帧绘制顺序

1. 背景 `#0B0D12` + 顶部微渐变（可选）。
2. 星系圆盘：`radialGradient(中心亮 #1E293B@0.55 → 边缘 0)`；星系标题（左上角，`星系 N · 45 个模组`）。
3. 星系内边（批量路径，每桶一次 `DrawPath`）：
   - Requires 实线 1.4；Optional/Recommends 虚线 1.0；Embedded 紫 1.2；Manual 琥珀 1.2。
   - Incompatible 默认不画，仅选中/hover 时画。
4. 选中/hover 的跨星系边（虚线，指向对端）。
5. 节点（**视口裁剪**：世界包围盒外扩 64 跳过）：
   - 底盘圆 + 2px 边框；hub 更大（36）、外发光；
   - 图标圆裁剪（`SKRoundRect`/圆路径 clip，`DrawImage` 64→40）；
   - 状态点（右上，缺失/冲突红、版本不匹配黄、正常绿）；
   - 角色色：hub 金 `#F5C542`、内圈（ring=1）蓝 `#3B82F6`、外圈（ring≥2）灰蓝 `#64748B`、可选关系节点绿边 `#34D399`（按主要入边类型）。
6. 标签（视口内 + `LabelVisible`）：底板半透明圆角 + 11px 文本；搜索命中加亮。
7. 选中环 / hover 环（只画一个，轻量）。
8. 搜索：非命中节点 alpha 0.28（批量改 paint alpha，不重建）。
9. 屏幕空间覆盖层：**星系关系总览**（右上）、图例（左下）、操作提示（底部）。

### 5.4 总览面板（跨星系关系）

- 固定屏幕位置：右上 `220×170`，圆角 10，背景 `#141822F2`，边框 `#2B3345`。
- 标题：`星系关系总览`；每个星系一个点（颜色按 hue 轮转，半径 8，显示 `G1..Gn` 或星系序号）。
- 点位置：面板内小圆环布局（`r=52`），与主图空间顺序一致（同一 index 顺序），不重叠。
- 聚合边：二次贝塞尔曲线，宽度 `1+log2(count)`；`RequiresCount>0` 用蓝色，否则灰色；透明度 `0.25+0.1*count` 封顶。
- 交互：点击聚合点 → `camera = Fit(galaxy.Bounds)`，选中该星系 hub，`InvalidateVisual`；点击面板其他区域不穿透。
- 折叠：点击标题右侧 `−/＋` 切换 `ShowOverview`（纯状态，无动画）。

### 5.5 图例（左下）

`● 星系核心` / `● 直接依赖` / `● 深层依赖` / `● 可选关系`；`滚轮缩放 · 拖空白平移 · 拖节点移动 · 双击重生成星系`。

---

## 6. 交互状态机

| 手势 | 行为 | 重绘范围 |
| --- | --- | --- |
| 左键按下节点 | 记录 `_hitNode`，`Pointer.Capture`，进入“可能拖动” | 无 |
| 移动（已捕获节点） | 更新该节点世界坐标 + 命中矩形 + 标签矩形，`InvalidateVisual` | 整控件一次（必要）；**不重排** |
| 左键按下空白 / 中键 | 进入平移，记录起点 | 无 |
| 移动（平移） | `camera = camera.Panned(dx,dy)` | 一次 |
| 释放（节点未移动） | 选中该节点，`NodeSelected` 事件，不 Fit、不重建 | 一次 |
| 双击节点 | `NodeActivated` 事件 → VM 以该节点重建星系 | 重建后 Fit 一次 |
| 滚轮 | `camera.ZoomAt(指针, 1.15^Δ)`，夹 `[0.15,4]`，`e.Handled=true` | 一次 |
| hover 变化 | 更新 `_hoverId`，`InvalidateVisual` | **仅当目标变化**，鼠标原地移动不重绘 |
| 触摸双指（可选） | 暂不做，避免误触 | — |
| 右键 | 保留给现有快捷菜单（不处理） | — |

- `SearchText` 变化：计算匹配集合（标题/id/Name 不区分大小写）；`Fit(matches bbox)` + 非命中降透明；**不做动画**。
- 单击选中时若节点在屏幕外（例如通过列表选中），`PanToNode` 一次。
- 命中测试：从后往前遍历缓存矩形（节点圆 + 标签底板），O(n)=270；坐标先反变换到世界。
- 拖动结束不吸附、不重建，位置保留在 `GalaxyScene` 节点上。

---

## 7. ViewModel 集成

### 7.1 新属性

```csharp
[ObservableProperty] private GalaxyScene? _galaxyScene;
[ObservableProperty] private string? _starSelectedNodeId;   // 控件 ↔ VM 选中同步
private string? _starFocusNodeId;                            // 双击锁定
```

### 7.2 `BuildGalaxyScene()`

```csharp
private void BuildGalaxyScene()
{
    if (_graph is null) { GalaxyScene = null; return; }

    var nodes = _graph.Nodes
        .Where(n => !n.IsSynthetic && !n.IsEmbedded)
        .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .ToList();                                  // 270 个，全部显示
    var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var edges = _graph.Edges
        .Where(e => ids.Contains(e.From) && ids.Contains(e.To))
        .ToList();

    var layout = new StarGalaxyLayoutEngine().Compute(
        nodes, edges, new StarGalaxyOptions { FocusNodeId = _starFocusNodeId });

    GalaxyScene = GalaxyScene.From(layout, nodes, edges);   // O(n+e)，UI 线程 ≤5ms
    StarSelectedNodeId = _selectedNodeId;
}
```

> 星图**忽略状态/加载器/搜索筛选**（验收要求 270 全显）。搜索只高亮。此语义写进控件提示与文档。

### 7.3 选中同步

```csharp
public void SelectGalaxyNode(string id)
{
    var node = _graph?.FindNode(id); if (node is null) return;
    _selectedNodeId = node.Id; _selectedEdgeId = null;
    UpdateNodeDetail(node);
    _suppressSelectionSync = true;
    SelectedNodeItem = NodeItems.FirstOrDefault(n => ...);
    SelectedManualLink = null; SelectedIssue = null;
    _suppressSelectionSync = false;
    StarSelectedNodeId = node.Id;
    // 不调用 _graphView.SelectNode，不 BuildScene，不 BuildGalaxyScene
}
```

- `OnSelectedNodeItemChanged`：**删除** `if (IsStarView) BuildStarScene();`；
  若 `IsStarView` 且 id 变化 → `StarSelectedNodeId = value?.Id`。
- 列表点击 → `SelectNode()` 仍会 `_graphView?.SelectNode`（graph 隐藏无害）；再同步 `StarSelectedNodeId`，控件负责平移。

### 7.4 双击重生成

```csharp
public void FocusGalaxy(string id)
{
    if (_graph?.FindNode(id) is null) return;
    _starFocusNodeId = id;
    SelectGalaxyNode(id);
    BuildGalaxyScene();       // FocusNodeId → 该节点成为星系 0 hub
}
```

### 7.5 清理 star 特判

- `BuildScene()`：删除 `_starVisibleIds` 初始化、`(!IsStarView || ...)` 条件、`FocusEdgesOnly = IsStarView ? false : ...` 分支、末尾 star `FitToView` 定时器。
- `LayoutAsync()`：删除 star 的 `LayoutAlgorithm.Star / StarNodeLimit=16` 分支。
- `OnSelectedViewOptionChanged`：
  - 进入 star：`ShowDetailsPanel=false; BuildGalaxyScene();`
  - 离开 star：`GalaxyScene = null;`（释放图标/场景，满足“切换页面释放控件资源”）。
- `OnSearchTextChanged`：`if (IsStarView) return;`（控件自己处理高亮/聚焦）。
- 删除 `_starVisibleIds`、`RecenterStar`、`BuildStarScene`。

### 7.6 XAML

```xml
<Grid IsVisible="{Binding IsStarView}">
  <controls:StarGalaxyView x:Name="GalaxyView"
                           Scene="{Binding GalaxyScene}"
                           SearchText="{Binding SearchText}"
                           SelectedNodeId="{Binding StarSelectedNodeId, Mode=TwoWay}" />
</Grid>
```

- 删除 `xmlns:wv`（若无其他用途）。
- 顶部标题/搜索框/筛选 Flyout 复用现有，不改布局。

### 7.7 Code-behind

- 删除 `_starWeb`、`QueueStarWebReload`、`EnsureStarWeb`、`ReloadStarWeb`、`StarBridge`、PropertyChanged 中的 StarScene 分支、`AttachViewModel` 末尾的 `QueueStarWebReload()`。
- 新增：
  ```csharp
  GalaxyView.NodeSelected += id => _viewModel?.SelectGalaxyNode(id);
  GalaxyView.NodeActivated += id => _viewModel?.FocusGalaxy(id);
  ```
- 保留 `Closed += (_, _) => Environment.Exit(0);`（无 WebView 后进程立即退出）。

### 7.8 启动路径

- `Program.cs` 删除 `WebViewControl.WebView.Settings.OsrEnabled = true` 的 try/catch 与 `using`。
- csproj 删除 `WebViewControl-Avalonia`、`echarts.min.js` 项。
- 删除 `tools/StarProbe`（已失效）。

---

## 8. 性能预算与实现约束

| 指标 | 目标 | 手段 |
| --- | --- | --- |
| 进入星图首帧 | < 300ms | 布局 ≤20ms；图标后台解码，首帧占位；不加载 WebView |
| 拖动 270 节点 / 650 边 | 50–60 FPS | 布局缓存；批量路径；视口裁剪；画笔缓存；仅整控件一次重绘 |
| 空闲 CPU | ≈0% | 无定时器、无动画；只有输入事件触发重绘 |
| 鼠标移动 | 不重绘风暴 | hover id 不变不 Invalidate；命中测试用缓存矩形 |
| 内存增量 | < 150MB | 图标 64×64 LRU ≤360（≈6MB）；场景数组 O(n+e)；无 WebView/CEF |
| 页面切换 | 释放 | `GalaxyScene=null` + `OnDetachedFromVisualTree` 取消解码/清缓存 |

禁止：在 `Render` 中计算布局、分配画笔、重建几何、发起 IO；每帧 O(n²) 碰撞。

---

## 9. 测试计划（`StarGalaxyLayoutTests.cs`）

合成图：270 节点 / 650 边，8 个高连接枢纽 + 长链 + 孤立节点 + 跨星系边。

1. `AllNodesPlacedExactlyOnce`：输出节点集合 = 输入集合（270）。
2. `GalaxyCountAtLeastSix`：星系数 ≥6；每个 ≤55。
3. `HubsHaveHighestInternalDegree`：每个星系 hub 的内部度 ≥ 该星系平均值。
4. `LayoutIsDeterministic`：同输入两次，坐标/分层逐一相等。
5. `FocusNodeBecomesGalaxyZeroHub`：`FocusNodeId` 生效，星系 0 hub 正确，≤55。
6. `IsolatedNodesStillAppear`：10 个孤立 + 40 个连接节点，全部出现。
7. `CrossLinksAggregateUniquePairs`：聚合 count 之和 = 跨星系边数；无序对唯一；索引合法。
8. `GalaxyCentersDoNotOverlap`：任意两星系中心距 > 两者半径和。
9. `NodesKeepMinimumSpacing`：同星系任意两点距离 ≥ `MinNodeSpacing - 0.5`。
10. `LabelsDoNotOverlapAndAvoidNodes`：所有 `LabelVisible` 矩形两两不相交，且不与节点圆相交。
11. `CameraZoomAtCursorKeepsAnchor` / `CameraFitContainsBounds` / `CameraPanMovesWorld`。
12. `LayoutCompletesWithinBudget`：300 节点 / 700 边 < 250ms（CI 宽松阈值，仅防回归）。

现有 178 个测试必须保持全绿。`StarLayoutTests`（GraphLayoutEngine）不受影响。

---

## 10. 验收步骤（本地）

1. **构建 / 测试**（§1.4 命令）：
   - `dotnet build` → 0 error；
   - `dotnet test` → 190 左右全绿。
2. **运行**：
   ```bash
   cd /c/Users/Lenovo
   /c/Users/Lenovo/.dotnet/dotnet.exe run --project F:/DSH-Workspace/PackDependencyTool/src/PackDepTool.App/PackDepTool.App.csproj -c Debug
   ```
   打开 `E:\PackDependencyTool\data` 对应实例，切到「星图」视图。
3. **截图验收**：`tools/launch-capture.ps1`（`-Out screenshots/star.png`，可先点击「星图」视图按钮）。
   - 270 个模组全部出现；
   - ≥6 个可辨认星系，单星系 ≤55；
   - 图标 + 名称可见，角色配色区分；
   - 右上总览面板可见。
4. **交互手测**：
   - 拖单节点 → 只有该节点移动，松手保留；
   - 拖空白 → 平移；滚轮 → 鼠标位置缩放；
   - 单击 → 选中，视图不重置；双击 → 以该节点重建星系；
   - hover 不闪；搜索 → 高亮 + 自动聚焦；
   - 切回其他页 → 无后台定时器/CPU。
5. **退出**：关闭窗口后 `Get-Process PackDependencyTool` 为空；任务管理器无 WebView/CEF 子进程。
6. **空闲 CPU**：停留星图 30s，任务管理器 CPU ≈0%。
7. **内存**：切换前后私有工作集增量 < 150MB。

> 不 publish Release；只提交源码 + Debug 可运行输出。

---

## 11. 风险与对策

| 风险 | 对策 |
| --- | --- |
| `ISkiaSharpApiLeaseFeature` 命名空间/包不可用 | 先写 10 行 spike 编译验证（`Avalonia.Skia` 由 `Avalonia.Desktop` 传递）；不行则退到 `DrawingContext` 自绘（仍是原生、无 WebView） |
| 自绘在渲染线程读取可变场景 | 场景对象构建后不可变；拖动只改 `double` 坐标（x64 原子）；渲染按帧读取 |
| DPI 缩放导致文字/圆点偏大 | 用 lease canvas 的现有缩放；读取 `RenderScaling` 校准字号（一次） |
| 中文字体缺失 | `SKTypeface.FromFamilyName("Microsoft YaHei UI")` → 回退 `"Microsoft YaHei"` → `null`（默认） |
| 270 节点首次图标解码卡顿 | 后台分批 + 首帧占位；只解码视口内优先 |
| 布局在 UI 线程超预算 | 目标 ≤20ms；若实测超 60ms，改 `Task.Run` + 取消令牌（场景到达前显示“布局中”） |
| 星图忽略筛选引起困惑 | 控件底部提示“显示全部已安装模组 · 搜索仅高亮” |
| global.json SDK 不匹配 | 按 §1.4 从 `C:\Users\Lenovo` 运行显式 SDK，不改仓库配置 |

---

## 12. 实施顺序（可逐步提交）

1. [ ] `StarGalaxyCamera.cs` + 单测（纯数学，先绿）
2. [ ] `StarGalaxyLayout.cs` + 单测（聚类/拆分/分层/避让/聚合）
3. [ ] `GalaxyScene.cs`（映射模型 → 渲染就绪）
4. [ ] `StarGalaxyView.cs`：Skia adapter spike → 背景/星系/边/节点/标签
5. [ ] 图标缓存 + 后台解码 + 首帧占位
6. [ ] 交互：命中、单节点拖、平移、鼠标中心缩放、hover、单击/双击
7. [ ] 搜索高亮 + 自动聚焦；总览面板 + 图例 + 折叠
8. [ ] VM 接线（`BuildGalaxyScene/SelectGalaxyNode/FocusGalaxy`），删除 star 特判
9. [ ] XAML + code-behind 替换，删除 WebView 路径与工具
10. [ ] `dotnet test` 全绿；`dotnet run` 截图 + 手测验收
11. [ ] 提交（不 publish Release），记录验收路径

---

## 13. 当前进度

- [x] 阅读全部指定文件与相关调用链
- [x] 确认 WebView 路径、旧控件死代码、16 节点限制、每帧重排根因
- [x] 基线构建 + 178 测试全绿
- [x] 定位本机 SDK 与可复现构建命令
- [x] 方案与文件清单定稿（本文档）
- [x] 编码：相机 / 星系布局 / 原生 Skia 控件 / 场景 / VM 接线 / 删除 WebView
- [x] 测试：193 全绿（新增 15 个布局/相机测试）
- [x] 离线预览：`tools/StarPreview` 直接把真实控件渲染成 PNG，无需启动主程序
- [x] 真实数据：270 节点 / 370 边 / 9 个均衡星系（≤55）/ 布局约 43ms
- [x] 提交到 `feat/native-star-galaxy`

> 恢复工作时：先读本文档 §1.4 命令；渲染预览用 `tools/StarPreview/README.md`。
