use std::path::{Path, PathBuf};

use regex::Regex;
use serde::Serialize;
use serde_json::Value;

#[derive(Debug, Clone, Serialize)]
pub struct PerfEntry {
    pub name: String,
    pub detail: String,
    pub milliseconds: f64,
    pub percent: Option<f64>,
}

#[derive(Debug, Clone, Serialize)]
pub struct PerformanceReport {
    pub source: String,
    pub kind: String,
    pub summary: String,
    pub entries: Vec<PerfEntry>,
    pub notes: Vec<String>,
}

pub fn discover(root: &Path) -> Option<(PathBuf, String)> {
    let directories = [
        "logs/spark",
        "config/spark",
        "spark",
        "spark-reports",
        "logs/caliper",
        "caliper",
        "caliper-reports",
        "config/caliper",
        "logs",
    ];
    let mut candidates = Vec::new();
    for relative in directories {
        let directory = root.join(relative);
        let Ok(entries) = std::fs::read_dir(&directory) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let extension = path
                .extension()
                .map(|ext| ext.to_string_lossy().to_ascii_lowercase())
                .unwrap_or_default();
            if !matches!(extension.as_str(), "json" | "csv" | "txt") {
                continue;
            }
            let name = path
                .file_name()
                .map(|name| name.to_string_lossy().to_ascii_lowercase())
                .unwrap_or_default();
            if !(name.contains("spark")
                || name.contains("caliper")
                || name.contains("profiler")
                || name.contains("profile"))
            {
                continue;
            }
            let kind = if name.contains("caliper") {
                "Caliper"
            } else {
                "Spark"
            };
            let modified = entry
                .metadata()
                .and_then(|meta| meta.modified())
                .ok();
            candidates.push((path, kind.to_string(), modified));
        }
    }
    candidates.sort_by(|a, b| b.2.cmp(&a.2));
    candidates
        .into_iter()
        .next()
        .map(|(path, kind, _)| (path, kind))
}

pub fn analyze(path: &Path, kind: &str) -> Option<PerformanceReport> {
    let text = std::fs::read_to_string(path).ok()?;
    analyze_text(&text, &path.to_string_lossy(), kind)
}

pub fn analyze_text(text: &str, source: &str, kind: &str) -> Option<PerformanceReport> {
    let mut entries = parse_json(text)
        .or_else(|| parse_csv(&text))
        .unwrap_or_default();
    let mut notes = Vec::new();

    if entries.is_empty() {
        entries = parse_percent_frames(text, &mut notes);
    }
    if entries.is_empty() {
        return None;
    }
    entries.sort_by(|a, b| {
        b.milliseconds
            .partial_cmp(&a.milliseconds)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    entries.truncate(40);

    let max = entries.first().map(|entry| entry.milliseconds).unwrap_or(0.0);
    let summary = if kind.eq_ignore_ascii_case("caliper") {
        format!("Caliper：{} 个加载项，最慢 {:.1} ms。", entries.len(), max)
    } else {
        format!("Spark / 采样：{} 个热点，最高 {:.1} ms。", entries.len(), max)
    };
    if max <= 0.0 {
        notes.push("报告中没有可用的耗时数字，只能按采样占比排序。".to_string());
    }

    Some(PerformanceReport {
        source: source.to_string(),
        kind: kind.to_string(),
        summary,
        entries,
        notes,
    })
}

fn parse_json(text: &str) -> Option<Vec<PerfEntry>> {
    let value: Value = serde_json::from_str(text).ok()?;
    let mut entries = Vec::new();
    collect_json(&value, &mut entries);
    if entries.is_empty() {
        None
    } else {
        Some(entries)
    }
}

fn collect_json(value: &Value, entries: &mut Vec<PerfEntry>) {
    match value {
        Value::Array(items) => {
            for item in items {
                collect_json(item, entries);
            }
        }
        Value::Object(object) => {
            let mut name = None;
            let mut file = None;
            let mut milliseconds = 0.0;
            let mut percent = None;
            for (key, item) in object {
                let key = key.to_ascii_lowercase();
                match item {
                    Value::String(text)
                        if matches!(key.as_str(), "name" | "mod" | "modid" | "mod_name" | "classname") =>
                    {
                        name = Some(text.clone())
                    }
                    Value::String(text)
                        if matches!(key.as_str(), "file" | "filename" | "file_name" | "methodname") =>
                    {
                        file = Some(text.clone())
                    }
                    Value::Number(number)
                        if matches!(
                            key.as_str(),
                            "ms"
                                | "milliseconds"
                                | "time"
                                | "duration"
                                | "load_time"
                                | "loadtime"
                                | "self"
                        ) =>
                    {
                        milliseconds = number.as_f64().unwrap_or(0.0)
                    }
                    Value::Number(number) if matches!(key.as_str(), "percent" | "percentage") => {
                        percent = number.as_f64()
                    }
                    Value::Array(_) | Value::Object(_) => collect_json(item, entries),
                    _ => {}
                }
            }
            if let Some(name) = name {
                if milliseconds > 0.0 || percent.is_some() {
                    entries.push(PerfEntry {
                        name,
                        detail: file.unwrap_or_default(),
                        milliseconds,
                        percent,
                    });
                }
            }
        }
        _ => {}
    }
}

fn parse_csv(text: &str) -> Option<Vec<PerfEntry>> {
    let mut lines = text
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty());
    let header = lines.next()?.split(',').map(str::trim).collect::<Vec<_>>();
    let name_index = header.iter().position(|item| {
        matches!(
            item.trim_matches('"').to_ascii_lowercase().as_str(),
            "name" | "mod" | "modid" | "mod_name"
        )
    })?;
    let time_index = header.iter().position(|item| {
        matches!(
            item.trim_matches('"').to_ascii_lowercase().as_str(),
            "ms" | "milliseconds" | "time" | "duration" | "load_time" | "loadtime"
        )
    })?;
    let mut entries = Vec::new();
    for line in lines {
        let columns = line.split(',').map(str::trim).collect::<Vec<_>>();
        let name = columns.get(name_index)?.trim_matches('"');
        let milliseconds = columns
            .get(time_index)?
            .trim_matches('"')
            .replace(',', ".")
            .parse::<f64>()
            .unwrap_or(0.0);
        if !name.is_empty() && milliseconds > 0.0 {
            entries.push(PerfEntry {
                name: name.to_string(),
                detail: String::new(),
                milliseconds,
                percent: None,
            });
        }
    }
    if entries.is_empty() { None } else { Some(entries) }
}

fn parse_percent_frames(text: &str, notes: &mut Vec<String>) -> Vec<PerfEntry> {
    let frame = Regex::new(
        r"(?<pct>\d+(?:[.,]\d+)?)\s*%\s+(?<frame>(?:[A-Za-z_$][\w$]*\.)+[A-Za-z_$][\w$#<>]*)",
    )
    .ok();
    let tps = Regex::new(r"(?<value>\d+(?:[.,]\d+)?)\s*TPS").ok();
    let mspt = Regex::new(r"(?<value>\d+(?:[.,]\d+)?)\s*ms(?:/t(?:ick)?| per tick|pt)\b").ok();
    let mut entries: Vec<PerfEntry> = Vec::new();
    for line in text.lines() {
        if let Some(frame) = &frame {
            if let Some(captures) = frame.captures(line) {
                let percent = captures
                    .name("pct")
                    .and_then(|value| value.as_str().replace(',', ".").parse::<f64>().ok())
                    .unwrap_or(0.0);
                let name = captures
                    .name("frame")
                    .map(|value| value.as_str().to_string())
                    .unwrap_or_default();
                if percent >= 1.0 && !name.is_empty() {
                    entries.push(PerfEntry {
                        name,
                        detail: line.trim().to_string(),
                        milliseconds: percent,
                        percent: Some(percent),
                    });
                }
            }
        }
        if let Some(tps) = &tps {
            if let Some(value) = tps
                .captures(line)
                .and_then(|captures| captures.name("value"))
                .and_then(|value| value.as_str().replace(',', ".").parse::<f64>().ok())
            {
                if (0.0..18.0).contains(&value) {
                    notes.push(format!("日志报告 TPS={value:.2}，低于 20。"));
                }
            }
        }
        if let Some(mspt) = &mspt {
            if let Some(value) = mspt
                .captures(line)
                .and_then(|captures| captures.name("value"))
                .and_then(|value| value.as_str().replace(',', ".").parse::<f64>().ok())
            {
                if value > 50.0 {
                    notes.push(format!("日志报告 {value:.1} ms/tick，超过 50ms。"));
                }
            }
        }
    }
    entries.sort_by(|a, b| {
        b.percent
            .unwrap_or(0.0)
            .partial_cmp(&a.percent.unwrap_or(0.0))
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    entries.dedup_by(|a, b| a.name.eq_ignore_ascii_case(&b.name));
    entries
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_caliper_json() {
        let entries = parse_json(r#"[{"name":"Create","ms":321.5},{"name":"Flywheel","ms":88}]"#)
            .unwrap();
        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].milliseconds, 321.5);
    }

    #[test]
    fn parses_percent_frames() {
        let mut notes = Vec::new();
        let entries = parse_percent_frames(
            "43.2% net.minecraft.server.level.ServerLevel.tick\n2.1% com.example.Mod.tick",
            &mut notes,
        );
        assert_eq!(entries.len(), 2);
    }
}
