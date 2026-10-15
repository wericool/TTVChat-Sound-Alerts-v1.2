use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

pub const AUDIO_EXTENSIONS: [&str; 6] = ["mp3", "wav", "m4a", "aac", "ogg", "flac"];

#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "PascalCase", default)]
pub struct SoundCommand {
    pub command: String,
    pub sound_file_path: String,
    pub cooldown_seconds: i64,
    pub volume: i64,
    pub is_personal_cooldown: bool,
    pub enabled: bool,
}

impl Default for SoundCommand {
    fn default() -> Self {
        Self { command: String::new(), sound_file_path: String::new(), cooldown_seconds: 15, volume: 80, is_personal_cooldown: false, enabled: true }
    }
}

impl SoundCommand {
    pub fn validate(&mut self) -> Result<(), String> {
        self.command = self.command.trim().to_string();
        let len = self.command.chars().count();
        if !(1..=60).contains(&len) || self.command.chars().any(char::is_whitespace) {
            return Err("Команда должна быть одним словом, до 60 символов.".into());
        }
        if !(0..=86400).contains(&self.cooldown_seconds) {
            return Err("Кулдаун: от 0 до 86400 секунд.".into());
        }
        if !(0..=100).contains(&self.volume) {
            return Err("Громкость: от 0 до 100.".into());
        }
        if self.sound_file_path.trim().is_empty() {
            return Err("Выберите звуковой файл.".into());
        }
        Ok(())
    }
}

#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "PascalCase", default)]
pub struct Config {
    pub version: i32,
    pub last_channel_name: String,
    pub master_volume: f64,
    pub minimize_to_tray: bool,
    pub command_settings: Vec<SoundCommand>,
}

impl Default for Config {
    fn default() -> Self {
        Self { version: 2, last_channel_name: String::new(), master_volume: 80.0, minimize_to_tray: true, command_settings: Vec::new() }
    }
}

pub fn read(path: &Path) -> Result<Config, String> {
    let text = fs::read_to_string(path).map_err(|e| e.to_string())?;
    let mut config: Config = serde_json::from_str(text.trim_start_matches('\u{feff}')).map_err(|e| e.to_string())?;
    if config.version > 2 {
        return Err("Эта конфигурация создана более новой версией приложения.".into());
    }
    let base = path.parent().map(Path::to_path_buf).unwrap_or_default();
    let mut names = HashSet::new();
    for cmd in &mut config.command_settings {
        cmd.validate()?;
        if !names.insert(cmd.command.to_lowercase()) {
            return Err(format!("Повтор команды {}.", cmd.command));
        }
        let file = PathBuf::from(&cmd.sound_file_path);
        cmd.sound_file_path = if file.is_absolute() { file } else { base.join(file) }.display().to_string();
    }
    config.master_volume = if config.master_volume.is_finite() { config.master_volume.clamp(0.0, 100.0) } else { 80.0 };
    Ok(config)
}

pub fn load(path: &Path) -> Result<Config, String> {
    if path.exists() { read(path) } else { Ok(Config::default()) }
}

/// Атомарная запись: временный файл, затем замена; прошлая версия остаётся в `.bak`.
pub fn save(path: &Path, config: &Config) -> io::Result<()> {
    if let Some(dir) = path.parent() {
        fs::create_dir_all(dir)?;
    }
    let temp = path.with_extension("json.tmp");
    fs::write(&temp, serde_json::to_string_pretty(config).map_err(io::Error::other)?)?;
    if path.exists() {
        let _ = fs::copy(path, path.with_extension("json.bak"));
    }
    fs::rename(&temp, path)
}

pub fn parse_channel(value: &str) -> Result<String, String> {
    let mut value = value.trim().to_string();
    if value.to_lowercase().contains("twitch.tv") {
        if !value.contains("://") {
            value = format!("https://{value}");
        }
        let lower = value.to_lowercase();
        let rest = lower.strip_prefix("https://").or_else(|| lower.strip_prefix("http://")).ok_or("Укажите ссылку twitch.tv/канал.")?;
        let (host, path) = rest.split_once('/').unwrap_or((rest, ""));
        if host != "twitch.tv" && host != "www.twitch.tv" {
            return Err("Укажите ссылку twitch.tv/канал.".into());
        }
        value = path.split(['/', '?', '#']).next().unwrap_or("").to_string();
    }
    let value = value.trim_start_matches('#').to_lowercase();
    let ok = (1..=25).contains(&value.len()) && value.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'_');
    if ok { Ok(value) } else { Err("Имя канала: латинские буквы, цифры и _, до 25 символов.".into()) }
}

fn is_audio(path: &Path) -> bool {
    path.extension().and_then(|e| e.to_str()).map(|e| AUDIO_EXTENSIONS.contains(&e.to_lowercase().as_str())).unwrap_or(false)
}

fn walk(dir: &Path, out: &mut Vec<String>, depth: usize) {
    let Ok(entries) = fs::read_dir(dir) else { return };
    let mut entries: Vec<_> = entries.flatten().map(|e| e.path()).collect();
    entries.sort_by_key(|p| p.to_string_lossy().to_lowercase());
    for path in entries {
        if path.is_dir() {
            if depth < 16 {
                walk(&path, out, depth + 1);
            }
        } else if is_audio(&path) {
            out.push(path.display().to_string());
        }
    }
}

/// Файлы и папки (с вложенными) → список аудиофайлов без повторов.
pub fn scan_audio(paths: &[String]) -> Vec<String> {
    let mut out = Vec::new();
    for p in paths {
        let path = Path::new(p);
        if path.is_dir() {
            walk(path, &mut out, 0);
        } else if path.is_file() && is_audio(path) {
            out.push(path.display().to_string());
        }
    }
    let mut seen = HashSet::new();
    out.retain(|p| seen.insert(p.to_lowercase()));
    out
}
