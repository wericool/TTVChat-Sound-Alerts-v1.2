use crate::{log, player::{AudioMsg, QUEUE_LIMIT}, Shared};
use futures_util::{SinkExt, StreamExt};
use serde_json::json;
use std::sync::atomic::Ordering;
use std::time::Duration;
use tauri::{AppHandle, Emitter, Manager};
use tokio_tungstenite::tungstenite::Message;

fn status(app: &AppHandle, state: &str, text: impl Into<String>) {
    let text = text.into();
    let _ = app.emit("status", json!({ "state": state, "text": text }));
    log(app, text);
}

/// Анонимное чтение чата с переподключением и нарастающей паузой.
pub async fn run(app: AppHandle, channel: String) {
    let mut failures = 0u32;
    loop {
        let mut joined = false;
        if let Err(e) = session(&app, &channel, &mut joined).await {
            status(&app, "warn", format!("Связь потеряна: {e}"));
        }
        if joined {
            failures = 0;
        }
        let delay = (2u64 << failures.min(4)).min(30);
        failures += 1;
        status(&app, "warn", format!("Переподключение через {delay} с…"));
        tokio::time::sleep(Duration::from_secs(delay)).await;
    }
}

async fn session(app: &AppHandle, channel: &str, joined: &mut bool) -> Result<(), String> {
    status(app, "warn", "Подключение к Twitch…");
    let (socket, _) = tokio::time::timeout(Duration::from_secs(20), tokio_tungstenite::connect_async("wss://irc-ws.chat.twitch.tv:443"))
        .await
        .map_err(|_| "время ожидания истекло".to_string())?
        .map_err(|e| e.to_string())?;
    let (mut tx, mut rx) = socket.split();
    let nick = format!("justinfan{}", 100000 + (std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.subsec_nanos()).unwrap_or(0) % 99_000_000));
    for line in ["CAP REQ :twitch.tv/tags twitch.tv/commands".to_string(), "PASS SCHMOOPIIE".to_string(), format!("NICK {nick}"), format!("JOIN #{channel}")] {
        tx.send(Message::Text(format!("{line}\r\n").into())).await.map_err(|e| e.to_string())?;
    }
    let mut connected = false;
    loop {
        let wait = if connected { Duration::from_secs(360) } else { Duration::from_secs(20) };
        let message = tokio::time::timeout(wait, rx.next())
            .await
            .map_err(|_| "нет данных от Twitch".to_string())?
            .ok_or("Сервер закрыл соединение.")?
            .map_err(|e| e.to_string())?;
        let text = match message {
            Message::Text(t) => t,
            Message::Close(_) => return Err("Сервер закрыл соединение.".into()),
            _ => continue,
        };
        for line in text.as_str().split("\r\n").filter(|l| !l.is_empty()) {
            if let Some(rest) = line.strip_prefix("PING ") {
                tx.send(Message::Text(format!("PONG {rest}\r\n").into())).await.map_err(|e| e.to_string())?;
            }
            if line.contains(" RECONNECT") {
                return Err("Сервер запросил переподключение.".into());
            }
            if !connected && line.contains(" 366 ") && line.contains(&format!(" #{channel} ")) {
                connected = true;
                *joined = true;
                status(app, "ok", format!("Подключён · #{channel}"));
            }
            if !connected && line.contains(" NOTICE ") {
                return Err(line.rsplit(':').next().unwrap_or(line).to_string());
            }
            if connected {
                if let Some((user, text, id)) = parse_privmsg(line, channel) {
                    handle(app, &user, &text, id.as_deref());
                }
            }
        }
    }
}

fn handle(app: &AppHandle, user: &str, text: &str, id: Option<&str>) {
    let shared = app.state::<Shared>();
    let commands = shared.config.lock().unwrap().command_settings.clone();
    let room = shared.pending.load(Ordering::SeqCst) < QUEUE_LIMIT;
    let found = shared.engine.lock().unwrap().matches(user, text, id, &commands, room);
    if let Some(cmd) = found {
        let _ = shared.audio.lock().unwrap().send(AudioMsg::Enqueue(cmd, user.to_string()));
    }
}

pub fn parse_privmsg(line: &str, channel: &str) -> Option<(String, String, Option<String>)> {
    let mut line = line;
    let mut id = None;
    if line.starts_with('@') {
        let end = line.find(' ')?;
        id = line[1..end].split(';').find_map(|x| x.strip_prefix("id=")).map(String::from);
        line = &line[end + 1..];
    }
    let marker = format!(" PRIVMSG #{channel} :").to_ascii_lowercase();
    let pos = line.to_ascii_lowercase().find(&marker)?;
    let bang = line.find('!')?;
    if !line.starts_with(':') || bang <= 1 || bang > pos {
        return None;
    }
    Some((line[1..bang].to_string(), line[pos + marker.len()..].to_string(), id))
}

#[cfg(test)]
mod tests {
    use super::parse_privmsg;

    #[test]
    fn tags_and_colon_in_message() {
        let (user, text, id) = parse_privmsg("@id=abc;x=y :bob!bob@bob.tmi.twitch.tv PRIVMSG #chan :!hi: there", "chan").unwrap();
        assert_eq!((user.as_str(), text.as_str(), id.as_deref()), ("bob", "!hi: there", Some("abc")));
    }

    #[test]
    fn rejects_foreign_channel_and_other_lines() {
        assert!(parse_privmsg(":bob!bob@x PRIVMSG #other :!hi", "chan").is_none());
        assert!(parse_privmsg(":tmi.twitch.tv 001 justinfan1 :Welcome", "chan").is_none());
    }
}
