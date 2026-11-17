use serde::{Deserialize, Serialize};
use std::{env, fs, io, path::PathBuf};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct ConversationRecord {
    pub id: String,
    pub title: String,
    pub archived: bool,
    pub pinned: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct DesktopStore {
    pub repository_path: String,
    pub codex_executable: String,
    pub conversations: Vec<ConversationRecord>,
}

impl DesktopStore {
    pub fn path() -> PathBuf {
        if let Ok(local_app_data) = env::var("LOCALAPPDATA") {
            return PathBuf::from(local_app_data).join("PocketAgent-rust/desktop.json");
        }
        if let Ok(data_home) = env::var("XDG_DATA_HOME") {
            return PathBuf::from(data_home).join("PocketAgent-rust/desktop.json");
        }
        env::current_dir()
            .unwrap_or_else(|_| PathBuf::from("."))
            .join(".pocketagent-rust/desktop.json")
    }

    pub fn load() -> Self {
        let path = Self::path();
        fs::read_to_string(path)
            .ok()
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or_default()
    }

    pub fn save(&self) -> io::Result<()> {
        let path = Self::path();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let temporary = path.with_extension("json.tmp");
        fs::write(
            &temporary,
            serde_json::to_vec_pretty(self).map_err(io::Error::other)?,
        )?;
        fs::rename(temporary, path)
    }

    pub fn merge_threads<I>(&mut self, threads: I)
    where
        I: IntoIterator<Item = (String, String)>,
    {
        for (id, title) in threads {
            if let Some(existing) = self
                .conversations
                .iter_mut()
                .find(|conversation| conversation.id == id)
            {
                if existing.title == "Untitled" && title != "Untitled" {
                    existing.title = title;
                }
            } else {
                self.conversations.push(ConversationRecord {
                    id,
                    title,
                    archived: false,
                    pinned: false,
                });
            }
        }
    }

    pub fn display_sessions(&self) -> Vec<(String, String)> {
        let mut sessions = self
            .conversations
            .iter()
            .filter(|conversation| !conversation.archived)
            .map(|conversation| {
                let title = format!(
                    "{}{}  ({})",
                    if conversation.pinned { "[pinned] " } else { "" },
                    conversation.title,
                    conversation.id
                );
                (conversation.id.clone(), title)
            })
            .collect::<Vec<_>>();
        sessions.sort_by(|left, right| left.1.cmp(&right.1));
        sessions
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn merge_preserves_local_pin_and_archive_state() {
        let mut store = DesktopStore {
            conversations: vec![ConversationRecord {
                id: "thread-1".into(),
                title: "Saved title".into(),
                archived: true,
                pinned: true,
            }],
            ..Default::default()
        };
        store.merge_threads([(String::from("thread-1"), String::from("Provider title"))]);
        assert_eq!(store.conversations[0].title, "Saved title");
        assert!(store.conversations[0].archived);
        assert!(store.conversations[0].pinned);
    }
}
