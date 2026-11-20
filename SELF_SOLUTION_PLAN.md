# 自研功能实施设计

对应 `FEATURE_ROADMAP.md` 中不适合直接引入外部技术栈的目标。

## 1. 原生图布局 / 交互（替代 Cytoscape.js / G6）

复用 `GraphLayoutEngine`，补 Axolotl 的结构：

- 固定节点尺寸：228 × 76
- 分层间距：112
- 连通分量间距：96
- 画布内边距：56
- 边避让间隙：2
- 先按必需依赖计算层，再放孤立/循环节点
- 循环节点标注，未解析依赖单独成节点
- 节点拖拽后固定偏移，窗口缩放不重排
- 图视图只画边到 Canvas，节点卡片在 Avalonia 控件层

不引入 WebView。

## 2. 自制版本约束求解器（替代 PubGrub）

不做完整 PubGrub，先做对 Minecraft 模组足够用的回溯 + 前向检查：

### 输入

- 已安装模组：modId、版本、加载器、MC 版本
- 本地/在线候选版本：版本号、版本范围、必需/不兼容声明
- 用户偏好：核心前置不动、内容模组优先保留大版本

### 算法

1. 变量 = 需要选版本的 modId
2. 域 = 满足 MC/加载器的候选版本
3. 约束 = 必需依赖范围 + 不兼容声明
4. 选约束最多的变量，尝试候选版本
5. 前向检查：删除与已选版本冲突的候选
6. 失败时回溯，并记录冲突链
7. 超时/无解时返回“哪条约束导致断裂”，不返回假方案

### 分阶段

- [x] 阶段 1：只校验当前已安装组合是否满足声明的必需范围（`VersionSetSolver`）
- [ ] 阶段 2：加入候选版本
- [ ] 阶段 3：输出方案
- 阶段 2：加入 Modrinth/CurseForge 候选版本
- 阶段 3：输出升级/降级方案，并复跑字节码冲突检查

### UI

“求解当前组合”入口放在诊断页；结果列出改动项、无法满足的约束和受影响的爆炸半径。

## 3. 原生仪表盘（替代 Grafana / React）

信息架构：

- 顶部四张指标卡：MSPT、1% Low、有效加载耗时、内容冲突数
- 中部：MSPT 趋势图 + 有效加载耗时 Top N 条形图
- 右侧：归因抽屉（自身耗时 vs 前置共享耗时 + 冲突边）
- 底部：变更时间线/快照对比

图表用 Avalonia 原生绘制，不引入 React/Tailwind。

## 4. Spark / Caliper 自动读取（已完成基础）

新增：

- `ProfilerReportDiscovery` 扫描 `logs/spark`、`config/spark`、`spark`、`logs/caliper`、`caliper`、`logs` 下的 JSON/CSV/TXT 报告
- Caliper CSV/JSON 解析为 `caliper-load-cost` 诊断线索，按耗时排序
- 性能命令优先自动读取报告，不再默认拿 `latest.log`
- 手动选择支持 `.json/.csv`

后续：

- 解析 Spark JSON 的 profile 采样树
- 将 Caliper 模组耗时映射到依赖图节点
- 仪表盘直接展示这些数据
