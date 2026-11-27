use regex::Regex;
use serde::Serialize;
use std::collections::HashMap;

#[derive(Debug, Clone, Serialize)]
pub struct LogIssue {
    pub rule: String,
    pub severity: String,
    pub title: String,
    pub explanation: String,
    pub culprit: String,
    pub fix: String,
    pub evidence: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct LogNoise {
    pub count: usize,
    pub signature: String,
    pub sample: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct LogDoctorReport {
    pub summary: String,
    pub total_lines: usize,
    pub noise_lines: usize,
    pub issues: Vec<LogIssue>,
    pub noise: Vec<LogNoise>,
}

pub fn analyze(text: &str) -> LogDoctorReport {
    let normalized_text = text.replace("\r\n", "\n");
    let lines: Vec<&str> = normalized_text.split('\n').collect();
    let mut issues: Vec<LogIssue> = Vec::new();
    let mut seen: HashMap<String, usize> = HashMap::new();

    for (index, line) in lines.iter().enumerate() {
        let lower = line.to_ascii_lowercase();
        let rule = if lower.contains("missing dependency")
            || (lower.contains("requires") && lower.contains("which is not installed"))
        {
            Some("missing-dependency")
        } else if lower.contains("mixin apply failed")
            || lower.contains("mixin transform")
            || lower.contains("mixin apply")
        {
            Some("mixin")
        } else if lower.contains("classnotfoundexception")
            || lower.contains("noclassdeffounderror")
        {
            Some("class-not-found")
        } else if lower.contains("nosuchmethoderror") || lower.contains("nosuchfielderror") {
            Some("no-such-method")
        } else if lower.contains("outofmemoryerror") || lower.contains("java heap space") {
            Some("out-of-memory")
        } else if lower.contains("incompatible mod set")
            || lower.contains("conflicting mods")
            || lower.contains("incompatible mods")
        {
            Some("incompatible")
        } else if lower.contains("crash report")
            || (lower.contains("description:") && lower.contains("exception"))
        {
            Some("crash")
        } else {
            None
        };

        let Some(rule) = rule else { continue };
        let key = format!("{rule}:{}", normalize(line));
        if let Some(existing) = seen.get(&key).copied() {
            issues[existing].evidence = format!("{}\n{}", issues[existing].evidence, line.trim());
            continue;
        }
        let evidence_start = index.saturating_sub(2);
        let evidence_end = (index + 3).min(lines.len());
        let evidence = lines[evidence_start..evidence_end]
            .join("\n")
            .trim()
            .to_string();
        let culprit = extract_culprit(line);
        issues.push(LogIssue {
            rule: rule.to_string(),
            severity: if rule == "crash" { "warning" } else { "error" }.to_string(),
            title: title(rule).to_string(),
            explanation: explanation(rule).to_string(),
            culprit,
            fix: fix(rule).to_string(),
            evidence,
        });
        seen.insert(key, issues.len() - 1);
    }

    let mut groups: HashMap<String, (usize, String)> = HashMap::new();
    for line in &lines {
        if line.trim().is_empty() {
            continue;
        }
        let lower = line.to_ascii_lowercase();
        if lower.contains("error")
            || lower.contains("exception")
            || lower.contains("fatal")
            || lower.contains("mixin apply")
        {
            continue;
        }
        let signature = normalize(line);
        if signature.len() < 8 {
            continue;
        }
        groups
            .entry(signature)
            .and_modify(|entry| entry.0 += 1)
            .or_insert((1, line.trim().to_string()));
    }
    let mut noise: Vec<LogNoise> = groups
        .into_iter()
        .filter(|(_, (count, _))| *count >= 3)
        .map(|(signature, (count, sample))| LogNoise {
            count,
            signature,
            sample,
        })
        .collect();
    noise.sort_by(|a, b| b.count.cmp(&a.count));
    noise.truncate(10);
    let noise_lines: usize = noise.iter().map(|item| item.count).sum();

    let summary = if issues.is_empty() {
        format!(
            "未识别到明确错误；{} 行日志中隔离了 {} 行重复低风险日志。",
            lines.len(),
            noise_lines
        )
    } else {
        format!(
            "识别到 {} 个问题，隔离 {} 行重复日志。",
            issues.len(),
            noise_lines
        )
    };

    LogDoctorReport {
        summary,
        total_lines: lines.len(),
        noise_lines,
        issues,
        noise,
    }
}

fn title(rule: &str) -> &'static str {
    match rule {
        "missing-dependency" => "缺少必需前置",
        "mixin" => "Mixin 注入失败",
        "class-not-found" => "运行时找不到类",
        "no-such-method" => "运行时找不到方法",
        "out-of-memory" => "内存不足",
        "incompatible" => "模组组合不兼容",
        "crash" => "游戏崩溃",
        _ => "日志问题",
    }
}

fn explanation(rule: &str) -> &'static str {
    match rule {
        "missing-dependency" => "某个模组声明必须依赖另一个模组，但当前实例里没有装。",
        "mixin" => "模组修改游戏代码时找不到预期目标，常见于版本不匹配或前置过期。",
        "class-not-found" => "游戏加载到一半找不到类，通常说明本体或前置版本不配套。",
        "no-such-method" => "代码调用了不存在的方法，通常是本体/前置版本比模组要求的新或旧。",
        "out-of-memory" => "分配的内存不足以加载当前整合包。",
        "incompatible" => "两个模组明确不能一起运行，或加载器检查到了冲突。",
        "crash" => "日志或崩溃报告包含终止性错误，需要结合堆栈定位。",
        _ => "日志中出现异常模式。",
    }
}

fn fix(rule: &str) -> &'static str {
    match rule {
        "missing-dependency" => "按日志中的 modId 补装前置，并检查版本范围。",
        "mixin" => "把相关模组和前置升级到支持当前 Minecraft 版本的版本。",
        "class-not-found" | "no-such-method" => "对齐本体、加载器和前置版本，不要只升级报错模组。",
        "out-of-memory" => "提高分配内存，并检查重复加载或超大整合包。",
        "incompatible" => "先移除冲突组合中的一个，再逐个回加定位。",
        "crash" => "查看崩溃报告顶部的 Description 和堆栈首行。",
        _ => "结合证据行检查对应模组版本和前置。",
    }
}

fn extract_culprit(line: &str) -> String {
    let lower = line.to_ascii_lowercase();
    for marker in ["modid=", "mod id=", "from mod ", "required by "] {
        if let Some(position) = lower.find(marker) {
            let rest = &line[position + marker.len()..];
            let token: String = rest
                .chars()
                .take_while(|ch| ch.is_ascii_alphanumeric() || matches!(ch, '_' | '-' | '.'))
                .collect();
            if !token.is_empty() {
                return token;
            }
        }
    }
    String::new()
}

fn normalize(line: &str) -> String {
    let lower = line.trim().to_ascii_lowercase();
    let lower = Regex::new(r"^\s*\[[^\]]{4,40}\]\s*")
        .unwrap()
        .replace(&lower, "")
        .to_string();
    let lower = Regex::new(r"^\[[^\]]*(?:info|warn|error|debug|trace)[^\]]*\]\s*")
        .unwrap()
        .replace(&lower, "")
        .to_string();
    Regex::new(r"(?:[0-9a-f]{8}-[0-9a-f-]{20,}|\d+|0x[0-9a-f]+)")
        .unwrap()
        .replace_all(&lower, "#")
        .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn groups_repeated_noise_and_finds_missing_dependency() {
        let mut text = String::from(
            "[12:00:00] [main/ERROR] Missing dependency: create requires flywheel\n",
        );
        for index in 0..6 {
            text.push_str(&format!(
                "[12:00:0{index}] [Render thread/INFO] Loaded texture atlas part {index}\n"
            ));
        }
        let report = analyze(&text);
        assert!(report.issues.iter().any(|issue| issue.rule == "missing-dependency"));
        assert!(report.noise_lines >= 6);
    }
}
