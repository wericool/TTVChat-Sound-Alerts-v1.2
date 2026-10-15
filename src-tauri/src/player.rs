use crate::config::SoundCommand;
use rodio::{Decoder, DeviceSinkBuilder, MixerDeviceSink, Player};
use serde_json::json;
use std::collections::VecDeque;
use std::fs::File;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{mpsc, Arc};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Emitter};

pub const QUEUE_LIMIT: usize = 50;
pub const TEST_USER: &str = "Проверка";

pub enum AudioMsg {
    Enqueue(SoundCommand, String),
    Skip,
    Clear,
    Master(f64),
}

struct Current {
    command: SoundCommand,
    user: String,
    player: Player,
    deadline: Instant,
}

/// Один поток владеет аудиоустройством и очередью: звуки идут по порядку, ошибка одного не блокирует остальные.
pub fn spawn(app: AppHandle, rx: mpsc::Receiver<AudioMsg>, pending: Arc<AtomicUsize>, master: f64) {
    std::thread::spawn(move || {
        let mut device: Option<MixerDeviceSink> = None;
        let mut queue: VecDeque<(SoundCommand, String)> = VecDeque::new();
        let mut current: Option<Current> = None;
        let mut master = master;
        let mut dirty = true;
        loop {
            match rx.recv_timeout(Duration::from_millis(80)) {
                Ok(AudioMsg::Enqueue(cmd, user)) => {
                    if queue.len() >= QUEUE_LIMIT {
                        crate::log_kind(&app, "warn", "Очередь заполнена. Запрос пропущен.");
                    } else {
                        crate::log_command(&app, if user == TEST_USER { "test" } else { "chat" }, &user, &cmd.command);
                        queue.push_back((cmd, user));
                        dirty = true;
                    }
                }
                Ok(AudioMsg::Skip) => {
                    if let Some(c) = current.take() {
                        c.player.stop();
                        dirty = true;
                    }
                }
                Ok(AudioMsg::Clear) => {
                    queue.clear();
                    crate::log(&app, "Очередь очищена.");
                    dirty = true;
                }
                Ok(AudioMsg::Master(v)) => {
                    master = v;
                    if let Some(c) = &current {
                        c.player.set_volume(volume(&c.command, master));
                    }
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => return,
            }
            if let Some(c) = &current {
                if c.player.empty() || Instant::now() > c.deadline {
                    current = None;
                    dirty = true;
                }
            }
            while current.is_none() {
                let Some((cmd, user)) = queue.pop_front() else { break };
                dirty = true;
                match start(&mut device, &cmd, master) {
                    Ok(player) => current = Some(Current { command: cmd, user, player, deadline: Instant::now() + Duration::from_secs(900) }),
                    Err(e) => crate::log_kind(&app, "error", e),
                }
            }
            if dirty {
                dirty = false;
                pending.store(queue.len(), Ordering::SeqCst);
                let playing = current.as_ref().map(|c| json!({ "command": c.command.command, "user": c.user }));
                let _ = app.emit("player", json!({ "playing": playing, "pending": queue.len() }));
            }
        }
    });
}

fn volume(cmd: &SoundCommand, master: f64) -> f32 {
    (cmd.volume as f32 / 100.0) * (master as f32 / 100.0)
}

fn start(device: &mut Option<MixerDeviceSink>, cmd: &SoundCommand, master: f64) -> Result<Player, String> {
    let file = File::open(&cmd.sound_file_path).map_err(|_| format!("Файл не найден: {}", cmd.sound_file_path))?;
    let source = Decoder::try_from(file).map_err(|e| format!("Не удалось воспроизвести {}: {e}", cmd.command))?;
    if device.is_none() {
        *device = Some(DeviceSinkBuilder::open_default_sink().map_err(|e| format!("Нет устройства вывода звука: {e}"))?);
    }
    let player = Player::connect_new(device.as_ref().unwrap().mixer());
    player.set_volume(volume(cmd, master));
    player.append(source);
    Ok(player)
}
