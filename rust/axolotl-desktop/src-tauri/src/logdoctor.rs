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
    pub count: usize,
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

pub fn analyze(text: &str, mods: &[String]) -> LogDoctorReport {
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
            || lower.contains("invalidmixinexception")
            || lower.contains("invalidinjectionexception")
        {
            Some("mixin")
        } else if lower.contains("classnotfoundexception")
            || lower.contains("noclassdeffounderror")
            || lower.contains("class not found")
        {
            Some("class-not-found")
        } else if lower.contains("nosuchmethoderror") || lower.contains("nosuchfielderror") {
            Some("no-such-method")
        } else if lower.contains("outofmemoryerror")
            || lower.contains("java heap space")
            || lower.contains("gl_out_of_memory")
        {
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
        } else if lower.contains("unsupported class file major version")
            || lower.contains("unsupportedclassversionerror")
        {
            Some("java-version")
        } else if lower.contains("access transformer") || lower.contains("accesstransformer") {
            Some("access-transformer")
        } else if lower.contains("failed to load mod")
            || lower.contains("modresolutionexception")
            || lower.contains("modloadingexception")
            || lower.contains("failed to create mod")
            || lower.contains("mod file") && lower.contains("missing")
        {
            Some("mod-load")
        } else if lower.contains("duplicate mod")
            || lower.contains("duplicatemod")
            || lower.contains("duplicatemodsfound")
        {
            Some("duplicate-mod")
        } else if lower.contains("trying to register duplicate")
            || lower.contains("registry") && lower.contains("duplicate")
        {
            Some("duplicate-registry")
        } else if lower.contains("exception ticking") || lower.contains("ticking entity") {
            Some("tick-crash")
        } else if lower.contains("opengl error") || lower.contains("gl error") {
            Some("opengl")
        } else {
            None
        };

        let Some(rule) = rule else { continue };
        let key = format!("{rule}:{}", exception_target(line).unwrap_or_else(|| normalize(line)));
        if let Some(existing) = seen.get(&key).copied() {
            issues[existing].count += 1;
            if issues[existing].evidence.lines().count() < 12 {
                issues[existing].evidence =
                    format!("{}\n{}", issues[existing].evidence, line.trim());
            }
            continue;
        }
        let evidence_start = index.saturating_sub(2);
        let evidence_end = (index + 3).min(lines.len());
        let evidence = lines[evidence_start..evidence_end]
            .join("\n")
            .trim()
            .to_string();
        let culprit = source_for(&evidence, line, mods);
        issues.push(LogIssue {
            rule: rule.to_string(),
            severity: if rule == "crash" { "warning" } else { "error" }.to_string(),
            title: title(rule).to_string(),
            explanation: explanation(rule).to_string(),
            culprit,
            fix: fix(rule).to_string(),
            evidence,
            count: 1,
        });
        seen.insert(key, issues.len() - 1);
    }

    let mut merged: Vec<LogIssue> = Vec::new();
    for issue in issues {
        if let Some(existing) = merged.iter_mut().find(|item| item.rule == issue.rule) {
            existing.count += issue.count;
            let mut sources: Vec<&str> = existing
                .culprit
                .split('、')
                .chain(issue.culprit.split('、'))
                .filter(|source| !source.is_empty() && *source != "未定位")
                .collect();
            sources.sort();
            sources.dedup();
            existing.culprit = if sources.is_empty() {
                "未定位".to_string()
            } else {
                sources.join("、")
            };
            if existing.evidence.lines().count() < 12 {
                existing.evidence = format!("{}\n{}", existing.evidence, issue.evidence);
            }
        } else {
            merged.push(issue);
        }
    }
    let issues = merged;

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
        let first = &issues[0];
        format!(
            "主要问题：{}（{} 处）；共 {} 类问题，已隔离 {} 行重复日志。",
            first.title,
            first.count,
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
        "java-version" => "Java 版本不匹配",
        "access-transformer" => "Access Transformer 冲突",
        "mod-load" => "模组加载失败",
        "duplicate-mod" => "重复安装模组",
        "tick-crash" => "游戏 tick 崩溃",
        "opengl" => "OpenGL 渲染错误",
        "duplicate-registry" => "注册表重复注册",
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
        "java-version" => "模组编译使用的 Java 版本比当前启动的 Java 新，类文件无法读取。",
        "access-transformer" => "多个模组修改了同一访问级别，或 AT 目标在当前版本不存在。",
        "mod-load" => "加载器无法读取某个模组文件，通常是文件损坏、重复或版本不匹配。",
        "duplicate-mod" => "同一个 modId 被安装了多份，加载器不知道该用哪一个。",
        "tick-crash" => "世界运行中某个方块/实体每 tick 抛异常，通常能定位到具体模组类。",
        "opengl" => "渲染线程报告 OpenGL 错误，可能是驱动、光影或渲染模组。",
        "duplicate-registry" => "两个模组注册了同一个 ID，通常是版本冲突或重复安装。",
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
        "java-version" => "把实例 Java 升级到模组要求的最低版本，或换用对应版本的模组。",
        "access-transformer" => "定位同时修改同一成员的模组，移除其中一个或改用兼容版本。",
        "mod-load" => "删除损坏/重复的 JAR，重新下载对应版本。",
        "duplicate-mod" => "在 mods 目录里只保留一个同 modId 的 JAR。",
        "tick-crash" => "从堆栈第一个非 Minecraft/加载器类开始查，通常就是肇事模组。",
        "opengl" => "先移除光影/渲染优化模组，再更新显卡驱动。",
        "duplicate-registry" => "从堆栈/注册 ID 找到两个来源，移除重复或改用兼容版本。",
        _ => "结合证据行检查对应模组版本和前置。",
    }
}

fn exception_target(line: &str) -> Option<String> {
    let lower = line.to_ascii_lowercase();
    for marker in [
        "classnotfoundexception:",
        "noclassdeffounderror:",
        "nosuchmethoderror:",
        "nosuchfielderror:",
    ] {
        if let Some(position) = lower.find(marker) {
            let rest = line[position + marker.len()..].trim();
            let token = rest
                .split_whitespace()
                .next()
                .unwrap_or("")
                .trim_matches(|ch| ch == '\'' || ch == '"');
            if !token.is_empty() {
                return Some(token.to_string());
            }
        }
    }
    None
}

fn source_for(evidence: &str, line: &str, mods: &[String]) -> String {
    let lower = evidence.to_ascii_lowercase();
    let mut matches: Vec<&String> = mods
        .iter()
        .filter(|name| {
            let token = name.trim();
            token.len() >= 3 && lower.contains(&token.to_ascii_lowercase())
        })
        .collect();
    matches.sort();
    matches.dedup();
    if !matches.is_empty() {
        return matches
            .into_iter()
            .take(3)
            .cloned()
            .collect::<Vec<_>>()
            .join("、");
    }

    // No filename match: show the first third-party package from the stack so
    // the user still has a concrete lead instead of a generic "unknown".
    for raw in evidence.lines() {
        let trimmed = raw.trim();
        let Some(rest) = trimmed.strip_prefix("at ") else { continue };
        let Some(class) = rest.split('(').next() else { continue };
        let package = class.rsplit_once('.').map(|(pkg, _)| pkg).unwrap_or(class);
        if !package.starts_with("net.minecraft")
            && !package.starts_with("java.")
            && !package.starts_with("jdk.")
            && !package.starts_with("org.spongepowered")
        {
            return format!("未定位模组（堆栈：{package}）");
        }
    }
    let _ = line;
    "未定位".to_string()
}

pub fn collect_mod_names(root: &std::path::Path) -> Vec<String> {
    let mut names = Vec::new();
    let Ok(entries) = std::fs::read_dir(root.join("mods")) else { return names };
    for entry in entries.flatten() {
        let file = entry.file_name().to_string_lossy().to_string();
        if !file.to_ascii_lowercase().ends_with(".jar") {
            continue;
        }
        let base = file.trim_end_matches(".jar");
        let base = base
            .split(['-', '_'])
            .next()
            .unwrap_or(base)
            .trim()
            .to_string();
        if base.len() >= 3 {
            names.push(base);
        }
        // Keep the full stem too: some mod names include separators.
        let stem = file.trim_end_matches(".jar").to_ascii_lowercase();
        if stem.len() >= 3 && !names.iter().any(|name| name.eq_ignore_ascii_case(&stem)) {
            names.push(stem);
        }
    }
    names
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
    #[ignore]
    fn analyzes_real_log() {
        let root = std::path::Path::new(r"E:\Axolotl\mc\profiles\测试");
        let text = std::fs::read_to_string(root.join("logs").join("latest.log")).unwrap_or_default();
        let report = analyze(&text, &collect_mod_names(root));
        println!("{}", report.summary);
        for issue in report.issues.iter().take(6) {
            println!("- [{}] {} | source={} | {}", issue.rule, issue.title, issue.culprit, issue.fix);
        }
    }

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
        let report = analyze(&text, &[]);
        assert!(report.issues.iter().any(|issue| issue.rule == "missing-dependency"));
        assert!(report.noise_lines >= 6);
    }
}
