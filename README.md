# Pack Dependency Tool

一个面向 Windows 的 **Minecraft 实例 / 模组关系工作台**。

它不只是画依赖图：把实例管理、模组下载、依赖诊断、崩溃日志分析、备份历史和 Axolotl 伴生启动放在同一个界面里，尽量少开弹窗、少来回切工具。

> 本工具不是账号启动器的替代品。微软登录、正版验证仍由启动器负责；离线启动只是本地调试入口。

## 核心功能

### 依赖与模组

- **关系清单**：按问题优先排列，清晰显示必需、可选、推荐、不兼容、内嵌和手动关系。
- **关系图**：缩放、平移、框选、节点定位、单模组关系聚焦。
- **问题检查**：缺失依赖、版本范围不匹配、声明冲突、循环依赖、重复模组。
- **模组清单**：搜索、按加载器/状态筛选、批量启用/禁用、中文名与图标。
- **导入导出**：表格模组列表、`modlist.html`、`.mrpack`、JSON/DOT/GraphML/CSV、Markdown 报告、长图。
- **整合包合并**：按 `modId` 去重，目标包优先，配置非破坏合并。

### 实例与启动

- 首页切换实例，实例管理独立完成新建、导入、PCL 目录识别和目录打开。
- 支持 PCL / HMCL / 官方 `versions` 目录，也支持普通整合包目录。
- 按 Minecraft 版本匹配 Java，使用 `javaw` 启动，避免命令行窗口。
- 自动内存分配、调试模式启动、启动参数隔离（每个实例自己的 `--gameDir`）。
- 离线用户名启动；不保存密码或正版 token。

### Axolotl 伴生启动

- 在「设置 → 外部启动器」中自动探测 Axolotl Launcher。
- 读取 `%APPDATA%\red.ghs.axolotl\release\app.db` 的实例列表，合并到首页。
- 对外部 Axolotl 实例调用官方 URL Scheme：

```text
axolotl://launch?instance_id=<id>
```

- 只读实例数据库和调用协议，不修改 Axolotl 文件。
- 这是 **伴生工具**，不是 Axolotl 内部插件；Axolotl 当前 MCP 只提供 `get_launcher_info` / `start_session` / `end_session`，没有实例或日志读取工具。

### 诊断

- **Crash Doctor**：读取 `crash-reports/*.txt`、`latest.log`、`hs_err`，提取事实、异常链、相关模组和修复建议。
- 支持直接选择外部日志文件或 `.zip` 日志包；自动优先读取 crash report 和 `latest.log`，跳过 `debug.log`。
- 专门识别：
  - Feature placement / `PlacedFeature` 的 Stream 崩溃
  - Thaumaturge 与 JEI 的 `NoSuchMethodError` API 不匹配
  - Mixin 注入失败、缺失依赖、Java / OOM / 原生崩溃
  - KubeJS 脚本错误、无效物品刷屏、AllTheLeaks 次要报告
- 顶部「结论」卡直接给出首要原因和修复方向，错误按严重性排序，跨文件结果去重。

### 备份与历史

- 每次扫描自动记录模组列表快照。
- 文件快照对比：新增、删除、修改。
- 模组列表历史对比：新增、移除、版本变化、名称变化。
- **导出变更日志**：把对比结果导出为 Markdown 表格。
- 快照恢复；缺失的旧 JAR 可通过在线 SHA1 精确匹配补齐。

### 下载与更新

- Modrinth / CurseForge 双源搜索，按实例的游戏版本和加载器过滤。
- 下载队列：进度、暂停、取消、重试。
- 模组、整合包、资源包和游戏本体 / 加载器安装。
- 下载中心是主页面，不再依赖二级弹窗。
- 更新检查默认手动；自动下载、自动安装默认关闭，更新要求 SHA256 匹配。

### 界面

- 默认暗色低对比主题，支持切换 Light。
- **自定义强调色**：设置中填写 `#RRGGBB`，应用到按钮、导航和选中态。
- 左侧任务导航：首页、发现、实例、备份、模组、诊断、服务端。
- 首页保留当前实例、健康概览、任务入口和常驻运行控制台。

## 快速开始

1. 从 [Releases](https://github.com/Chashao-mao/PackDependencyTool/releases/latest) 下载 `PackDependencyTool.exe`。
2. 双击运行，无需安装 .NET。
3. 首页选择实例，或拖入包含 `mods` 的目录。
4. 先做一次体检，再进入模组列表处理依赖。
5. 需要下载内容时进入「发现」；需要崩溃分析时进入「诊断」。

## 系统要求

- Windows 10 / 11 x64
- 发布版为自包含单文件，不需要单独安装 .NET
- 联网功能：Modrinth / CurseForge 搜索、图标、在线补全、更新检查
- 服务端日志分析不要求 Minecraft 正在运行

## 构建

需要 .NET 9 SDK：

```powershell
dotnet build src/PackDepTool.App/PackDepTool.App.csproj -c Release
dotnet test tests/PackDepTool.Core.Tests/PackDepTool.Core.Tests.csproj
```

发布单文件：

```powershell
dotnet publish src/PackDepTool.App/PackDepTool.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o dist/PackDependencyTool-win-x64
```

## 隐私与安全

- 本项目不需要 Microsoft 账号密码。
- 离线用户名只用于生成离线 UUID。
- Axolotl 适配只读取实例数据库并调用已注册的 `axolotl://` 协议；不写入 Axolotl 数据。
- 更新包必须匹配 `update.json` 中的 SHA256，并使用项目固定签名证书验证。

## 说明

- 本工具不是启动器的完整替代品；正版登录请使用官方或你信任的启动器。
- 不附带第三方中文译名索引；中文名来自本地资源包或用户自定义。
- 服务端日志分析是“基于证据的规则诊断”，不会替代人工阅读完整崩溃报告。
- 本项目与 Axolotl Launcher 没有从属关系，只做协议和文件格式层面的互操作。

## 下载

<https://github.com/Chashao-mao/PackDependencyTool/releases/latest>
