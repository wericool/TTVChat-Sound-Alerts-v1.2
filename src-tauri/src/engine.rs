use crate::config::SoundCommand;
use std::collections::{HashMap, HashSet, VecDeque};
use std::path::Path;
use std::time::{Duration, Instant};

/// Правила срабатывания: дедупликация сообщений, общий и персональный кулдаун.
#[derive(Default)]
pub struct Engine {
    cooldowns: HashMap<(String, String), Instant>,
    seen: HashSet<String>,
    order: VecDeque<String>,
}

impl Engine {
    pub fn reset(&mut self) {
        self.cooldowns.clear();
        self.seen.clear();
        self.order.clear();
    }

    pub fn matches(&mut self, user: &str, text: &str, id: Option<&str>, commands: &[SoundCommand], queue_has_room: bool) -> Option<SoundCommand> {
        if let Some(id) = id.filter(|i| !i.is_empty()) {
            if !self.seen.insert(id.to_string()) {
                return None;
            }
            self.order.push_back(id.to_string());
            if self.order.len() > 2048 {
                if let Some(old) = self.order.pop_front() {
                    self.seen.remove(&old);
                }
            }
        }
        if !queue_has_room {
            return None;
        }
        let word = text.split_whitespace().next()?.to_lowercase();
        let cmd = commands.iter().find(|c| c.enabled && c.command.to_lowercase() == word)?;
        if !Path::new(&cmd.sound_file_path).is_file() {
            return None;
        }
        let now = Instant::now();
        self.cooldowns.retain(|_, until| *until > now);
        let bucket = (word, if cmd.is_personal_cooldown { user.to_lowercase() } else { "*".to_string() });
        if self.cooldowns.contains_key(&bucket) {
            return None;
        }
        if cmd.cooldown_seconds > 0 {
            self.cooldowns.insert(bucket, now + Duration::from_secs(cmd.cooldown_seconds as u64));
        }
        Some(cmd.clone())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn cmd(name: &str, cooldown: i64, personal: bool) -> SoundCommand {
        SoundCommand {
            command: name.into(),
            sound_file_path: std::env::current_exe().unwrap().display().to_string(),
            cooldown_seconds: cooldown,
            is_personal_cooldown: personal,
            ..Default::default()
        }
    }

    #[test]
    fn cyrillic_case_and_arguments() {
        let mut e = Engine::default();
        assert!(e.matches("a", "!ПРИВЕТ всем", Some("1"), &[cmd("!привет", 0, false)], true).is_some());
        assert!(e.matches("a", "!приветствие", Some("2"), &[cmd("!привет", 0, false)], true).is_none());
    }

    #[test]
    fn cooldowns_and_dedup() {
        let mut e = Engine::default();
        let c = [cmd("!gg", 30, false), cmd("!p", 30, true)];
        assert!(e.matches("a", "!gg", Some("1"), &c, true).is_some());
        assert!(e.matches("b", "!gg", Some("2"), &c, true).is_none());
        assert!(e.matches("a", "!p", Some("3"), &c, true).is_some());
        assert!(e.matches("b", "!p", Some("4"), &c, true).is_some());
        assert!(e.matches("A", "!p", Some("5"), &c, true).is_none());
        assert!(e.matches("z", "!gg", Some("1"), &c, true).is_none());
    }

    #[test]
    fn full_queue_does_not_consume_cooldown() {
        let mut e = Engine::default();
        let c = [cmd("!gg", 30, false)];
        assert!(e.matches("a", "!gg", Some("1"), &c, false).is_none());
        assert!(e.matches("a", "!gg", Some("2"), &c, true).is_some());
    }
}
