use serde::{Deserialize, Serialize};
use std::{
    env, fs, io,
    path::PathBuf,
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct ConversationRecord {
    pub id: String,
    pub title: String,
    pub archived: bool,
    pub pinned: bool,
    #[serde(default)]
    pub model: Option<String>,
    #[serde(default)]
    pub reasoning_effort: Option<String>,
    #[serde(default)]
    pub created_at_unix: i64,
    #[serde(default)]
    pub last_activity_unix: Option<i64>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct ScheduledMessage {
    pub id: String,
    #[serde(default)]
    pub conversation_id: String,
    pub prompt: String,
    pub due_at_unix: i64,
    pub model: Option<String>,
    pub reasoning_effort: Option<String>,
    #[serde(default)]
    pub status: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct DesktopStore {
    pub repository_path: String,
    pub codex_executable: String,
    pub conversations: Vec<ConversationRecord>,
    #[serde(default)]
    pub schedules: Vec<ScheduledMessage>,
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
        let mut store: Self = fs::read_to_string(path)
            .ok()
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or_default();
        let now = now_unix();
        for conversation in &mut store.conversations {
            if conversation.created_at_unix == 0 {
                conversation.created_at_unix = now;
            }
        }
        store
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
                    model: None,
                    reasoning_effort: None,
                    created_at_unix: now_unix(),
                    last_activity_unix: None,
                });
            }
        }
    }

    pub fn display_sessions(&self) -> Vec<(String, String)> {
        self.display_filtered(false)
    }

    pub fn display_archived_sessions(&self) -> Vec<(String, String)> {
        self.display_filtered(true)
    }

    fn display_filtered(&self, archived: bool) -> Vec<(String, String)> {
        let mut sessions = self
            .conversations
            .iter()
            .filter(|conversation| conversation.archived == archived)
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

    pub fn set_archived(&mut self, id: &str, archived: bool) -> bool {
        let Some(conversation) = self.conversations.iter_mut().find(|item| item.id == id) else {
            return false;
        };
        conversation.archived = archived;
        true
    }

    pub fn toggle_pinned(&mut self, id: &str) -> bool {
        let Some(conversation) = self.conversations.iter_mut().find(|item| item.id == id) else {
            return false;
        };
        conversation.pinned = !conversation.pinned;
        true
    }

    pub fn rename(&mut self, id: &str, title: &str) -> bool {
        let title = title.trim();
        if title.is_empty() {
            return false;
        }
        let Some(conversation) = self.conversations.iter_mut().find(|item| item.id == id) else {
            return false;
        };
        conversation.title = title.to_owned();
        true
    }

    pub fn set_model_options(
        &mut self,
        id: &str,
        model: Option<String>,
        effort: Option<String>,
    ) -> bool {
        let Some(conversation) = self.conversations.iter_mut().find(|item| item.id == id) else {
            return false;
        };
        conversation.model = model.filter(|value| !value.trim().is_empty());
        conversation.reasoning_effort = effort.filter(|value| !value.trim().is_empty());
        true
    }

    pub fn mark_activity(&mut self, id: &str, timestamp: i64) -> bool {
        let Some(conversation) = self.conversations.iter_mut().find(|item| item.id == id) else {
            return false;
        };
        conversation.last_activity_unix = Some(timestamp);
        true
    }

    pub fn archive_stale(&mut self, cutoff_unix: i64) -> usize {
        let mut archived = 0;
        for conversation in &mut self.conversations {
            let activity = conversation
                .last_activity_unix
                .unwrap_or(conversation.created_at_unix);
            if !conversation.archived && !conversation.pinned && activity < cutoff_unix {
                conversation.archived = true;
                archived += 1;
            }
        }
        archived
    }

    pub fn add_schedule(
        &mut self,
        conversation_id: String,
        prompt: String,
        due_at_unix: i64,
        model: Option<String>,
        effort: Option<String>,
    ) -> String {
        let id = format!("schedule-{}-{}", now_unix(), self.schedules.len());
        self.schedules.push(ScheduledMessage {
            id: id.clone(),
            conversation_id,
            prompt,
            due_at_unix,
            model,
            reasoning_effort: effort,
            status: "pending".into(),
        });
        id
    }

    pub fn set_schedule_status(&mut self, id: &str, status: &str) -> bool {
        let Some(schedule) = self.schedules.iter_mut().find(|item| item.id == id) else {
            return false;
        };
        schedule.status = status.to_owned();
        true
    }
}

pub fn now_unix() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs() as i64)
        .unwrap_or_default()
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
                model: None,
                reasoning_effort: None,
                created_at_unix: 1,
                last_activity_unix: None,
            }],
            ..Default::default()
        };
        store.merge_threads([(String::from("thread-1"), String::from("Provider title"))]);
        assert_eq!(store.conversations[0].title, "Saved title");
        assert!(store.conversations[0].archived);
        assert!(store.conversations[0].pinned);
    }

    #[test]
    fn conversation_controls_update_local_metadata() {
        let mut store = DesktopStore {
            conversations: vec![ConversationRecord {
                id: "thread-1".into(),
                title: "Old title".into(),
                archived: false,
                pinned: false,
                model: None,
                reasoning_effort: None,
                created_at_unix: 1,
                last_activity_unix: None,
            }],
            ..Default::default()
        };
        assert!(store.rename("thread-1", "New title"));
        assert!(store.toggle_pinned("thread-1"));
        assert!(store.set_archived("thread-1", true));
        assert!(store.display_sessions().is_empty());
        assert_eq!(
            store.display_archived_sessions()[0].1,
            "[pinned] New title  (thread-1)"
        );
        assert!(store.set_archived("thread-1", false));
    }

    #[test]
    fn auto_archive_skips_pinned_and_recent_conversations() {
        let mut store = DesktopStore {
            conversations: vec![
                ConversationRecord {
                    id: "old".into(),
                    title: "Old".into(),
                    archived: false,
                    pinned: false,
                    model: None,
                    reasoning_effort: None,
                    created_at_unix: 10,
                    last_activity_unix: None,
                },
                ConversationRecord {
                    id: "pinned".into(),
                    title: "Pinned".into(),
                    archived: false,
                    pinned: true,
                    model: None,
                    reasoning_effort: None,
                    created_at_unix: 10,
                    last_activity_unix: None,
                },
                ConversationRecord {
                    id: "recent".into(),
                    title: "Recent".into(),
                    archived: false,
                    pinned: false,
                    model: None,
                    reasoning_effort: None,
                    created_at_unix: 10,
                    last_activity_unix: Some(99),
                },
            ],
            ..Default::default()
        };
        assert_eq!(store.archive_stale(50), 1);
        assert!(store.conversations[0].archived);
        assert!(!store.conversations[1].archived);
        assert!(!store.conversations[2].archived);
    }

    #[test]
    fn scheduled_messages_persist_pending_metadata() {
        let mut store = DesktopStore::default();
        let id = store.add_schedule(
            "thread-1".into(),
            "later".into(),
            123,
            Some("gpt".into()),
            None,
        );
        assert_eq!(store.schedules[0].id, id);
        assert_eq!(store.schedules[0].status, "pending");
        assert_eq!(store.schedules[0].due_at_unix, 123);
    }
}
