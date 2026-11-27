# Axolotl Companion (Tauri 2 + Vue 3)

Axolotl Launcher 的轻量分析附属工具。只做 Axolotl 没有的能力：

- 实例读取（MCP）
- profile 路径解析
- Log Doctor：日志翻译、重复噪声隔离、可疑模组
- 启动 / 备份（通过 Axolotl MCP）

不复制实例管理、下载中心、账号、启动器 UI。

## 开发

需要 Node.js、Rust 和 WebView2 Runtime。

```powershell
cd rust/axolotl-desktop
npm install
npm run dev          # 前端
npm run tauri dev    # Tauri 窗口
```

如果 Rust 使用 GNU 工具链，确保 MinGW 在 PATH 中。

## 当前命令链路

- `list_instances` → `instance_list`
- `launch_instance` → `instance_launch`
- `backup_instance` → `backup_create`
- `analyze_log` → 解析 `logs/latest.log`

第一次受保护调用会让 Axolotl 弹出一次审批；之后在同一个 companion 进程内复用会话。

## 结构

```text
src/                 Vue 3 界面
src-tauri/src/
  axolotl.rs         MCP 客户端、路径解析
  logdoctor.rs       日志分析
  lib.rs             Tauri commands
```

## 边界

- 只读实例数据，不直接写 Axolotl 数据库
- 启动/备份由 Axolotl 执行
- 不注入 Axolotl 前端
- 不做启动器功能
