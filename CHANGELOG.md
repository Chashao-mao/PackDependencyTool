# Changelog

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
