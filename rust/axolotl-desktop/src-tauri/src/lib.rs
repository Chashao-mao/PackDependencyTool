mod axolotl;
pub mod conflicts;
mod logdoctor;
mod mods;
mod performance;

use std::sync::Arc;

use axolotl::{find_latest_log, AxolotlClient, Instance};
use logdoctor::LogDoctorReport;
use serde_json::Value;
use tauri::State;
use tokio::sync::Mutex;

#[derive(Default)]
struct AppState {
    client: Mutex<Option<Arc<AxolotlClient>>>,
    local_instances: Mutex<Vec<Instance>>,
}

async fn client(state: &AppState) -> Result<Arc<AxolotlClient>, String> {
    let mut slot = state.client.lock().await;
    if let Some(client) = slot.clone() {
        return Ok(client);
    }
    let url = std::env::var("AXOLOTL_MCP_URL")
        .unwrap_or_else(|_| "http://127.0.0.1:5219/mcp".to_string());
    let token = std::env::var("AXOLOTL_MCP_TOKEN").ok();
    let client = AxolotlClient::connect(&url, token.as_deref())
        .await
        .map_err(|error| error.to_string())?;
    let client = Arc::new(client);
    *slot = Some(client.clone());
    Ok(client)
}

#[tauri::command]
fn ping() -> String {
    "pong".to_string()
}

#[tauri::command]
async fn list_instances(state: State<'_, AppState>) -> Result<Vec<Instance>, String> {
    let mut result = state.local_instances.lock().await.clone();
    let remote = match client(&state).await {
        Ok(client) => client.list_instances().await.map_err(|error| error.to_string())?,
        Err(error) => {
            if result.is_empty() {
                return Err(error);
            }
            return Ok(result);
        }
    };
    for instance in remote {
        if !result.iter().any(|item| item.resolved_path == instance.resolved_path) {
            result.push(instance);
        }
    }
    Ok(result)
}

#[tauri::command]
async fn add_local_instance(
    state: State<'_, AppState>,
    path: String,
) -> Result<Instance, String> {
    let root = std::path::PathBuf::from(path.trim());
    if !root.is_dir() {
        return Err("目录不存在".to_string());
    }
    let resolved = std::fs::canonicalize(&root)
        .map_err(|error| error.to_string())?
        .to_string_lossy()
        .trim_start_matches(r"\\?\")
        .to_string();
    let name = root
        .file_name()
        .map(|name| name.to_string_lossy().to_string())
        .unwrap_or_else(|| resolved.clone());
    let id = format!("local:{}", resolved.to_ascii_lowercase());
    let instance = Instance {
        id: id.clone(),
        name,
        path: resolved.clone(),
        resolved_path: resolved,
        game_version: "?".to_string(),
        loader: "本地目录".to_string(),
        loader_version: String::new(),
        install_stage: "本地分析".to_string(),
        last_played: None,
    };
    let mut local = state.local_instances.lock().await;
    if !local.iter().any(|item| item.id == id) {
        local.push(instance.clone());
    }
    Ok(instance)
}

#[tauri::command]
async fn launch_instance(state: State<'_, AppState>, instance_id: String) -> Result<Value, String> {
    if instance_id.starts_with("local:") {
        return Err("本地目录只用于只读分析；启动请使用 Axolotl 实例".to_string());
    }
    let client = client(&state).await?;
    client
        .launch(&instance_id)
        .await
        .map_err(|error| error.to_string())
}

#[tauri::command]
async fn scan_conflicts(
    state: State<'_, AppState>,
    instance_id: String,
) -> Result<Vec<conflicts::Conflict>, String> {
    let client = client(&state).await?;
    let instances = client.list_instances().await.map_err(|error| error.to_string())?;
    let instance = instances
        .into_iter()
        .find(|instance| instance.id == instance_id)
        .ok_or_else(|| format!("实例不存在：{instance_id}"))?;
    Ok(conflicts::scan(std::path::Path::new(&instance.resolved_path)))
}

#[tauri::command]
async fn analyze_log(
    state: State<'_, AppState>,
    instance_id: String,
) -> Result<LogDoctorReport, String> {
    let client = client(&state).await?;
    let instances = client.list_instances().await.map_err(|error| error.to_string())?;
    let instance = instances
        .into_iter()
        .find(|instance| instance.id == instance_id)
        .ok_or_else(|| format!("实例不存在：{instance_id}"))?;
    let root = std::path::PathBuf::from(&instance.resolved_path);
    let log = find_latest_log(&root).ok_or_else(|| {
        format!(
            "没有找到日志：{}",
            root.join("logs").join("latest.log").display()
        )
    })?;
    let text = std::fs::read_to_string(&log).map_err(|error| error.to_string())?;
    let index = logdoctor::ClassIndex::build(&root);
    let report = logdoctor::analyze(&text, &index);
    Ok(report)
}

#[derive(serde::Serialize)]
struct ProfilerStatus {
    spark_installed: bool,
    caliper_available: bool,
    report_found: bool,
    report_path: Option<String>,
    guidance: Vec<String>,
}

#[tauri::command]
async fn profiler_status(
    state: State<'_, AppState>,
    instance_id: String,
) -> Result<ProfilerStatus, String> {
    let client = client(&state).await?;
    let instances = client
        .list_instances()
        .await
        .map_err(|error| error.to_string())?;
    let instance = instances
        .into_iter()
        .find(|instance| instance.id == instance_id)
        .ok_or_else(|| format!("实例不存在：{instance_id}"))?;
    let root = std::path::Path::new(&instance.resolved_path);
    let mods = mods::scan_mods(root);
    let spark_installed = mods.iter().any(|item| {
        item.id.to_ascii_lowercase().contains("spark")
            || item.name.to_ascii_lowercase().contains("spark")
    });
    // Caliper is a JVM profiler agent, not a Minecraft mod. It can only be
    // started with custom JVM arguments, which Axolotl MCP does not expose.
    let caliper_available = false;
    let report = performance::discover(root);
    let mut guidance = Vec::new();
    if report.is_some() {
        guidance.push("已找到性能报告，可以直接分析。".to_string());
    } else if spark_installed {
        guidance.push("Spark 已安装：启动游戏后执行 /spark profiler start，停止后报告会出现在 config/spark。".to_string());
    } else {
        guidance.push("没有检测到 Spark；需要先安装 Spark 模组。".to_string());
    }
    if caliper_available {
        guidance.push("检测到 Caliper 报告，可直接分析。".to_string());
    } else {
        guidance.push("Caliper 不是模组，而是 JVM profiler agent；需要自定义 JVM 参数，Axolotl MCP 不允许，companion 不能替你启动。".to_string());
    }
    Ok(ProfilerStatus {
        spark_installed,
        caliper_available,
        report_found: report.is_some(),
        report_path: report.map(|(path, _)| path.to_string_lossy().to_string()),
        guidance,
    })
}

#[tauri::command]
fn analyze_performance_text(text: String, source: String, kind: String) -> Result<performance::PerformanceReport, String> {
    performance::analyze_text(&text, &source, &kind)
        .ok_or_else(|| "无法从文件内容解析 Spark/Caliper 报告".to_string())
}

#[tauri::command]
async fn analyze_performance(
    state: State<'_, AppState>,
    instance_id: String,
) -> Result<performance::PerformanceReport, String> {
    let client = client(&state).await?;
    let instances = client
        .list_instances()
        .await
        .map_err(|error| error.to_string())?;
    let instance = instances
        .into_iter()
        .find(|instance| instance.id == instance_id)
        .ok_or_else(|| format!("实例不存在：{instance_id}"))?;
    let root = std::path::Path::new(&instance.resolved_path);
    let (path, kind) = performance::discover(root).ok_or_else(|| {
        "没有找到 spark / caliper 报告；请放到 config/spark、logs/spark 或 logs 下".to_string()
    })?;
    performance::analyze(&path, &kind)
        .ok_or_else(|| format!("无法解析报告：{}", path.display()))
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(AppState::default())
        .invoke_handler(tauri::generate_handler![
            ping,
            list_instances,
            add_local_instance,
            launch_instance,
            analyze_log,
            scan_conflicts,
            analyze_performance,
            analyze_performance_text,
            profiler_status
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
