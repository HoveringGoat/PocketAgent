use serde_json::{Value, json};
use std::{io, path::Path};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    process::{Child, ChildStdin, ChildStdout, Command},
};

pub struct CodexSession {
    child: Child,
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
    repository: String,
    next_id: u64,
    thread_id: Option<String>,
}

impl CodexSession {
    pub async fn connect(
        executable: &str,
        repository: &str,
    ) -> Result<Self, Box<dyn std::error::Error + Send + Sync>> {
        if repository.trim().is_empty() || !Path::new(repository).is_dir() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Choose an existing repository directory.",
            )
            .into());
        }
        let mut command = if Path::new(executable)
            .extension()
            .is_some_and(|extension| extension.eq_ignore_ascii_case("ps1"))
        {
            let script = Path::new(executable);
            let basedir = script.parent().ok_or_else(|| {
                io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "Codex wrapper has no parent directory",
                )
            })?;
            let node = basedir.join("node.exe");
            let node = if node.is_file() {
                node
            } else {
                "node.exe".into()
            };
            let codex_js = basedir.join("node_modules/@openai/codex/bin/codex.js");
            if !codex_js.is_file() {
                return Err(io::Error::new(
                    io::ErrorKind::NotFound,
                    "Codex wrapper's codex.js was not found",
                )
                .into());
            }
            let mut command = Command::new(node);
            command.arg(codex_js);
            command
        } else {
            Command::new(executable)
        };
        let mut child = command
            .args(["app-server", "--listen", "stdio://"])
            .current_dir(repository)
            .stdin(std::process::Stdio::piped())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::null())
            .spawn()?;
        let stdin = child
            .stdin
            .take()
            .ok_or_else(|| io::Error::other("Codex stdin was unavailable"))?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| io::Error::other("Codex stdout was unavailable"))?;
        let mut session = Self {
            child,
            stdin,
            stdout: BufReader::new(stdout),
            repository: repository.to_owned(),
            next_id: 0,
            thread_id: None,
        };
        session.request("initialize", json!({
            "clientInfo": { "name": "pocketagent-rust-desktop", "title": "PocketAgent Rust Desktop", "version": "0.1.0" }
        })).await?;
        session.notify("initialized", json!({})).await?;
        Ok(session)
    }

    pub async fn send_prompt(
        &mut self,
        prompt: &str,
    ) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
        let thread_id = match self.thread_id.clone() {
            Some(thread_id) => thread_id,
            None => {
                let result = self
                    .request("thread/start", json!({ "cwd": self.repository }))
                    .await?;
                let id = result
                    .get("thread")
                    .and_then(|thread| thread.get("id"))
                    .and_then(Value::as_str)
                    .ok_or_else(|| io::Error::other("Codex returned a thread without an ID"))?
                    .to_owned();
                self.thread_id = Some(id.clone());
                id
            }
        };
        self.request(
            "turn/start",
            json!({
                "threadId": thread_id,
                "input": [{ "type": "text", "text": prompt, "text_elements": [] }]
            }),
        )
        .await?;

        let mut output = String::new();
        loop {
            let message = self.read_message().await?;
            match message.get("method").and_then(Value::as_str) {
                Some("item/agentMessage/delta") => {
                    if let Some(delta) = message
                        .get("params")
                        .and_then(|params| params.get("delta"))
                        .and_then(Value::as_str)
                    {
                        output.push_str(delta);
                    }
                }
                Some("turn/completed") => {
                    if output.is_empty()
                        && let Some(items) = message
                            .get("params")
                            .and_then(|params| params.get("turn"))
                            .and_then(|turn| turn.get("items"))
                            .and_then(Value::as_array)
                    {
                        for item in items {
                            if item.get("type").and_then(Value::as_str) == Some("agentMessage")
                                && let Some(text) = item.get("text").and_then(Value::as_str)
                            {
                                output.push_str(text);
                            }
                        }
                    }
                    return Ok(output);
                }
                Some(method)
                    if method.contains("requestApproval")
                        || method == "item/tool/requestUserInput" =>
                {
                    return Err(io::Error::other("Codex requested approval/input; use the existing desktop for this interaction.").into());
                }
                Some("error") => {
                    let message = message
                        .get("params")
                        .and_then(|params| params.get("error"))
                        .and_then(|error| error.get("message"))
                        .and_then(Value::as_str)
                        .unwrap_or("Codex reported an error");
                    return Err(io::Error::other(message.to_owned()).into());
                }
                _ => {}
            }
        }
    }

    async fn notify(
        &mut self,
        method: &str,
        params: Value,
    ) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        self.write_message(json!({ "method": method, "params": params }))
            .await
    }

    async fn request(
        &mut self,
        method: &str,
        params: Value,
    ) -> Result<Value, Box<dyn std::error::Error + Send + Sync>> {
        self.next_id += 1;
        let id = self.next_id;
        self.write_message(json!({ "id": id, "method": method, "params": params }))
            .await?;
        loop {
            let message = self.read_message().await?;
            if message.get("id").and_then(Value::as_u64) != Some(id) {
                continue;
            }
            if let Some(error) = message.get("error") {
                let text = error
                    .get("message")
                    .and_then(Value::as_str)
                    .unwrap_or("Codex request failed");
                return Err(io::Error::other(text.to_owned()).into());
            }
            return Ok(message.get("result").cloned().unwrap_or(Value::Null));
        }
    }

    async fn write_message(
        &mut self,
        message: Value,
    ) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let bytes = serde_json::to_vec(&message)?;
        self.stdin.write_all(&bytes).await?;
        self.stdin.write_all(b"\n").await?;
        self.stdin.flush().await?;
        Ok(())
    }

    async fn read_message(&mut self) -> Result<Value, Box<dyn std::error::Error + Send + Sync>> {
        let mut line = String::new();
        if self.stdout.read_line(&mut line).await? == 0 {
            let status = self.child.wait().await?;
            return Err(io::Error::other(format!("Codex exited with {status}")).into());
        }
        Ok(serde_json::from_str(line.trim())?)
    }
}

impl Drop for CodexSession {
    fn drop(&mut self) {
        if self.child.try_wait().ok().flatten().is_none() {
            let _ = self.child.start_kill();
        }
    }
}
