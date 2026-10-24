# Changelog

## 1.0.3

### 模组列表

- 模组列表改为虚拟化渲染，并预加载上下各约一屏。
- 依赖线支持跨虚拟窗口连续绘制。
- 新增按文件时间排序，以及按分类、按加载器排序。
- 正序 / 倒序切换会立即刷新列表。
- 工具栏压缩，减少重复标签和操作提示。
- 详情面板的基本信息、简介、依赖关系支持独立折叠。
- 加载器筛选只显示实际存在的加载器；多加载器 metadata 的模组跟随实例加载器。

### 依赖关系

- 独立安装的模组优先于同名 Jar-in-Jar，不再被内嵌副本隐藏。
- 缺失依赖和缺失节点连线改为红虚线。
- 缺失依赖支持从列表直接添加本地忽略规则。
- 修复定位被筛选隐藏、未安装必需/可选依赖时无反应的问题。
- 分类词条扩充，分类结果增加内容签名缓存和预归一化。

### 下载与更新

- 本次加入下载中心的队列任务全部成功后自动关闭下载窗口。
- 「可更新」筛选改为显式检查：批量哈希识别项目、只查询已知项目。
- 更新检查增加 6 小时缓存、检查统计和取消按钮。
- 图标缓存增加上限，切换整合包自动清理，列表图标统一居中、等比显示。
- 新增程序图标。

### 性能

- 本地规则新增、删除、启停和导入改为后台构图。
- 图标缓存与分类结果复用，问题数量一次预计算。
- 虚拟化列表避免一次性创建全部行和图标。

## 1.0.2

- 修复依赖版本范围判定：预发布版本排序、精确版本和未生效冲突声明。
- 修复同一目标同时显示冲突与可选声明时方向不清的问题。
- 联网补全不再为同一对模组追加矛盾关系；在线边标注无版本范围。
- 批量下载/升级遵守所有入边必需版本范围。
- 下载支持断点续传、重试、哈希校验和失败清理。
- 游戏 libraries/assets 并发下载，下载中心支持队列、暂停、继续和重试。
- 设置支持游戏下载源、Modrinth/CurseForge API 基址和 Mod CDN 基址。
- 依赖关系图、详情关系和 PNG 导出区分未生效冲突。
- 优化关系连线重绘和下载队列 UI 刷新频率。
- 修复空关键词无法搜索、分类分页宽度跳动和底部缺失依赖区域占位。

## 1.0.1

- 客户端 / 服务端 / 双端侧标记，列表、详情与导出均包含。
- 新增侧筛选；导出的 CSV/Markdown/GraphML 增加侧与依赖明细。
- 详情操作按钮改为固定网格对齐；已安装模组显示“更换版本”，下载后自动禁用旧版本。
- 模组详情与已禁用列表支持永久删除。
- 列表取消虚拟化，修复远距离模组连接线不绘制。
- 补全入口合并；离线补全先显示可勾选计划。
- 游戏安装窗口重排，支持实例名称与取消安装。
- 修复加载器安装器无法使用系统代理的问题。

## 1.0.0

- Portable Windows desktop modpack dependency analyzer.
- Fabric/Quilt/Forge/NeoForge metadata scanning.
- Dependency graph with relation focus, rails, filters and reports.
- Modrinth/CurseForge enrichment and downloads.
- Download center pagination, categories and installed markers.
- Optional update check via a public `update.json`; optional SHA256-verified
  auto-download and opt-in restart-to-update.
- Launcher-compatible instance import/export and loader switching.
- Removed bundled third-party translation data; Chinese names are user-provided
  overrides only.
