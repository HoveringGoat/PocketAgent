use serde::{Deserialize, Serialize};
use std::{
    env, fs, io,
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct ConversationRecord {
    pub id: String,
    pub title: String,
    pub archived: bool,
    pub pinned: bool,
    #[serde(default)]
    pub working_directory: String,
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
        let source = if path.exists() {
            path.clone()
        } else {
            backup_path(&path)
        };
        let mut store: Self = fs::read_to_string(source)
            .ok()
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or_default();
        let now = now_unix();
        for conversation in &mut store.conversations {
            if conversation.created_at_unix == 0 {
                conversation.created_at_unix = now;
            }
            if conversation.working_directory.is_empty() {
                conversation.working_directory = store.repository_path.clone();
            }
        }
        store
    }

    pub fn save(&self) -> io::Result<()> {
        save_to_path(&Self::path(), self)
    }
}

fn backup_path(path: &Path) -> PathBuf {
    path.with_extension("json.bak")
}

fn save_to_path(path: &Path, store: &DesktopStore) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let temporary = path.with_extension("json.tmp");
    fs::write(
        &temporary,
        serde_json::to_vec_pretty(store).map_err(io::Error::other)?,
    )?;
    let backup = backup_path(path);
    if backup.exists() {
        fs::remove_file(&backup)?;
    }
    if path.exists() {
        fs::rename(path, &backup)?;
    }
    match fs::rename(&temporary, path) {
        Ok(()) => {
            let _ = fs::remove_file(backup);
            Ok(())
        }
        Err(error) => {
            let _ = fs::remove_file(path);
            if backup.exists() {
                let _ = fs::rename(&backup, path);
            }
            Err(error)
        }
    }
}

impl DesktopStore {
    pub fn register_thread(&mut self, id: String, title: String) {
        if self
            .conversations
            .iter()
            .any(|conversation| conversation.id == id)
        {
            return;
        }
        self.conversations.push(ConversationRecord {
            id,
            title,
            archived: false,
            pinned: false,
            working_directory: self.repository_path.clone(),
            model: None,
            reasoning_effort: None,
            created_at_unix: now_unix(),
            last_activity_unix: None,
        });
    }

    pub fn refresh_registered_threads<I>(&mut self, threads: I)
    where
        I: IntoIterator<Item = (String, String)>,
    {
        for (id, title) in threads {
            if let Some(existing) = self
                .conversations
                .iter_mut()
                .find(|conversation| conversation.id == id)
                && existing.title == "Untitled"
                && title != "Untitled"
            {
                existing.title = title;
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
            .map(|conversation| (conversation.id.clone(), conversation.title.clone()))
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

    pub fn set_pinned(&mut self, id: &str, pinned: bool) -> bool {
        let Some(conversation) = self.conversations.iter_mut().find(|item| item.id == id) else {
            return false;
        };
        conversation.pinned = pinned;
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
                working_directory: String::new(),
                model: None,
                reasoning_effort: None,
                created_at_unix: 1,
                last_activity_unix: None,
            }],
            ..Default::default()
        };
        store.refresh_registered_threads([(
            String::from("thread-1"),
            String::from("Provider title"),
        )]);
        assert_eq!(store.conversations[0].title, "Saved title");
        assert!(store.conversations[0].archived);
        assert!(store.conversations[0].pinned);
    }

    #[test]
    fn new_conversations_use_the_saved_repository_as_their_folder() {
        let mut store = DesktopStore {
            repository_path: "C:/work/default".into(),
            ..Default::default()
        };
        store.register_thread(String::from("thread-1"), String::from("Foldered"));
        assert_eq!(store.conversations[0].working_directory, "C:/work/default");
    }

    #[test]
    fn refresh_does_not_import_provider_owned_threads() {
        let mut store = DesktopStore {
            conversations: vec![ConversationRecord {
                id: "pocket-thread".into(),
                title: "Untitled".into(),
                archived: false,
                pinned: false,
                working_directory: "C:/work/default".into(),
                model: None,
                reasoning_effort: None,
                created_at_unix: 1,
                last_activity_unix: None,
            }],
            ..Default::default()
        };
        store.refresh_registered_threads([
            ("pocket-thread".into(), "Updated title".into()),
            ("provider-owned".into(), "Should stay hidden".into()),
        ]);
        assert_eq!(store.conversations.len(), 1);
        assert_eq!(store.conversations[0].title, "Updated title");
    }

    #[test]
    fn repeated_saves_replace_the_store_and_remove_the_backup() {
        let root = std::env::temp_dir().join(format!("pocketagent-store-{}", now_unix()));
        let path = root.join("desktop.json");
        let mut store = DesktopStore {
            repository_path: "C:/first".into(),
            ..Default::default()
        };
        save_to_path(&path, &store).expect("first save should succeed");
        store.repository_path = "C:/second".into();
        save_to_path(&path, &store).expect("replacement save should succeed");
        let loaded: DesktopStore = serde_json::from_str(
            &std::fs::read_to_string(&path).expect("store should remain readable"),
        )
        .expect("store JSON should remain valid");
        assert_eq!(loaded.repository_path, "C:/second");
        assert!(!backup_path(&path).exists());
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn conversation_controls_update_local_metadata() {
        let mut store = DesktopStore {
            conversations: vec![ConversationRecord {
                id: "thread-1".into(),
                title: "Old title".into(),
                archived: false,
                pinned: false,
                working_directory: String::new(),
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
        assert_eq!(store.display_archived_sessions()[0].1, "New title");
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
                    working_directory: String::new(),
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
                    working_directory: String::new(),
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
                    working_directory: String::new(),
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
