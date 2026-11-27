mod axolotl;
mod logdoctor;

use std::sync::Arc;

use axolotl::{find_latest_log, AxolotlClient, Instance};
use logdoctor::LogDoctorReport;
use serde_json::Value;
use tauri::State;
use tokio::sync::Mutex;

#[derive(Default)]
struct AppState {
    client: Mutex<Option<Arc<AxolotlClient>>>,
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
    let client = client(&state).await?;
    client
        .list_instances()
        .await
        .map_err(|error| error.to_string())
}

#[tauri::command]
async fn launch_instance(state: State<'_, AppState>, instance_id: String) -> Result<Value, String> {
    let client = client(&state).await?;
    client
        .launch(&instance_id)
        .await
        .map_err(|error| error.to_string())
}

#[tauri::command]
async fn backup_instance(state: State<'_, AppState>, instance_id: String) -> Result<Value, String> {
    let client = client(&state).await?;
    client
        .backup(&instance_id)
        .await
        .map_err(|error| error.to_string())
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
    Ok(logdoctor::analyze(&text))
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(AppState::default())
        .invoke_handler(tauri::generate_handler![
            ping,
            list_instances,
            launch_instance,
            backup_instance,
            analyze_log
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
