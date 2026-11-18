use serde_json::{Value, json};
use std::{io, path::Path, sync::Arc};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    process::{Child, ChildStdin, ChildStdout, Command},
    sync::{Notify, mpsc, watch},
};

#[derive(Clone, Debug)]
pub struct PromptRequest {
    pub id: Value,
    pub method: String,
    pub prompt: String,
    pub decisions: Vec<String>,
    pub question_ids: Vec<String>,
}

#[derive(Debug, Clone)]
pub struct SteerRequest {
    pub prompt: String,
    pub model: Option<String>,
    pub effort: Option<String>,
    pub expected_turn_id: Option<String>,
}

pub struct PromptControl<'a> {
    pub cancel: &'a mut watch::Receiver<bool>,
    pub steers: &'a mut mpsc::UnboundedReceiver<SteerRequest>,
}

#[derive(Default)]
pub struct InteractionState {
    pub pending: Option<PromptRequest>,
    pub response: Option<Value>,
}

pub type SharedInteraction = Arc<(std::sync::Mutex<InteractionState>, Notify)>;

impl InteractionState {
    pub fn shared() -> SharedInteraction {
        Arc::new((std::sync::Mutex::new(Self::default()), Notify::new()))
    }

    pub fn clear(shared: &SharedInteraction) {
        shared
            .0
            .lock()
            .expect("interaction lock should work")
            .pending = None;
        shared
            .0
            .lock()
            .expect("interaction lock should work")
            .response = None;
    }

    pub fn respond(shared: &SharedInteraction, response: Value) {
        shared
            .0
            .lock()
            .expect("interaction lock should work")
            .response = Some(response);
        shared.1.notify_one();
    }
}

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
        model: Option<&str>,
        effort: Option<&str>,
        control: PromptControl<'_>,
        interaction: SharedInteraction,
        on_request: impl Fn(PromptRequest),
    ) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
        let thread_id = self.ensure_thread().await?;
        let mut parameters = json!({
            "threadId": thread_id,
            "input": [{ "type": "text", "text": prompt, "text_elements": [] }]
        });
        if let Some(model) = model.filter(|value| !value.trim().is_empty()) {
            parameters["model"] = json!(model);
        }
        if let Some(effort) = effort.filter(|value| !value.trim().is_empty()) {
            parameters["effort"] = json!(effort);
        }
        let turn = self.request("turn/start", parameters).await?;
        let turn_id = turn
            .get("turn")
            .and_then(|turn| turn.get("id"))
            .and_then(Value::as_str)
            .map(str::to_owned);

        let mut output = String::new();
        let mut interrupted = false;
        loop {
            let message = if interrupted {
                self.read_message().await?
            } else {
                tokio::select! {
                    message = self.read_message() => message?,
                    _ = interaction.1.notified() => {
                        let response = {
                            let mut state = interaction.0.lock().expect("interaction lock should work");
                            state.response.take()
                        };
                        if let Some(response) = response {
                            let request_id = interaction
                                .0
                                .lock()
                                .expect("interaction lock should work")
                                .pending
                                .as_ref()
                                .map(|request| request.id.clone());
                            if let Some(request_id) = request_id {
                                self.write_message(json!({ "id": request_id, "result": response })).await?;
                                let mut state = interaction.0.lock().expect("interaction lock should work");
                                state.pending = None;
                            }
                        }
                        continue;
                    }
                    changed = control.cancel.changed() => {
                        if changed.is_ok() && *control.cancel.borrow() {
                            if let Some(turn_id) = turn_id.as_deref() {
                                let _ = self.notify("turn/interrupt", json!({
                                    "threadId": thread_id,
                                    "turnId": turn_id,
                                })).await;
                            }
                            InteractionState::clear(&interaction);
                            interrupted = true;
                        }
                        continue;
                    }
                    steer = control.steers.recv() => {
                        if let Some(steer) = steer {
                            self.send_steer(&thread_id, turn_id.as_deref(), &steer).await?;
                        }
                        continue;
                    }
                }
            };
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
                    if interrupted {
                        return Err(io::Error::other("Turn stopped by user").into());
                    }
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
                    if message.get("id").is_some()
                        && (method.contains("requestApproval")
                            || method == "item/tool/requestUserInput") =>
                {
                    let request = prompt_request(&message, method);
                    {
                        let mut state = interaction.0.lock().expect("interaction lock should work");
                        state.pending = Some(request.clone());
                        state.response = None;
                    }
                    on_request(request);
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

    async fn send_steer(
        &mut self,
        thread_id: &str,
        expected_turn_id: Option<&str>,
        steer: &SteerRequest,
    ) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let expected_turn_id = steer
            .expected_turn_id
            .as_deref()
            .or(expected_turn_id)
            .ok_or_else(|| io::Error::other("Codex did not return an active turn ID"))?;
        let input = Self::steer_parameters(thread_id, expected_turn_id, steer);
        self.next_id += 1;
        self.write_message(json!({
            "id": self.next_id,
            "method": "turn/steer",
            "params": input,
        }))
        .await
    }

    fn steer_parameters(thread_id: &str, expected_turn_id: &str, steer: &SteerRequest) -> Value {
        let mut input = json!({
            "threadId": thread_id,
            "expectedTurnId": expected_turn_id,
            "input": [{ "type": "text", "text": steer.prompt, "text_elements": [] }]
        });
        if let Some(model) = steer
            .model
            .as_deref()
            .filter(|value| !value.trim().is_empty())
        {
            input["model"] = json!(model);
        }
        if let Some(effort) = steer
            .effort
            .as_deref()
            .filter(|value| !value.trim().is_empty())
        {
            input["effort"] = json!(effort);
        }
        input
    }

    pub fn set_repository(&mut self, repository: impl Into<String>) {
        self.repository = repository.into();
    }

    pub async fn new_thread(
        &mut self,
        model: Option<&str>,
    ) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
        self.thread_id = None;
        let mut parameters = json!({ "cwd": self.repository });
        if let Some(model) = model.filter(|value| !value.trim().is_empty()) {
            parameters["model"] = json!(model);
        }
        let result = self.request("thread/start", parameters).await?;
        let id = result
            .get("thread")
            .and_then(|thread| thread.get("id"))
            .and_then(Value::as_str)
            .ok_or_else(|| io::Error::other("Codex returned a thread without an ID"))?
            .to_owned();
        self.thread_id = Some(id.clone());
        Ok(id)
    }

    pub async fn resume_thread(
        &mut self,
        thread_id: &str,
        model: Option<&str>,
    ) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
        let mut parameters = json!({ "threadId": thread_id, "cwd": self.repository });
        if let Some(model) = model.filter(|value| !value.trim().is_empty()) {
            parameters["model"] = json!(model);
        }
        let result = self.request("thread/resume", parameters).await?;
        self.thread_id = Some(thread_id.to_owned());
        let thread = result.get("thread").cloned().unwrap_or(Value::Null);
        if thread.get("historyMode").and_then(Value::as_str) == Some("paginated") {
            let mut turns = Vec::new();
            let mut cursor = None::<String>;
            loop {
                let mut parameters = json!({
                    "threadId": thread_id,
                    "limit": 100,
                    "sortDirection": "asc",
                    "itemsView": "full"
                });
                if let Some(cursor_value) = cursor.as_deref() {
                    parameters["cursor"] = json!(cursor_value);
                }
                let page = self.request("thread/turns/list", parameters).await?;
                if let Some(data) = page.get("data").and_then(Value::as_array) {
                    turns.extend(data.iter().cloned());
                }
                cursor = page
                    .get("nextCursor")
                    .and_then(Value::as_str)
                    .filter(|value| !value.is_empty())
                    .map(str::to_owned);
                if cursor.is_none() {
                    break;
                }
            }
            Ok(turns_text(&turns))
        } else {
            Ok(thread_text(&result))
        }
    }

    pub async fn current_thread_text(
        &mut self,
    ) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
        let thread_id = self
            .thread_id
            .clone()
            .ok_or_else(|| io::Error::other("No conversation is selected"))?;
        let result = self
            .request(
                "thread/read",
                json!({ "threadId": thread_id, "includeTurns": true }),
            )
            .await?;
        let thread = result.get("thread").cloned().unwrap_or(Value::Null);
        if thread.get("historyMode").and_then(Value::as_str) == Some("paginated") {
            let mut turns = Vec::new();
            let mut cursor = None::<String>;
            loop {
                let mut parameters = json!({
                    "threadId": thread_id,
                    "limit": 100,
                    "sortDirection": "asc",
                    "itemsView": "full"
                });
                if let Some(cursor_value) = cursor.as_deref() {
                    parameters["cursor"] = json!(cursor_value);
                }
                let page = self.request("thread/turns/list", parameters).await?;
                if let Some(data) = page.get("data").and_then(Value::as_array) {
                    turns.extend(data.iter().cloned());
                }
                cursor = page
                    .get("nextCursor")
                    .and_then(Value::as_str)
                    .filter(|value| !value.is_empty())
                    .map(str::to_owned);
                if cursor.is_none() {
                    break;
                }
            }
            Ok(turns_text(&turns))
        } else {
            Ok(thread_text(&result))
        }
    }

    pub async fn list_threads(
        &mut self,
    ) -> Result<Vec<(String, String)>, Box<dyn std::error::Error + Send + Sync>> {
        let result = self.request("thread/list", json!({ "limit": 100 })).await?;
        let threads = result
            .get("data")
            .or_else(|| result.get("threads"))
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        Ok(threads
            .into_iter()
            .filter_map(|thread| {
                let id = thread.get("id").and_then(Value::as_str)?;
                let title = thread
                    .get("title")
                    .and_then(Value::as_str)
                    .unwrap_or("Untitled");
                Some((id.to_owned(), title.to_owned()))
            })
            .collect())
    }

    pub async fn list_models(
        &mut self,
    ) -> Result<Vec<String>, Box<dyn std::error::Error + Send + Sync>> {
        let result = self
            .request(
                "model/list",
                json!({ "limit": 100, "includeHidden": false }),
            )
            .await?;
        Ok(result
            .get("data")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter_map(|model| {
                let id = model.get("model").and_then(Value::as_str)?;
                let display = model
                    .get("displayName")
                    .and_then(Value::as_str)
                    .unwrap_or(id);
                let efforts = model
                    .get("supportedReasoningEfforts")
                    .and_then(Value::as_array)
                    .map(|values| {
                        values
                            .iter()
                            .filter_map(|value| {
                                value.get("reasoningEffort").and_then(Value::as_str)
                            })
                            .collect::<Vec<_>>()
                            .join("/")
                    })
                    .filter(|value| !value.is_empty())
                    .map(|value| format!(" [{value}]"))
                    .unwrap_or_default();
                Some(format!("{id} - {display}{efforts}"))
            })
            .collect())
    }

    pub async fn usage_summary(
        &mut self,
    ) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
        let result = self.request("account/rateLimits/read", json!({})).await?;
        Ok(rate_limit_summary(&result))
    }

    async fn ensure_thread(&mut self) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
        if let Some(thread_id) = self.thread_id.clone() {
            return Ok(thread_id);
        }
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
        Ok(id)
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

fn thread_text(result: &Value) -> String {
    let Some(items) = result
        .get("thread")
        .and_then(|thread| thread.get("items"))
        .and_then(Value::as_array)
    else {
        return String::new();
    };
    items
        .iter()
        .filter_map(render_item)
        .collect::<Vec<_>>()
        .join("\n\n")
}

fn turns_text(turns: &[Value]) -> String {
    turns
        .iter()
        .filter_map(|turn| turn.get("items").and_then(Value::as_array))
        .flat_map(|items| items.iter().filter_map(render_item))
        .collect::<Vec<_>>()
        .join("\n\n")
}

fn render_item(item: &Value) -> Option<String> {
    let kind = item.get("type").and_then(Value::as_str)?;
    match kind {
        "userMessage" => item
            .get("text")
            .and_then(Value::as_str)
            .map(str::to_owned)
            .or_else(|| {
                item.get("content")
                    .and_then(Value::as_array)
                    .map(|content| {
                        content
                            .iter()
                            .filter_map(|part| {
                                (part.get("type").and_then(Value::as_str) == Some("text"))
                                    .then(|| part.get("text").and_then(Value::as_str))
                                    .flatten()
                            })
                            .collect::<Vec<_>>()
                            .join("\n")
                    })
            }),
        "agentMessage" => item.get("text").and_then(Value::as_str).map(str::to_owned),
        "commandExecution" => Some(format_item(
            "Command",
            item.get("command").and_then(Value::as_str),
            item.get("aggregatedOutput").and_then(Value::as_str),
        )),
        "fileChange" => item
            .get("changes")
            .and_then(|changes| serde_json::to_string_pretty(changes).ok())
            .map(|changes| format!("File changes\n```json\n{changes}\n```")),
        "mcpToolCall" => Some(format!(
            "Tool: {}/{}",
            item.get("server").and_then(Value::as_str).unwrap_or("MCP"),
            item.get("tool").and_then(Value::as_str).unwrap_or("call")
        )),
        "webSearch" => item
            .get("query")
            .and_then(Value::as_str)
            .map(|query| format!("Web search: {query}")),
        "imageView" => item
            .get("path")
            .and_then(Value::as_str)
            .map(|path| format!("![Image]({path})")),
        _ => None,
    }
}

fn format_item(label: &str, first: Option<&str>, second: Option<&str>) -> String {
    [
        first.map(|value| format!("{label}: {value}")),
        second.map(str::to_owned),
    ]
    .into_iter()
    .flatten()
    .collect::<Vec<_>>()
    .join("\n")
}

fn rate_limit_summary(result: &Value) -> String {
    let mut bucket = result
        .get("rateLimitsByLimitId")
        .and_then(|value| value.get("codex"))
        .unwrap_or(result);
    if bucket.get("primary").is_none() && bucket.get("secondary").is_none() {
        bucket = result.get("rateLimits").unwrap_or(result);
    }
    let windows = [bucket.get("primary"), bucket.get("secondary")]
        .into_iter()
        .flatten()
        .filter_map(|window| {
            let used = window.get("usedPercent").and_then(Value::as_i64)?;
            let minutes = window
                .get("windowDurationMins")
                .and_then(Value::as_i64)
                .unwrap_or_default();
            let remaining = (100 - used).clamp(0, 100);
            let label = if minutes >= 10_000 {
                "weekly"
            } else {
                "5-hour"
            };
            Some(format!("{label} {remaining}%"))
        })
        .collect::<Vec<_>>();
    if windows.is_empty() {
        "Usage unavailable".into()
    } else {
        windows.join("  -  ")
    }
}

fn prompt_request(message: &Value, method: &str) -> PromptRequest {
    let params = message.get("params").cloned().unwrap_or(Value::Null);
    let question_ids = params
        .get("questions")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|question| {
            question
                .get("id")
                .and_then(Value::as_str)
                .map(str::to_owned)
        })
        .collect::<Vec<_>>();
    let prompt = if method == "item/tool/requestUserInput" {
        params
            .get("questions")
            .and_then(Value::as_array)
            .map(|questions| {
                questions
                    .iter()
                    .enumerate()
                    .map(|(index, question)| {
                        let label = question
                            .get("header")
                            .or_else(|| question.get("id"))
                            .and_then(Value::as_str)
                            .unwrap_or("Question");
                        let text = question
                            .get("question")
                            .and_then(Value::as_str)
                            .unwrap_or("Provide an answer");
                        format!("{}. {}: {}", index + 1, label, text)
                    })
                    .collect::<Vec<_>>()
                    .join("\n")
            })
            .unwrap_or_else(|| "Codex is requesting input.".into())
    } else {
        params
            .get("reason")
            .or_else(|| params.get("command"))
            .and_then(Value::as_str)
            .unwrap_or("Codex is requesting approval.")
            .to_owned()
    };
    let decisions = params
        .get("availableDecisions")
        .and_then(Value::as_array)
        .map(|values| {
            values
                .iter()
                .filter_map(Value::as_str)
                .map(str::to_owned)
                .collect()
        })
        .unwrap_or_else(|| vec!["accept".into(), "decline".into(), "cancel".into()]);
    PromptRequest {
        id: message.get("id").cloned().unwrap_or(Value::Null),
        method: method.to_owned(),
        prompt,
        decisions,
        question_ids,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn steer_parameters_bind_the_observed_turn_and_preserve_options() {
        let steer = SteerRequest {
            prompt: "continue with the tests".into(),
            model: Some("gpt-test".into()),
            effort: Some("high".into()),
            expected_turn_id: None,
        };
        let parameters = CodexSession::steer_parameters("thread-1", "turn-2", &steer);
        assert_eq!(parameters["threadId"], "thread-1");
        assert_eq!(parameters["expectedTurnId"], "turn-2");
        assert_eq!(parameters["input"][0]["text"], "continue with the tests");
        assert_eq!(parameters["model"], "gpt-test");
        assert_eq!(parameters["effort"], "high");
    }

    #[test]
    fn approval_request_preserves_id_reason_and_decisions() {
        let request = prompt_request(
            &json!({
                "id": 7,
                "method": "item/commandExecution/requestApproval",
                "params": {
                    "reason": "Run the formatter",
                    "availableDecisions": ["accept", "decline"]
                }
            }),
            "item/commandExecution/requestApproval",
        );
        assert_eq!(request.id, json!(7));
        assert_eq!(request.prompt, "Run the formatter");
        assert_eq!(request.decisions, vec!["accept", "decline"]);
    }

    #[test]
    fn input_request_extracts_question_ids() {
        let request = prompt_request(
            &json!({
                "id": "req-1",
                "method": "item/tool/requestUserInput",
                "params": { "questions": [
                    { "id": "name", "header": "Name", "question": "What is your name?" },
                    { "id": "color", "question": "Favorite color?" }
                ] }
            }),
            "item/tool/requestUserInput",
        );
        assert_eq!(request.question_ids, vec!["name", "color"]);
        assert!(request.prompt.contains("What is your name?"));
    }

    #[test]
    fn thread_text_renders_content_and_tool_items() {
        let text = thread_text(&json!({
            "thread": { "items": [
                { "type": "userMessage", "content": [{ "type": "text", "text": "hello" }] },
                { "type": "commandExecution", "command": "cargo test", "aggregatedOutput": "ok" },
                { "type": "agentMessage", "text": "done" }
            ] }
        }));
        assert!(text.contains("hello"));
        assert!(text.contains("Command: cargo test"));
        assert!(text.contains("done"));
    }

    #[test]
    fn rate_limit_summary_exposes_only_remaining_windows() {
        let summary = rate_limit_summary(&json!({
            "rateLimits": {
                "primary": { "usedPercent": 20, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 5, "windowDurationMins": 10080 }
            },
            "accountId": "must-not-be-rendered"
        }));
        assert_eq!(summary, "5-hour 80%  -  weekly 95%");
        assert!(!summary.contains("account"));
    }

    #[test]
    fn paginated_turns_render_all_items_in_order() {
        let text = turns_text(&[
            json!({ "items": [{ "type": "userMessage", "text": "first" }] }),
            json!({ "items": [{ "type": "agentMessage", "text": "second" }] }),
        ]);
        assert_eq!(text, "first\n\nsecond");
    }

    #[test]
    fn image_view_items_become_markdown_images() {
        let text = thread_text(&json!({
            "thread": { "items": [
                { "type": "imageView", "path": "C:/repo/output.png" }
            ] }
        }));
        assert_eq!(text, "![Image](C:/repo/output.png)");
    }
}
