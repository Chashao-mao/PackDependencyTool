use anyhow::{anyhow, Context, Result};
use clap::{Parser, Subcommand};
use rmcp::{
    model::{CallToolRequestParams, ClientConfig, Tool},
    transport::{streamable_http_client::StreamableHttpClientTransportConfig, StreamableHttpClientTransport},
    ServiceExt,
};
use serde_json::{Map, Value};

#[derive(Parser, Debug)]
#[command(name = "axolotl-companion", version, about = "Axolotl Launcher MCP companion")]
struct Cli {
    /// Axolotl local MCP endpoint.
    #[arg(long, env = "AXOLOTL_MCP_URL", default_value = "http://127.0.0.1:5219/mcp")]
    url: String,

    /// Optional bearer token configured in Axolotl.
    #[arg(long, env = "AXOLOTL_MCP_TOKEN")]
    token: Option<String>,

    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand, Debug)]
enum Command {
    /// List tools exposed by Axolotl.
    Tools {
        #[arg(default_value = "")]
        filter: String,
    },
    /// Find and call the instance-list tool.
    Instances,
    /// Find and call the instance-launch tool.
    Launch { instance_id: String },
    /// Create a backup snapshot for one instance.
    Backup { instance_id: String },
    /// Call an arbitrary MCP tool with a JSON object.
    Call {
        name: String,
        #[arg(default_value = "{}")]
        args: String,
        /// Start a control session and inject session_id into the arguments.
        #[arg(long)]
        session: bool,
    },
}

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "axolotl_companion=info".into()),
        )
        .with_target(false)
        .init();

    let cli = Cli::parse();
    let mut config = StreamableHttpClientTransportConfig::with_uri(cli.url.clone());
    if let Some(token) = cli.token.filter(|token| !token.is_empty()) {
        config = config.auth_header(token);
    }
    let transport = StreamableHttpClientTransport::from_config(config);
    let client = ClientConfig::default()
        .serve(transport)
        .await
        .context("无法连接 Axolotl MCP；确认启动器已开启 MCP 并允许本客户端")?;

    match cli.command {
        Command::Tools { filter } => {
            let tools = client.list_all_tools().await.context("tools/list 失败")?;
            for tool in tools
                .iter()
                .filter(|tool| filter.is_empty() || tool.name.contains(&filter))
            {
                println!("{}", tool.name);
                if let Some(description) = &tool.description {
                    println!("  {description}");
                }
            }
        }
        Command::Instances => {
            let tools = client.list_all_tools().await?;
            let tool = tools
                .iter()
                .find(|tool| tool.name == "instance_list")
                .ok_or_else(|| anyhow!("没有找到 instance_list；先运行 `tools instance`"))?;
            let session = start_session(&client).await?;
            let mut args = Map::new();
            args.insert("session_id".into(), Value::String(session.clone()));
            let result = call(&client, tool, args).await?;
            print_json(&result)?;
            end_session(&client, &session).await;
        }
        Command::Launch { instance_id } => {
            let tools = client.list_all_tools().await?;
            let tool = tools
                .iter()
                .find(|tool| tool.name == "instance_launch")
                .ok_or_else(|| anyhow!("没有找到 instance_launch；先运行 `tools launch`"))?;
            let session = start_session(&client).await?;
            let key = instance_id_key(tool);
            let mut args = Map::new();
            args.insert("session_id".into(), Value::String(session.clone()));
            args.insert(key, Value::String(instance_id));
            let result = call(&client, tool, args).await?;
            print_json(&result)?;
            end_session(&client, &session).await;
        }
        Command::Backup { instance_id } => {
            let tools = client.list_all_tools().await?;
            let tool = tools
                .iter()
                .find(|tool| tool.name == "backup_create")
                .ok_or_else(|| anyhow!("没有找到 backup_create"))?;
            let session = start_session(&client).await?;
            let mut args = Map::new();
            args.insert("session_id".into(), Value::String(session.clone()));
            args.insert("instance_id".into(), Value::String(instance_id));
            let result = call(&client, tool, args).await?;
            print_json(&result)?;
            end_session(&client, &session).await;
        }
        Command::Call { name, args, session } => {
            let args: Value = serde_json::from_str(&args).context("参数必须是 JSON 对象")?;
            let mut args = args.as_object().cloned().unwrap_or_default();
            let tools = client.list_all_tools().await?;
            let tool = tools
                .iter()
                .find(|tool| tool.name == name)
                .ok_or_else(|| anyhow!("工具不存在：{name}"))?;
            let session = if session { Some(start_session(&client).await?) } else { None };
            if let Some(id) = &session {
                args.insert("session_id".into(), Value::String(id.clone()));
            }
            let result = call(&client, tool, args).await?;
            print_json(&result)?;
            if let Some(id) = session { end_session(&client, &id).await; }
        }
    }

    client.cancel().await.ok();
    Ok(())
}

async fn start_session(
    client: &rmcp::service::RunningService<rmcp::RoleClient, rmcp::model::InitializeRequestParams>,
) -> Result<String> {
    let tools = client.list_all_tools().await?;
    let tool = tools
        .iter()
        .find(|tool| tool.name == "start_session")
        .ok_or_else(|| anyhow!("Axolotl 没有暴露 start_session"))?;
    let result = call(client, tool, Map::new()).await?;
    if let Some(message) = mcp_error(&result) {
        return Err(anyhow!("Axolotl 拒绝控制会话：{message}"));
    }
    let id = result
        .pointer("/structuredContent/session_id")
        .and_then(Value::as_str)
        .map(str::to_string)
        .or_else(|| {
            result
                .get("content")
                .and_then(Value::as_array)
                .and_then(|items| items.first())
                .and_then(|item| item.get("text"))
                .and_then(Value::as_str)
                .and_then(|text| serde_json::from_str::<Value>(text).ok())
                .and_then(|value| value.get("session_id").and_then(Value::as_str).map(str::to_string))
        })
        .ok_or_else(|| anyhow!("start_session 未返回 session_id；可能被启动器拒绝"))?;
    tracing::info!(session = %id, "control session started");
    Ok(id)
}

async fn end_session(
    client: &rmcp::service::RunningService<rmcp::RoleClient, rmcp::model::InitializeRequestParams>,
    id: &str,
) {
    let tools = match client.list_all_tools().await {
        Ok(tools) => tools,
        Err(_) => return,
    };
    if let Some(tool) = tools.iter().find(|tool| tool.name == "end_session") {
        let mut args = Map::new();
        args.insert("session_id".into(), Value::String(id.to_string()));
        if let Err(error) = call(client, tool, args).await {
            tracing::warn!(%error, "end_session failed");
        } else {
            tracing::info!(session = %id, "control session ended");
        }
    }
}

async fn call(
    client: &rmcp::service::RunningService<rmcp::RoleClient, rmcp::model::InitializeRequestParams>,
    tool: &Tool,
    args: Map<String, Value>,
) -> Result<Value> {
    let params = CallToolRequestParams::new(tool.name.to_string()).with_arguments(args);
    let result = client
        .call_tool(params)
        .await
        .with_context(|| format!("调用工具失败：{}", tool.name))?;
    serde_json::to_value(result).context("序列化 MCP 结果失败")
}

fn instance_id_key(tool: &Tool) -> String {
    let properties = tool
        .input_schema
        .get("properties")
        .and_then(Value::as_object);
    if let Some(properties) = properties {
        if let Some(key) = properties
            .keys()
            .find(|key| key.to_lowercase().contains("instance") && key.to_lowercase().contains("id"))
        {
            return key.clone();
        }
        if let Some(key) = properties.keys().find(|key| key.to_lowercase().contains("instance")) {
            return key.clone();
        }
    }
    "instance_id".to_string()
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

fn print_json(value: &Value) -> Result<()> {
    println!("{}", serde_json::to_string_pretty(value)?);
    Ok(())
}
