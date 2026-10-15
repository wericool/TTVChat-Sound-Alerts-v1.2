#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod config;
mod engine;
mod player;
mod twitch;

use config::{Config, SoundCommand};
use engine::Engine;
use player::AudioMsg;
use serde::Serialize;
use std::sync::atomic::{AtomicBool, AtomicUsize};
use std::sync::{mpsc, Arc, Mutex};
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Emitter, Manager, State, WindowEvent};

pub struct Shared {
    pub config: Mutex<Config>,
    pub config_path: std::path::PathBuf,
    pub read_only: AtomicBool,
    pub engine: Mutex<Engine>,
    pub audio: Mutex<mpsc::Sender<AudioMsg>>,
    pub pending: Arc<AtomicUsize>,
    pub connection: Mutex<Option<tauri::async_runtime::JoinHandle<()>>>,
}

pub fn log(app: &AppHandle, message: impl Into<String>) {
    let _ = app.emit("log", message.into());
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct InitialState {
    config: Config,
    config_path: String,
    read_only: bool,
    load_error: Option<String>,
}

#[tauri::command]
fn get_state(state: State<Shared>, load_error: State<LoadError>) -> InitialState {
    InitialState {
        config: state.config.lock().unwrap().clone(),
        config_path: state.config_path.display().to_string(),
        read_only: state.read_only.load(std::sync::atomic::Ordering::SeqCst),
        load_error: load_error.0.clone(),
    }
}

struct LoadError(Option<String>);

#[tauri::command]
fn save_config(state: State<Shared>, config: Config) -> Result<(), String> {
    *state.config.lock().unwrap() = config.clone();
    state.engine.lock().unwrap().reset();
    if state.read_only.load(std::sync::atomic::Ordering::SeqCst) {
        return Ok(());
    }
    config::save(&state.config_path, &config).map_err(|e| format!("Не удалось сохранить настройки: {e}"))
}

#[tauri::command]
fn set_master(state: State<Shared>, volume: f64) {
    let _ = state.audio.lock().unwrap().send(AudioMsg::Master(volume));
}

#[tauri::command]
fn play_test(state: State<Shared>, command: SoundCommand) {
    let _ = state.audio.lock().unwrap().send(AudioMsg::Enqueue(command, "Проверка".into()));
}

#[tauri::command]
fn skip(state: State<Shared>) {
    let _ = state.audio.lock().unwrap().send(AudioMsg::Skip);
}

#[tauri::command]
fn clear_queue(state: State<Shared>) {
    let _ = state.audio.lock().unwrap().send(AudioMsg::Clear);
}

#[tauri::command]
fn connect(app: AppHandle, state: State<Shared>, channel: String) -> Result<String, String> {
    let channel = config::parse_channel(&channel)?;
    let mut slot = state.connection.lock().unwrap();
    if slot.is_some() {
        return Err("Соединение уже запущено.".into());
    }
    state.engine.lock().unwrap().reset();
    *slot = Some(tauri::async_runtime::spawn(twitch::run(app, channel.clone())));
    Ok(channel)
}

#[tauri::command]
fn disconnect(app: AppHandle, state: State<Shared>) {
    if let Some(handle) = state.connection.lock().unwrap().take() {
        handle.abort();
    }
    let _ = app.emit("status", serde_json::json!({ "state": "off", "text": "Не подключён" }));
    log(&app, "Отключено.");
}

#[tauri::command]
fn scan_paths(paths: Vec<String>) -> Vec<String> {
    config::scan_audio(&paths)
}

#[tauri::command]
fn missing_files(paths: Vec<String>) -> Vec<String> {
    paths.into_iter().filter(|p| !std::path::Path::new(p).is_file()).collect()
}

#[tauri::command]
fn import_config(path: String) -> Result<Config, String> {
    config::read(std::path::Path::new(&path))
}

#[tauri::command]
fn export_config(config: Config, path: String) -> Result<(), String> {
    std::fs::write(path, serde_json::to_string_pretty(&config).map_err(|e| e.to_string())?).map_err(|e| e.to_string())
}

#[tauri::command]
fn confirm_import_recovery(state: State<Shared>) {
    // После импорта исправного файла автозапись снова разрешена; повреждённый файл сохраняем отдельно.
    use std::sync::atomic::Ordering;
    if state.read_only.swap(false, Ordering::SeqCst) && state.config_path.exists() {
        let stamp = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
        let backup = state.config_path.with_extension(format!("json.damaged-{stamp}"));
        let _ = std::fs::copy(&state.config_path, backup);
    }
}

fn show_main(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
    if let Some(tray) = app.tray_by_id("main") {
        let _ = tray.set_visible(false);
    }
}

fn main() {
    let data_dir = std::env::args()
        .skip_while(|a| a != "--data")
        .nth(1)
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| {
            std::path::PathBuf::from(std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into())).join("TTVChat-Sound-Alerts")
        });
    let config_path = data_dir.join("config.json");
    let (loaded, load_error, read_only) = match config::load(&config_path) {
        Ok(c) => (c, None, false),
        Err(e) => (Config::default(), Some(format!("Настройки не прочитаны: {e} Файл сохранён; автозапись отключена.")), true),
    };
    let pending = Arc::new(AtomicUsize::new(0));
    let (tx, rx) = mpsc::channel();
    let master = loaded.master_volume;
    let shared = Shared {
        config: Mutex::new(loaded),
        config_path,
        read_only: AtomicBool::new(read_only),
        engine: Mutex::new(Engine::default()),
        audio: Mutex::new(tx),
        pending: pending.clone(),
        connection: Mutex::new(None),
    };

    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _, _| show_main(app)))
        .plugin(tauri_plugin_dialog::init())
        .manage(shared)
        .manage(LoadError(load_error))
        .invoke_handler(tauri::generate_handler![
            get_state, save_config, set_master, play_test, skip, clear_queue, connect, disconnect,
            scan_paths, missing_files, import_config, export_config, confirm_import_recovery
        ])
        .setup(move |app| {
            player::spawn(app.handle().clone(), rx, pending, master);
            let open = MenuItem::with_id(app, "show", "Открыть", true, None::<&str>)?;
            let quit = MenuItem::with_id(app, "quit", "Выход", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&open, &quit])?;
            let mut tray = TrayIconBuilder::with_id("main")
                .tooltip("TTVChat Sound Alerts")
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "show" => show_main(app),
                    "quit" => app.exit(0),
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::DoubleClick { .. } = event {
                        show_main(tray.app_handle());
                    }
                });
            if let Some(icon) = app.default_window_icon() {
                tray = tray.icon(icon.clone());
            }
            let tray = tray.build(app)?;
            tray.set_visible(false)?;
            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::Resized(_) = event {
                let app = window.app_handle();
                let to_tray = app.state::<Shared>().config.lock().unwrap().minimize_to_tray;
                if to_tray && window.is_minimized().unwrap_or(false) {
                    let _ = window.hide();
                    if let Some(tray) = app.tray_by_id("main") {
                        let _ = tray.set_visible(true);
                    }
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("ошибка запуска приложения");
}
