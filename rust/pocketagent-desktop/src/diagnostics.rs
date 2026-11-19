use std::{
    env,
    fs::{self, OpenOptions},
    io::Write,
    path::PathBuf,
    time::{SystemTime, UNIX_EPOCH},
};

fn log_path() -> PathBuf {
    if let Ok(local_app_data) = env::var("LOCALAPPDATA") {
        return PathBuf::from(local_app_data).join("PocketAgent-rust/desktop.log");
    }
    if let Ok(data_home) = env::var("XDG_DATA_HOME") {
        return PathBuf::from(data_home).join("PocketAgent-rust/desktop.log");
    }
    env::current_dir()
        .unwrap_or_else(|_| PathBuf::from("."))
        .join(".pocketagent-rust/desktop.log")
}

pub fn record(event: &str, detail: &str) {
    let path = log_path();
    let Some(parent) = path.parent() else { return };
    if fs::create_dir_all(parent).is_err() {
        return;
    }
    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs())
        .unwrap_or_default();
    if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(path) {
        let _ = writeln!(file, "{timestamp} {event} {detail}");
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn diagnostic_path_is_user_local() {
        assert!(
            super::log_path()
                .to_string_lossy()
                .contains("PocketAgent-rust")
        );
    }
}
