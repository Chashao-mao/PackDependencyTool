use std::path::{Path, PathBuf};
use std::sync::Arc;

use anyhow::{anyhow, Context, Result};
use rmcp::{
    model::{CallToolRequestParams, ClientConfig, InitializeRequestParams, Tool},
    service::RunningService,
    transport::{streamable_http_client::StreamableHttpClientTransportConfig, StreamableHttpClientTransport},
    ServiceExt,
};
use serde::Serialize;
use serde_json::{Map, Value};
use tokio::sync::Mutex;

type Client = RunningService<rmcp::RoleClient, InitializeRequestParams>;

#[derive(Debug, Clone, Serialize)]
pub struct Instance {
    pub id: String,
    pub name: String,
    pub path: String,
    pub resolved_path: String,
    pub game_version: String,
    pub loader: String,
    pub loader_version: String,
    pub install_stage: String,
    pub last_played: Option<String>,
}

pub struct AxolotlClient {
    client: Arc<Client>,
    session: Mutex<Option<String>>,
}

impl AxolotlClient {
    pub async fn connect(url: &str, token: Option<&str>) -> Result<Self> {
        let mut config = StreamableHttpClientTransportConfig::with_uri(url.to_string());
        if let Some(token) = token.filter(|token| !token.is_empty()) {
            config = config.auth_header(token);
        }
        let transport = StreamableHttpClientTransport::from_config(config);
        let client = ClientConfig::default()
            .serve(transport)
            .await
            .context("无法连接 Axolotl MCP；确认启动器已开启 MCP 并允许本客户端")?;
        Ok(Self {
            client: Arc::new(client),
            session: Mutex::new(None),
        })
    }

    async fn ensure_session(&self) -> Result<String> {
        let mut session = self.session.lock().await;
        if let Some(id) = session.clone() {
            return Ok(id);
        }
        let tools = self.client.list_all_tools().await?;
        let tool = tools
            .iter()
            .find(|tool| tool.name == "start_session")
            .ok_or_else(|| anyhow!("Axolotl 没有暴露 start_session"))?;
        let result = call(self.client.as_ref(), tool, Map::new()).await?;
        if let Some(message) = mcp_error(&result) {
            return Err(anyhow!("Axolotl 拒绝控制会话：{message}"));
        }
        let id = result
            .pointer("/structuredContent/session_id")
            .and_then(Value::as_str)
            .map(str::to_string)
            .ok_or_else(|| anyhow!("start_session 未返回 session_id"))?;
        *session = Some(id.clone());
        Ok(id)
    }

    async fn call_named(&self, name: &str, mut args: Map<String, Value>) -> Result<Value> {
        let session = self.ensure_session().await?;
        args.insert("session_id".into(), Value::String(session));
        let tools = self.client.list_all_tools().await?;
        let tool = tools
            .iter()
            .find(|tool| tool.name == name)
            .ok_or_else(|| anyhow!("工具不存在：{name}"))?;
        let result = call(self.client.as_ref(), tool, args).await?;
        if let Some(message) = mcp_error(&result) {
            return Err(anyhow!("{message}"));
        }
        Ok(result)
    }

    pub async fn list_instances(&self) -> Result<Vec<Instance>> {
        let result = self.call_named("instance_list", Map::new()).await?;
        let items = result
            .pointer("/structuredContent/items")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        Ok(items
            .into_iter()
            .map(|item| {
                let raw_path = item
                    .get("path")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_string();
                let resolved = resolve_instance_path(&raw_path);
                Instance {
                    id: item
                        .get("instance_id")
                        .and_then(Value::as_str)
                        .unwrap_or_default()
                        .to_string(),
                    name: item
                        .get("name")
                        .and_then(Value::as_str)
                        .unwrap_or("未命名")
                        .to_string(),
                    path: raw_path,
                    resolved_path: resolved.to_string_lossy().to_string(),
                    game_version: item
                        .get("game_version")
                        .and_then(Value::as_str)
                        .unwrap_or("?")
                        .to_string(),
                    loader: item
                        .get("loader")
                        .and_then(Value::as_str)
                        .unwrap_or("?")
                        .to_string(),
                    loader_version: item
                        .get("loader_version")
                        .and_then(Value::as_str)
                        .unwrap_or("")
                        .to_string(),
                    install_stage: item
                        .get("install_stage")
                        .and_then(Value::as_str)
                        .unwrap_or("?")
                        .to_string(),
                    last_played: item
                        .get("last_played")
                        .and_then(Value::as_str)
                        .map(str::to_string),
                }
            })
            .collect())
    }

    pub async fn launch(&self, instance_id: &str) -> Result<Value> {
        let mut args = Map::new();
        args.insert("instance_id".into(), Value::String(instance_id.to_string()));
        self.call_named("instance_launch", args).await
    }

    pub async fn backup(&self, instance_id: &str) -> Result<Value> {
        let mut args = Map::new();
        args.insert("instance_id".into(), Value::String(instance_id.to_string()));
        self.call_named("backup_create", args).await
    }

    pub async fn backup_config(&self, instance_id: &str) -> Result<Value> {
        let mut args = Map::new();
        args.insert("instance_id".into(), Value::String(instance_id.to_string()));
        self.call_named("backup_get_config", args).await
    }
}

async fn call(client: &Client, tool: &Tool, args: Map<String, Value>) -> Result<Value> {
    let params = CallToolRequestParams::new(tool.name.to_string()).with_arguments(args);
    let result = client
        .call_tool(params)
        .await
        .with_context(|| format!("调用工具失败：{}", tool.name))?;
    serde_json::to_value(result).context("序列化 MCP 结果失败")
}

fn mcp_error(value: &Value) -> Option<String> {
    if value.get("isError").and_then(Value::as_bool) != Some(true) {
        return None;
    }
    value
        .pointer("/structuredContent/error/message")
        .and_then(Value::as_str)
        .map(str::to_string)
        .or_else(|| {
            value
                .get("content")
                .and_then(Value::as_array)
                .and_then(|items| items.first())
                .and_then(|item| item.get("text"))
                .and_then(Value::as_str)
                .map(str::to_string)
        })
}

/// Axolotl stores only the profile name in `path`; resolve it to a real
/// directory under the launcher root without writing to Axolotl's database.
pub fn resolve_instance_path(raw: &str) -> PathBuf {
    if Path::new(raw).is_absolute() && Path::new(raw).exists() {
        return PathBuf::from(raw);
    }

    let mut roots = Vec::new();
    if let Ok(home) = std::env::var("AXOLOTL_HOME") {
        roots.push(PathBuf::from(home));
    }
    roots.push(PathBuf::from(r"E:\Axolotl"));
    if let Ok(local) = std::env::var("LOCALAPPDATA") {
        roots.push(PathBuf::from(local).join("Axolotl"));
    }
    if let Ok(program_files) = std::env::var("PROGRAMFILES") {
        roots.push(PathBuf::from(program_files).join("Axolotl"));
    }

    for root in roots {
        for relative in ["mc/profiles", "profiles", "mc/instances", "instances"] {
            let candidate = root.join(relative).join(raw);
            if candidate.is_dir() {
                return candidate;
            }
        }
    }
    PathBuf::from(raw)
}

pub fn find_latest_log(instance_root: &Path) -> Option<PathBuf> {
    let logs = instance_root.join("logs");
    ["latest.log", "debug.log"]
        .iter()
        .map(|name| logs.join(name))
        .find(|path| path.is_file())
}
