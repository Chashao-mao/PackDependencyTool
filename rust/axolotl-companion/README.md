# axolotl-companion

轻量 Rust companion，通过 Axolotl Launcher 自带的本地 MCP 服务读取实例、启动游戏和创建备份。它不复制 Axolotl UI，也不直接写 Axolotl 数据库。

## 前置

- Axolotl Launcher 已启动
- 在 Axolotl 设置中开启 MCP
- MCP 地址默认 `http://127.0.0.1:5219/mcp`
- 如果启用了身份验证，设置 `AXOLOTL_MCP_TOKEN`

Axolotl 的普通权限模式会在业务工具调用时弹出审批；必须在启动器里允许。审批请求未处理时，会返回：

```text
control_not_granted: Another control session or approval request is active
```

先处理掉 Axolotl 的审批弹窗，再重试。

## 构建

```powershell
cargo build --release
```

Windows 使用 GNU 工具链时，需要 MinGW 在 PATH 中。产物：

```text
target\release\axolotl-companion.exe
```

## 命令

```powershell
# 查看 Axolotl 暴露的工具
axolotl-companion tools
axolotl-companion tools instance

# 列出实例（自动 start_session / end_session）
axolotl-companion instances

# 启动实例
axolotl-companion launch <instance_id>

# 为实例创建备份
axolotl-companion backup <instance_id>

# 通用工具调用；--session 会注入 session_id
axolotl-companion call instance_get '{\"instance_id\":\"...\"}' --session
```

## 环境变量

| 变量 | 默认值 | 说明 |
|---|---|---|
| `AXOLOTL_MCP_URL` | `http://127.0.0.1:5219/mcp` | Axolotl MCP 地址 |
| `AXOLOTL_MCP_TOKEN` | 空 | Bearer token |
| `RUST_LOG` | `axolotl_companion=info` | 日志级别 |

## 边界

- 不直接打开/写 Axolotl SQLite
- 不注入 Axolotl 前端
- 不绕过启动器审批
- 业务工具都要求 `start_session`
- 启动/备份等操作由 Axolotl 执行，companion 只负责编排和读取结果
