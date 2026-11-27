use std::collections::HashSet;
use std::fs::File;
use std::io::Read;
use std::path::Path;

use serde::Serialize;
use serde_json::Value;
use zip::ZipArchive;


#[derive(Debug, Clone, Serialize)]
pub struct ModDependency {
    pub mod_id: String,
    pub mandatory: bool,
    pub version_range: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct ModInfo {
    pub id: String,
    pub name: String,
    pub version: String,
    pub jar_file: String,
    pub source: String,
    pub dependencies: Vec<ModDependency>,
}

pub fn scan_mods(root: &Path) -> Vec<ModInfo> {
    let mods_dir = root.join("mods");
    let Ok(entries) = std::fs::read_dir(&mods_dir) else {
        return Vec::new();
    };
    let mut result = Vec::new();
    for entry in entries.flatten() {
        let path = entry.path();
        if !path.extension().is_some_and(|ext| ext.eq_ignore_ascii_case("jar")) {
            continue;
        }
        let jar_file = path
            .file_name()
            .map(|name| name.to_string_lossy().to_string())
            .unwrap_or_default();
        let Ok(file) = File::open(&path) else { continue };
        let Ok(mut archive) = ZipArchive::new(file) else { continue };

        for index in 0..archive.len() {
            let Ok(mut entry) = archive.by_index(index) else { continue };
            let name = entry.name().to_string();
            let mut text = String::new();
            if entry.read_to_string(&mut text).is_err() {
                continue;
            }
            let parsed = if name.ends_with("mods.toml") {
                parse_neoforge(&text, &jar_file)
            } else if name.ends_with("fabric.mod.json") {
                parse_fabric(&text, &jar_file)
            } else if name.ends_with("quilt.mod.json") {
                parse_quilt(&text, &jar_file)
            } else {
                Vec::new()
            };
            result.extend(parsed);
        }
    }

    let mut seen = HashSet::new();
    result.retain(|item| seen.insert(item.id.to_ascii_lowercase()));
    result.sort_by(|a, b| a.name.to_ascii_lowercase().cmp(&b.name.to_ascii_lowercase()));
    result
}

fn parse_neoforge(text: &str, jar_file: &str) -> Vec<ModInfo> {
    let Ok(value) = text.parse::<toml::Value>() else {
        return Vec::new();
    };
    let dependencies = value.get("dependencies").and_then(toml::Value::as_table);
    let mut result = Vec::new();
    let Some(mods) = value.get("mods").and_then(toml::Value::as_array) else {
        return result;
    };
    for module in mods {
        let id = module
            .get("modId")
            .and_then(toml::Value::as_str)
            .unwrap_or("")
            .to_string();
        if id.is_empty() {
            continue;
        }
        let name = module
            .get("displayName")
            .and_then(toml::Value::as_str)
            .unwrap_or(&id)
            .to_string();
        let version = module
            .get("version")
            .and_then(toml::Value::as_str)
            .unwrap_or("?")
            .to_string();
        let mut dependencies_list = Vec::new();
        if let Some(dependencies) = dependencies {
            if let Some(items) = dependencies.get(&id).and_then(toml::Value::as_array) {
                for dependency in items {
                    let dep_id = dependency
                        .get("modId")
                        .and_then(toml::Value::as_str)
                        .unwrap_or("");
                    if dep_id.is_empty() {
                        continue;
                    }
                    dependencies_list.push(ModDependency {
                        mod_id: dep_id.to_string(),
                        mandatory: dependency
                            .get("mandatory")
                            .and_then(toml::Value::as_bool)
                            .unwrap_or(true),
                        version_range: dependency
                            .get("versionRange")
                            .and_then(toml::Value::as_str)
                            .unwrap_or("")
                            .to_string(),
                    });
                }
            }
        }
        result.push(ModInfo {
            id,
            name,
            version,
            jar_file: jar_file.to_string(),
            source: "neoforge".to_string(),
            dependencies: dependencies_list,
        });
    }
    result
}

fn parse_fabric(text: &str, jar_file: &str) -> Vec<ModInfo> {
    let Ok(value) = serde_json::from_str::<Value>(text) else {
        return Vec::new();
    };
    let Some(id) = value.get("id").and_then(Value::as_str) else {
        return Vec::new();
    };
    let mut dependencies = Vec::new();
    if let Some(depends) = value.get("depends").and_then(Value::as_object) {
        for dep_id in depends.keys() {
            dependencies.push(ModDependency {
                mod_id: dep_id.clone(),
                mandatory: true,
                version_range: String::new(),
            });
        }
    }
    if let Some(recommends) = value.get("recommends").and_then(Value::as_object) {
        for dep_id in recommends.keys() {
            dependencies.push(ModDependency {
                mod_id: dep_id.clone(),
                mandatory: false,
                version_range: String::new(),
            });
        }
    }
    vec![ModInfo {
        id: id.to_string(),
        name: value
            .get("name")
            .and_then(Value::as_str)
            .unwrap_or(id)
            .to_string(),
        version: value
            .get("version")
            .and_then(Value::as_str)
            .unwrap_or("?")
            .to_string(),
        jar_file: jar_file.to_string(),
        source: "fabric".to_string(),
        dependencies,
    }]
}

fn parse_quilt(text: &str, jar_file: &str) -> Vec<ModInfo> {
    let Ok(value) = serde_json::from_str::<Value>(text) else {
        return Vec::new();
    };
    let Some(loader) = value.get("quilt_loader") else {
        return Vec::new();
    };
    let Some(id) = loader.get("id").and_then(Value::as_str) else {
        return Vec::new();
    };
    let name = loader
        .get("metadata")
        .and_then(|meta| meta.get("name"))
        .and_then(Value::as_str)
        .unwrap_or(id);
    let mut dependencies = Vec::new();
    if let Some(depends) = loader.get("depends").and_then(Value::as_array) {
        for dependency in depends {
            if let Some(dep_id) = dependency.get("id").and_then(Value::as_str) {
                dependencies.push(ModDependency {
                    mod_id: dep_id.to_string(),
                    mandatory: true,
                    version_range: String::new(),
                });
            }
        }
    }
    vec![ModInfo {
        id: id.to_string(),
        name: name.to_string(),
        version: loader
            .get("version")
            .and_then(Value::as_str)
            .unwrap_or("?")
            .to_string(),
        jar_file: jar_file.to_string(),
        source: "quilt".to_string(),
        dependencies,
    }]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_fabric_metadata() {
        let mods = parse_fabric(
            r#"{"id":"create","name":"Create","version":"1.0","depends":{"flywheel":"*"}}"#,
            "create.jar",
        );
        assert_eq!(mods.len(), 1);
        assert_eq!(mods[0].dependencies[0].mod_id, "flywheel");
    }
}
