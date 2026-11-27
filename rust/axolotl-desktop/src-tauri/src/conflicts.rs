use std::collections::{HashMap, HashSet};
use std::fs::File;
use std::io::Read;
use std::path::{Path, PathBuf};

use serde::Serialize;
use serde_json::Value;
use zip::ZipArchive;

#[derive(Debug, Clone, Serialize)]
pub struct Conflict {
    pub kind: String,
    pub title: String,
    pub detail: String,
    pub mods: Vec<String>,
    pub evidence: String,
}

pub fn scan(root: &Path) -> Vec<Conflict> {
    let mods_dir = root.join("mods");
    let Ok(entries) = std::fs::read_dir(&mods_dir) else {
        return Vec::new();
    };
    let jars: Vec<PathBuf> = entries
        .flatten()
        .map(|entry| entry.path())
        .filter(|path| path.extension().is_some_and(|ext| ext.eq_ignore_ascii_case("jar")))
        .collect();

    let mut classes: HashMap<String, Vec<String>> = HashMap::new();
    let mut mixin_targets: HashMap<String, Vec<String>> = HashMap::new();
    let mut at_targets: HashMap<String, Vec<String>> = HashMap::new();

    for jar in jars {
        let mod_name = jar
            .file_stem()
            .map(|name| name.to_string_lossy().to_string())
            .unwrap_or_else(|| "unknown".to_string());
        let Ok(file) = File::open(&jar) else { continue };
        let Ok(mut archive) = ZipArchive::new(file) else { continue };

        let mut mixin_paths = HashSet::new();
        for index in 0..archive.len() {
            let Ok(mut entry) = archive.by_index(index) else { continue };
            let name = entry.name().to_string();
            if name.ends_with(".class") {
                classes
                    .entry(name)
                    .or_default()
                    .push(mod_name.clone());
                continue;
            }
            if name.ends_with("mixins.json") {
                let mut text = String::new();
                if entry.read_to_string(&mut text).is_ok() {
                    for path in mixin_class_paths(&text) {
                        mixin_paths.insert(path);
                    }
                }
                continue;
            }
            if name.ends_with("accesstransformer.cfg") {
                let mut text = String::new();
                if entry.read_to_string(&mut text).is_ok() {
                    for line in text.lines() {
                        let line = line.trim();
                        if line.is_empty() || line.starts_with('#') {
                            continue;
                        }
                        at_targets
                            .entry(line.to_string())
                            .or_default()
                            .push(mod_name.clone());
                    }
                }
            }
        }

        for index in 0..archive.len() {
            let Ok(mut entry) = archive.by_index(index) else { continue };
            if !mixin_paths.contains(entry.name()) {
                continue;
            }
            let mut bytes = Vec::new();
            if entry.read_to_end(&mut bytes).is_err() {
                continue;
            }
            for target in read_mixin_targets(&bytes) {
                mixin_targets
                    .entry(target)
                    .or_default()
                    .push(mod_name.clone());
            }
        }
    }

    let mut conflicts = Vec::new();
    for (target, mods) in mixin_targets {
        let unique = unique(mods);
        if unique.len() > 1 {
            conflicts.push(Conflict {
                kind: "mixin".to_string(),
                title: "Mixin 目标重叠".to_string(),
                detail: format!("{target} 被多个模组的 Mixin 同时修改。"),
                mods: unique.clone(),
                evidence: format!("target: {target}\nmods: {}", unique.join(", ")),
            });
        }
    }
    for (target, mods) in at_targets {
        let unique = unique(mods);
        if unique.len() > 1 {
            conflicts.push(Conflict {
                kind: "access-transformer".to_string(),
                title: "Access Transformer 冲突".to_string(),
                detail: format!("{target} 被多个模组同时改写访问级别。"),
                mods: unique.clone(),
                evidence: format!("line: {target}\nmods: {}", unique.join(", ")),
            });
        }
    }
    for (class, mods) in classes {
        let unique = unique(mods);
        if unique.len() > 1 && (class.starts_with("com/") || class.starts_with("net/")) {
            conflicts.push(Conflict {
                kind: "duplicate-class".to_string(),
                title: "重复类".to_string(),
                detail: format!("{class} 同时存在于多个 JAR。"),
                mods: unique.clone(),
                evidence: format!("class: {class}\nmods: {}", unique.join(", ")),
            });
        }
    }

    conflicts.sort_by(|a, b| a.kind.cmp(&b.kind).then(a.detail.cmp(&b.detail)));
    conflicts.truncate(200);
    conflicts
}

fn unique(items: Vec<String>) -> Vec<String> {
    let mut result: Vec<String> = Vec::new();
    for item in items {
        if !result.iter().any(|existing| existing.eq_ignore_ascii_case(&item)) {
            result.push(item);
        }
    }
    result
}

fn mixin_class_paths(text: &str) -> Vec<String> {
    let Ok(value) = serde_json::from_str::<Value>(text) else {
        return Vec::new();
    };
    let package = value
        .get("package")
        .and_then(Value::as_str)
        .unwrap_or("")
        .replace('.', "/");
    let mut result = Vec::new();
    for key in ["mixins", "client", "server"] {
        let Some(items) = value.get(key).and_then(Value::as_array) else {
            continue;
        };
        for item in items {
            let Some(name) = item.as_str() else { continue };
            let path = name.replace('.', "/");
            if package.is_empty() {
                result.push(format!("{path}.class"));
            } else {
                result.push(format!("{package}/{path}.class"));
            }
        }
    }
    result
}

fn read_mixin_targets(data: &[u8]) -> Vec<String> {
    if data.len() < 10 || read_u4(data, 0) != Some(0xCAFEBABE) {
        return Vec::new();
    }
    let Some(cp_count) = read_u2(data, 8) else { return Vec::new() };
    let mut offset = 10;
    let mut utf8: HashMap<u16, String> = HashMap::new();
    let mut class_utf8: HashMap<u16, u16> = HashMap::new();
    let mut index = 1;
    while index < cp_count {
        let Some(tag) = data.get(offset).copied() else { return Vec::new() };
        offset += 1;
        match tag {
            1 => {
                let Some(length) = read_u2(data, offset) else { return Vec::new() };
                offset += 2;
                let Some(bytes) = data.get(offset..offset + length as usize) else {
                    return Vec::new();
                };
                utf8.insert(index, String::from_utf8_lossy(bytes).to_string());
                offset += length as usize;
            }
            3 | 4 => offset += 4,
            5 | 6 => {
                offset += 8;
                // 8-byte constants occupy two constant-pool slots.
                index += 1;
            }
            7 => {
                let Some(name_index) = read_u2(data, offset) else { return Vec::new() };
                class_utf8.insert(index, name_index);
                offset += 2;
            }
            8 | 16 | 19 | 20 => offset += 2,
            9 | 10 | 11 | 12 | 17 | 18 => offset += 4,
            15 => offset += 3,
            _ => return Vec::new(),
        }
        index += 1;
    }

    let classes: HashMap<u16, String> = class_utf8
        .into_iter()
        .filter_map(|(index, name_index)| {
            utf8.get(&name_index).map(|name| (index, name.clone()))
        })
        .collect();

    offset += 6;
    let Some(interfaces) = read_u2(data, offset) else { return Vec::new() };
    offset += 2 + interfaces as usize * 2;
    for _ in 0..2 {
        let Some(count) = read_u2(data, offset) else { return Vec::new() };
        offset += 2;
        for _ in 0..count {
            offset += 6;
            offset = match skip_attributes(data, offset) {
                Some(next) => next,
                None => return Vec::new(),
            };
        }
    }

    let Some(attributes) = read_u2(data, offset) else { return Vec::new() };
    offset += 2;
    let mut targets = Vec::new();
    for _ in 0..attributes {
        let Some(name_index) = read_u2(data, offset) else { return Vec::new() };
        let Some(length) = read_u4(data, offset + 2) else { return Vec::new() };
        let start = offset + 6;
        let end = start + length as usize;
        if end > data.len() {
            return Vec::new();
        }
        let name = utf8.get(&name_index).map(String::as_str).unwrap_or("");
        if name == "RuntimeVisibleAnnotations" || name == "RuntimeInvisibleAnnotations" {
            read_annotations(data, start, end, &utf8, &classes, &mut targets);
        }
        offset = end;
    }
    targets.sort();
    targets.dedup_by(|a, b| a.eq_ignore_ascii_case(b));
    targets
}

fn skip_attributes(data: &[u8], offset: usize) -> Option<usize> {
    let count = read_u2(data, offset)?;
    let mut offset = offset + 2;
    for _ in 0..count {
        let length = read_u4(data, offset + 2)?;
        offset += 6 + length as usize;
    }
    Some(offset)
}

fn read_annotations(
    data: &[u8],
    start: usize,
    end: usize,
    utf8: &HashMap<u16, String>,
    classes: &HashMap<u16, String>,
    targets: &mut Vec<String>,
) {
    let Some(count) = read_u2(data, start) else { return };
    let mut offset = start + 2;
    for _ in 0..count {
        let Some(next) = read_annotation(data, offset, end, utf8, classes, targets, true) else {
            return;
        };
        offset = next;
    }
}

fn read_annotation(
    data: &[u8],
    offset: usize,
    end: usize,
    utf8: &HashMap<u16, String>,
    classes: &HashMap<u16, String>,
    targets: &mut Vec<String>,
    root: bool,
) -> Option<usize> {
    let type_index = read_u2(data, offset)?;
    let annotation_type = utf8.get(&type_index).map(String::as_str).unwrap_or("");
    let mut offset = offset + 2;
    let pairs = read_u2(data, offset)?;
    offset += 2;
    let is_mixin = root && annotation_type == "Lorg/spongepowered/asm/mixin/Mixin;";
    for _ in 0..pairs {
        let name_index = read_u2(data, offset)?;
        let name = utf8.get(&name_index).map(String::as_str).unwrap_or("");
        offset += 2;
        let collect = is_mixin && (name == "targets" || name == "value");
        offset = read_element(data, offset, end, utf8, classes, targets, collect)?;
    }
    Some(offset)
}

fn read_element(
    data: &[u8],
    offset: usize,
    end: usize,
    utf8: &HashMap<u16, String>,
    classes: &HashMap<u16, String>,
    targets: &mut Vec<String>,
    collect: bool,
) -> Option<usize> {
    if offset >= end {
        return Some(end);
    }
    let tag = *data.get(offset)? as char;
    let mut offset = offset + 1;
    match tag {
        's' => {
            let index = read_u2(data, offset)?;
            if collect {
                if let Some(value) = utf8.get(&index) {
                    add_target(value, targets);
                }
            }
            Some(offset + 2)
        }
        'c' => {
            let index = read_u2(data, offset)?;
            if collect {
                if let Some(value) = classes.get(&index) {
                    add_target(value, targets);
                }
            }
            Some(offset + 2)
        }
        'e' => Some(offset + 4),
        '@' => read_annotation(data, offset, end, utf8, classes, targets, false),
        '[' => {
            let count = read_u2(data, offset)?;
            offset += 2;
            for _ in 0..count {
                offset = read_element(data, offset, end, utf8, classes, targets, collect)?;
            }
            Some(offset)
        }
        _ => Some(offset + 2),
    }
}

fn add_target(value: &str, targets: &mut Vec<String>) {
    let mut value = value.trim();
    if value.starts_with('L') && value.ends_with(';') {
        value = &value[1..value.len() - 1];
    }
    if value.is_empty() {
        return;
    }
    let normalized = value.replace('/', ".");
    if !targets.iter().any(|target| target.eq_ignore_ascii_case(&normalized)) {
        targets.push(normalized);
    }
}

fn read_u2(data: &[u8], offset: usize) -> Option<u16> {
    let bytes = data.get(offset..offset + 2)?;
    Some(u16::from_be_bytes([bytes[0], bytes[1]]))
}

fn read_u4(data: &[u8], offset: usize) -> Option<u32> {
    let bytes = data.get(offset..offset + 4)?;
    Some(u32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_non_class_bytes() {
        assert!(read_mixin_targets(&[1, 2, 3, 4]).is_empty());
    }
}
