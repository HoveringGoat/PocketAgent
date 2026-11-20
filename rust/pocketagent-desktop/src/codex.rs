use pocketagent_host::{HostModel, HostUsage, UsageWindow};
use serde_json::{Value, json};
use std::{collections::HashSet, io, path::Path, sync::Arc};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader, Lines},
    process::{Child, ChildStdin, ChildStdout, Command},
    sync::{Notify, mpsc, watch},
};

const INITIAL_HISTORY_TURNS: u64 = 40;

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
    pub expected_turn_id: Option<String>,
    pub from_desktop: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UsageStatus {
    pub available: bool,
    pub exhausted: bool,
    pub next_reset_at_unix: Option<i64>,
    pub summary: String,
    pub api: HostUsage,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ModelCatalog {
    pub entries: Vec<String>,
    pub default_model: Option<String>,
    pub api: Vec<HostModel>,
}

pub struct PromptControl<'a> {
    pub cancel: &'a mut watch::Receiver<bool>,
    pub steers: &'a mut mpsc::UnboundedReceiver<SteerRequest>,
    pub on_turn_started: &'a dyn Fn(Option<String>),
    pub on_steer_response: &'a dyn Fn(&str, bool),
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
    stdout: Lines<BufReader<ChildStdout>>,
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
        #[cfg(windows)]
        command.creation_flags(0x08000000);
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
            stdout: BufReader::new(stdout).lines(),
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
            "cwd": self.repository,
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
        (control.on_turn_started)(turn_id.clone());

        let mut output = String::new();
        let mut streamed_agent_items = HashSet::new();
        let mut pending_steers = std::collections::HashMap::new();
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
                                self.next_id += 1;
                                self.write_message(json!({
                                    "id": self.next_id,
                                    "method": "turn/interrupt",
                                    "params": { "threadId": thread_id, "turnId": turn_id },
                                })).await?;
                            }
                            InteractionState::clear(&interaction);
                            interrupted = true;
                        }
                        continue;
                    }
                    steer = control.steers.recv() => {
                        if let Some(steer) = steer {
                            match self.send_steer(&thread_id, turn_id.as_deref(), &steer).await {
                                Ok(id) if steer.from_desktop => { pending_steers.insert(id, steer.prompt); }
                                Err(_) if steer.from_desktop => (control.on_steer_response)(&steer.prompt, false),
                                Ok(_) => {}
                                Err(error) => return Err(error),
                            }
                        }
                        continue;
                    }
                }
            };
            if message.get("method").is_none()
                && let Some(id) = message.get("id").and_then(Value::as_u64)
                && let Some(prompt) = pending_steers.remove(&id)
            {
                (control.on_steer_response)(&prompt, message.get("result").is_some());
                continue;
            }
            match message.get("method").and_then(Value::as_str) {
                Some("item/agentMessage/delta") => {
                    if let Some(item_id) = message
                        .get("params")
                        .and_then(|params| params.get("itemId"))
                        .and_then(Value::as_str)
                    {
                        streamed_agent_items.insert(item_id.to_owned());
                    }
                    if let Some(delta) = message
                        .get("params")
                        .and_then(|params| params.get("delta"))
                        .and_then(Value::as_str)
                    {
                        output.push_str(delta);
                    }
                }
                Some("item/completed") => {
                    append_completed_agent_item(&mut output, &mut streamed_agent_items, &message);
                }
                Some("turn/completed") => {
                    if interrupted {
                        return Err(io::Error::other("Turn stopped by user").into());
                    }
                    if let Some(error) = turn_failure(&message) {
                        return Err(io::Error::other(error).into());
                    }
                    if output.is_empty()
                        && let Some(items) = message
                            .get("params")
                            .and_then(|params| params.get("turn"))
                            .and_then(|turn| turn.get("items"))
                            .and_then(Value::as_array)
                    {
                        for item in items {
                            if is_assistant_item(item)
                                && let Some(text) = response_item_text(item)
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
    ) -> Result<u64, Box<dyn std::error::Error + Send + Sync>> {
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
        .await?;
        Ok(self.next_id)
    }

    fn steer_parameters(thread_id: &str, expected_turn_id: &str, steer: &SteerRequest) -> Value {
        // A steer augments the already-running turn. Model and reasoning
        // settings belong to that turn and must not be changed mid-flight.
        json!({
            "threadId": thread_id,
            "expectedTurnId": expected_turn_id,
            "input": [{ "type": "text", "text": steer.prompt, "text_elements": [] }]
        })
    }

    pub fn set_repository(&mut self, repository: impl Into<String>) {
        self.repository = repository.into();
    }

    pub fn clear_thread(&mut self) {
        self.thread_id = None;
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
        let history = self.thread_history(thread_id, result).await?;
        self.thread_id = Some(thread_id.to_owned());
        Ok(history)
    }

    /// Read a thread without acquiring the provider's active-writer lease.
    ///
    /// A second desktop or mobile client can use this path to mirror a
    /// conversation while another client owns the writable Codex session.
    pub async fn read_thread(
        &mut self,
        thread_id: &str,
    ) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
        let result = self
            .request(
                "thread/read",
                json!({ "threadId": thread_id, "includeTurns": true }),
            )
            .await?;
        self.thread_history(thread_id, result).await
    }

    async fn thread_history(
        &mut self,
        thread_id: &str,
        result: Value,
    ) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
        let thread = result.get("thread").cloned().unwrap_or(Value::Null);
        if thread.get("historyMode").and_then(Value::as_str) == Some("paginated") {
            let page = self
                .request(
                    "thread/turns/list",
                    json!({
                        "threadId": thread_id,
                        "limit": INITIAL_HISTORY_TURNS,
                        "sortDirection": "desc",
                        "itemsView": "full"
                    }),
                )
                .await?;
            let turns = page
                .get("data")
                .and_then(Value::as_array)
                .map(|data| recent_turns_in_display_order(data, INITIAL_HISTORY_TURNS as usize))
                .unwrap_or_default();
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
        self.thread_history(&thread_id, result).await
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
    ) -> Result<ModelCatalog, Box<dyn std::error::Error + Send + Sync>> {
        let mut catalog = ModelCatalog::default();
        let mut cursor = None::<String>;
        let mut cursors = HashSet::new();
        loop {
            let params = match cursor.as_deref() {
                Some(cursor) => json!({
                    "cursor": cursor,
                    "limit": 100,
                    "includeHidden": false
                }),
                None => json!({ "limit": 100, "includeHidden": false }),
            };
            let result = self.request("model/list", params).await?;
            Self::merge_model_catalog(&mut catalog, Self::model_catalog(&result));
            let next = result
                .get("nextCursor")
                .and_then(Value::as_str)
                .filter(|value| !value.trim().is_empty())
                .map(str::to_owned);
            let Some(next) = next else { break };
            if !cursors.insert(next.clone()) {
                return Err(
                    io::Error::other("Codex returned a repeated model catalog cursor").into(),
                );
            }
            cursor = Some(next);
        }
        if catalog.default_model.is_none() {
            catalog.default_model = catalog.api.first().map(|model| model.model.clone());
        }
        Ok(catalog)
    }

    fn merge_model_catalog(catalog: &mut ModelCatalog, page: ModelCatalog) {
        for (entry, model) in page.entries.into_iter().zip(page.api) {
            if catalog
                .api
                .iter()
                .any(|existing| existing.model == model.model)
            {
                continue;
            }
            if model.is_default && catalog.default_model.is_none() {
                catalog.default_model = Some(model.model.clone());
            }
            catalog.entries.push(entry);
            catalog.api.push(model);
        }
    }

    fn model_catalog(result: &Value) -> ModelCatalog {
        let mut catalog = ModelCatalog::default();
        for model in result
            .get("data")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
        {
            let Some(id) = model.get("model").and_then(Value::as_str) else {
                continue;
            };
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
                        .filter_map(|value| value.get("reasoningEffort").and_then(Value::as_str))
                        .collect::<Vec<_>>()
                        .join("/")
                })
                .filter(|value| !value.is_empty())
                .map(|value| format!(" [{value}]"))
                .unwrap_or_default();
            catalog.entries.push(format!("{id} - {display}{efforts}"));
            catalog.api.push(HostModel {
                model: id.to_owned(),
                display_name: display.to_owned(),
                is_default: model
                    .get("isDefault")
                    .and_then(Value::as_bool)
                    .unwrap_or(false),
                default_reasoning_effort: model
                    .get("defaultReasoningEffort")
                    .and_then(Value::as_str)
                    .map(str::to_owned),
                supported_reasoning_efforts: model
                    .get("supportedReasoningEfforts")
                    .and_then(Value::as_array)
                    .into_iter()
                    .flatten()
                    .filter_map(|v| {
                        v.get("reasoningEffort")
                            .and_then(Value::as_str)
                            .map(str::to_owned)
                    })
                    .collect(),
            });
            if model.get("isDefault").and_then(Value::as_bool) == Some(true) {
                catalog.default_model = Some(id.to_owned());
            }
        }
        catalog
    }

    pub async fn usage_status(
        &mut self,
    ) -> Result<UsageStatus, Box<dyn std::error::Error + Send + Sync>> {
        let result = self.request("account/rateLimits/read", json!({})).await?;
        Ok(rate_limit_status(&result))
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
        let Some(line) = self.stdout.next_line().await? else {
            let status = self.child.wait().await?;
            return Err(io::Error::other(format!("Codex exited with {status}")).into());
        };
        Ok(serde_json::from_str(&line)?)
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
    let Some(thread) = result.get("thread") else {
        return String::new();
    };
    if let Some(items) = thread.get("items").and_then(Value::as_array) {
        return items
            .iter()
            .filter_map(render_item)
            .collect::<Vec<_>>()
            .join("\n\n");
    }
    thread
        .get("turns")
        .and_then(Value::as_array)
        .map(|turns| turns_text(turns))
        .unwrap_or_default()
}

fn append_completed_agent_item(
    output: &mut String,
    streamed_agent_items: &mut HashSet<String>,
    message: &Value,
) {
    let Some(item) = message.get("params").and_then(|params| params.get("item")) else {
        return;
    };
    if !is_assistant_item(item) {
        return;
    }
    let item_id = item.get("id").and_then(Value::as_str);
    if item_id.is_some_and(|id| streamed_agent_items.contains(id)) {
        return;
    }
    if let Some(text) = response_item_text(item) {
        output.push_str(text);
    }
    if let Some(item_id) = item_id {
        streamed_agent_items.insert(item_id.to_owned());
    }
}

fn turn_failure(message: &Value) -> Option<String> {
    let turn = message.get("params")?.get("turn")?;
    let status = turn.get("status").and_then(Value::as_str);
    let error = turn
        .get("error")
        .and_then(|value| value.get("message"))
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty());
    if status.is_some_and(|value| !matches!(value, "completed" | "success")) || error.is_some() {
        Some(
            error
                .unwrap_or("Codex reported that the turn failed")
                .to_owned(),
        )
    } else {
        None
    }
}

fn turns_text(turns: &[Value]) -> String {
    turns
        .iter()
        .filter_map(|turn| turn.get("items").and_then(Value::as_array))
        .flat_map(|items| items.iter().filter_map(render_item))
        .collect::<Vec<_>>()
        .join("\n\n")
}

fn recent_turns_in_display_order(data: &[Value], limit: usize) -> Vec<Value> {
    data.iter().take(limit).cloned().rev().collect()
}

fn render_item(item: &Value) -> Option<String> {
    let kind = item.get("type").and_then(Value::as_str)?;
    match kind {
        "userMessage" => item
            .get("text")
            .and_then(Value::as_str)
            .map(|text| transcript_item("user", text))
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
                    .map(|text| transcript_item("user", &text))
            }),
        "agentMessage" => response_item_text(item).map(|text| transcript_item("assistant", text)),
        // Newer app-server responses use Responses API message items instead
        // of the legacy agentMessage shape. Keep both wire formats readable.
        "message" => {
            let role = item.get("role").and_then(Value::as_str)?;
            response_item_text(item).map(|text| {
                transcript_item(if role == "user" { "user" } else { "assistant" }, text)
            })
        }
        "commandExecution" => {
            let command = item
                .get("command")
                .and_then(Value::as_str)
                .unwrap_or("Command execution");
            let output = item
                .get("aggregatedOutput")
                .or_else(|| item.get("output"))
                .and_then(Value::as_str)
                .unwrap_or("");
            let detail = if output.trim().is_empty() {
                command.to_owned()
            } else {
                format!("{command}\n\n{output}")
            };
            let summary = command
                .lines()
                .next()
                .unwrap_or("Command execution")
                .chars()
                .take(96)
                .collect::<String>();
            Some(activity_item(&format!("Command: {summary}"), &detail))
        }
        "fileChange" => Some(activity_item(
            "File change",
            &serde_json::to_string_pretty(item).unwrap_or_else(|_| "File change".into()),
        )),
        "mcpToolCall" => Some(activity_item(
            item.get("toolName")
                .or_else(|| item.get("name"))
                .and_then(Value::as_str)
                .unwrap_or("MCP tool call"),
            &serde_json::to_string_pretty(item).unwrap_or_else(|_| "MCP tool call".into()),
        )),
        "webSearch" => Some(activity_item(
            "Web search",
            &serde_json::to_string_pretty(item).unwrap_or_else(|_| "Web search".into()),
        )),
        "imageView" => image_view_source(item)
            .map(|source| transcript_item("assistant", &format!("![Image]({source})"))),
        _ => None,
    }
}

fn is_assistant_item(item: &Value) -> bool {
    match item.get("type").and_then(Value::as_str) {
        Some("agentMessage") => true,
        Some("message") => item.get("role").and_then(Value::as_str) == Some("assistant"),
        _ => false,
    }
}

fn response_item_text(item: &Value) -> Option<&str> {
    item.get("text").and_then(Value::as_str).or_else(|| {
        item.get("content")
            .and_then(Value::as_array)
            .and_then(|content| {
                content.iter().find_map(|part| {
                    let kind = part.get("type").and_then(Value::as_str);
                    (matches!(
                        kind,
                        Some("text") | Some("output_text") | Some("input_text")
                    ))
                    .then(|| part.get("text").and_then(Value::as_str))
                    .flatten()
                })
            })
    })
}

fn transcript_item(role: &str, text: &str) -> String {
    format!("\u{001e}{role}\n{text}")
}

fn activity_item(summary: &str, text: &str) -> String {
    format!("\u{001e}activity|{summary}\n{text}")
}

fn image_view_source(item: &Value) -> Option<String> {
    ["path", "url", "uri", "source", "data"]
        .into_iter()
        .find_map(|field| value_as_image_source(item.get(field)))
}

fn value_as_image_source(value: Option<&Value>) -> Option<String> {
    match value? {
        Value::String(value) if !value.trim().is_empty() => Some(value.clone()),
        Value::Object(object) => ["path", "url", "uri", "data"]
            .into_iter()
            .find_map(|field| value_as_image_source(object.get(field))),
        _ => None,
    }
}

fn rate_limit_status(result: &Value) -> UsageStatus {
    let mut bucket = result
        .get("rateLimitsByLimitId")
        .and_then(|value| value.get("codex"))
        .unwrap_or(result);
    if bucket.get("primary").is_none() && bucket.get("secondary").is_none() {
        bucket = result.get("rateLimits").unwrap_or(result);
    }
    let mut exhausted = false;
    let mut api = HostUsage::default();
    let mut reset_times = Vec::new();
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
            let window = UsageWindow {
                remaining_percent: remaining,
                window_minutes: minutes,
                resets_at: window
                    .get("resetsAt")
                    .and_then(Value::as_i64)
                    .filter(|seconds| *seconds > 0 && *seconds < 253402300799)
                    .and_then(|seconds| chrono::DateTime::<chrono::Utc>::from_timestamp(seconds, 0))
                    .map(|time| time.to_rfc3339()),
            };
            match minutes {
                300 => api.five_hour = Some(window.clone()),
                10080 => api.weekly = Some(window.clone()),
                _ => {}
            }
            if remaining == 0 {
                exhausted = true;
                if let Some(reset) = window
                    .resets_at
                    .as_ref()
                    .and_then(|text| chrono::DateTime::parse_from_rfc3339(text).ok())
                    .map(|time| time.timestamp())
                    && reset > chrono::Utc::now().timestamp()
                {
                    reset_times.push(reset);
                }
            }
            let label = if minutes >= 10_000 {
                "weekly"
            } else {
                "5-hour"
            };
            Some(format!("{label} {remaining}%"))
        })
        .collect::<Vec<_>>();
    if windows.is_empty() {
        UsageStatus {
            summary: "Usage unavailable".into(),
            ..UsageStatus::default()
        }
    } else {
        let next_reset_at_unix = reset_times.into_iter().max();
        api.available = true;
        api.exhausted = exhausted;
        api.next_reset_at = next_reset_at_unix
            .and_then(|seconds| chrono::DateTime::<chrono::Utc>::from_timestamp(seconds, 0))
            .map(|time| time.to_rfc3339());
        UsageStatus {
            available: true,
            exhausted,
            next_reset_at_unix,
            summary: windows.join("  -  "),
            api,
        }
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

    #[cfg(windows)]
    #[test]
    #[ignore = "requires Node.js and the Windows provider wrapper"]
    fn fixture_reports_accepted_and_rejected_steers() {
        let root = std::env::temp_dir().join(format!("pocketagent-turn-{}", uuid::Uuid::new_v4()));
        let fixture = root.join("node_modules/@openai/codex/bin/codex.js");
        std::fs::create_dir_all(fixture.parent().unwrap()).unwrap();
        std::fs::write(&fixture, "import('./fixture.mjs');").unwrap();
        std::fs::write(
            fixture.with_file_name("fixture.mjs"),
            include_str!("../../../tests/fixtures/codex.mjs"),
        )
        .unwrap();
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let result = runtime.block_on(async {
            tokio::time::timeout(std::time::Duration::from_secs(10), async {
                let mut session = CodexSession::connect(
                    root.join("codex.ps1").to_str().unwrap(),
                    root.to_str().unwrap(),
                )
                .await
                .unwrap();
                session.new_thread(None).await.unwrap();
                let (cancel, mut cancel_rx) = watch::channel(false);
                let (send, mut receive) = mpsc::unbounded_channel();
                let outcomes = std::cell::RefCell::new(Vec::new());
                let on_start = |turn_id: Option<String>| {
                    assert!(turn_id.is_some());
                    for (prompt, expected_turn_id) in
                        [("accepted", None), ("rejected", Some("stale-id".into()))]
                    {
                        send.send(SteerRequest {
                            prompt: prompt.into(),
                            expected_turn_id,
                            from_desktop: true,
                        })
                        .unwrap();
                    }
                };
                let on_response = |prompt: &str, accepted| {
                    let mut outcomes = outcomes.borrow_mut();
                    outcomes.push((prompt.to_owned(), accepted));
                    if outcomes.len() == 2 {
                        cancel.send(true).unwrap();
                    }
                };
                let error = session
                    .send_prompt(
                        "hold",
                        None,
                        None,
                        PromptControl {
                            cancel: &mut cancel_rx,
                            steers: &mut receive,
                            on_turn_started: &on_start,
                            on_steer_response: &on_response,
                        },
                        InteractionState::shared(),
                        |_| {},
                    )
                    .await
                    .unwrap_err();
                assert_eq!(error.to_string(), "Turn stopped by user");
                assert_eq!(
                    outcomes.into_inner(),
                    vec![("accepted".into(), true), ("rejected".into(), false)]
                );
            })
            .await
        });
        let _ = std::fs::remove_dir_all(root);
        result.expect("fixture turn must finish");
    }

    #[test]
    fn steer_parameters_bind_the_observed_turn_without_overrides() {
        let steer = SteerRequest {
            prompt: "continue with the tests".into(),
            expected_turn_id: None,
            from_desktop: false,
        };
        let parameters = CodexSession::steer_parameters("thread-1", "turn-2", &steer);
        assert_eq!(parameters["threadId"], "thread-1");
        assert_eq!(parameters["expectedTurnId"], "turn-2");
        assert_eq!(parameters["input"][0]["text"], "continue with the tests");
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
    fn thread_text_preserves_roles_and_activity_items() {
        let text = thread_text(&json!({
            "thread": { "items": [
                { "type": "userMessage", "content": [{ "type": "text", "text": "hello" }] },
                { "type": "commandExecution", "command": "cargo test", "aggregatedOutput": "ok" },
                { "type": "agentMessage", "text": "done" }
            ] }
        }));
        assert!(text.contains("hello"));
        assert!(text.contains("\u{001e}user\nhello"));
        assert!(text.contains("\u{001e}activity|Command: cargo test"));
        assert!(text.contains("done"));
        assert!(text.contains("\u{001e}assistant\ndone"));
    }

    #[test]
    fn thread_text_reads_legacy_turns_when_flat_items_are_absent() {
        let text = thread_text(&json!({
            "thread": { "turns": [
                { "items": [
                    { "type": "userMessage", "content": [{ "type": "text", "text": "hello" }] },
                    { "type": "agentMessage", "text": "done" }
                ] }
            ] }
        }));
        assert!(text.contains("\u{001e}user\nhello"));
        assert!(text.contains("\u{001e}assistant\ndone"));
    }

    #[test]
    fn completed_agent_items_are_rendered_without_duplicate_deltas() {
        let mut output = String::new();
        let mut streamed = HashSet::new();
        append_completed_agent_item(
            &mut output,
            &mut streamed,
            &json!({
                "params": { "item": { "id": "item-1", "type": "agentMessage", "text": "reply" } }
            }),
        );
        assert_eq!(output, "reply");
        append_completed_agent_item(
            &mut output,
            &mut streamed,
            &json!({
                "params": { "item": { "id": "item-1", "type": "agentMessage", "text": "reply" } }
            }),
        );
        assert_eq!(output, "reply");
    }

    #[test]
    fn responses_message_items_render_assistant_content() {
        let text = thread_text(&json!({
            "thread": { "items": [
                { "type": "message", "role": "assistant", "content": [
                    { "type": "output_text", "text": "current reply" }
                ] }
            ] }
        }));
        assert_eq!(text, "\u{001e}assistant\ncurrent reply");
    }

    #[test]
    fn completed_responses_message_reaches_prompt_output() {
        let mut output = String::new();
        let mut streamed = HashSet::new();
        append_completed_agent_item(
            &mut output,
            &mut streamed,
            &json!({
                "params": { "item": {
                    "id": "response-item-1",
                    "type": "message",
                    "role": "assistant",
                    "content": [{ "type": "output_text", "text": "current reply" }]
                } }
            }),
        );
        assert_eq!(output, "current reply");
    }

    #[test]
    fn model_catalog_preserves_provider_default() {
        let catalog = CodexSession::model_catalog(&json!({
            "data": [
                { "model": "unsupported", "displayName": "Unsupported", "isDefault": false },
                { "model": "account-default", "displayName": "Account default", "isDefault": true }
            ]
        }));
        assert_eq!(catalog.default_model.as_deref(), Some("account-default"));
        assert_eq!(catalog.entries[0], "unsupported - Unsupported");
    }

    #[cfg(windows)]
    #[test]
    fn fixture_model_catalog_follows_provider_cursor() {
        let root =
            std::env::temp_dir().join(format!("pocketagent-models-{}", uuid::Uuid::new_v4()));
        let fixture = root.join("node_modules/@openai/codex/bin/codex.js");
        std::fs::create_dir_all(fixture.parent().unwrap()).unwrap();
        std::fs::write(&fixture, "import('./fixture.mjs');").unwrap();
        std::fs::write(
            fixture.with_file_name("fixture.mjs"),
            include_str!("../../../tests/fixtures/codex.mjs"),
        )
        .unwrap();
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let result = runtime.block_on(async {
            let mut session = CodexSession::connect(
                root.join("codex.ps1").to_str().unwrap(),
                root.to_str().unwrap(),
            )
            .await
            .unwrap();
            session.list_models().await.unwrap()
        });
        let _ = std::fs::remove_dir_all(root);
        assert_eq!(
            result
                .api
                .iter()
                .map(|model| model.model.as_str())
                .collect::<Vec<_>>(),
            vec!["gpt-6-astra", "gpt-5.6-sol"]
        );
    }

    #[cfg(windows)]
    #[test]
    fn fixture_completed_item_reply_reaches_prompt() {
        let root = std::env::temp_dir().join(format!("pocketagent-reply-{}", uuid::Uuid::new_v4()));
        let fixture = root.join("node_modules/@openai/codex/bin/codex.js");
        std::fs::create_dir_all(fixture.parent().unwrap()).unwrap();
        std::fs::write(&fixture, "import('./fixture.mjs');").unwrap();
        std::fs::write(
            fixture.with_file_name("fixture.mjs"),
            include_str!("../../../tests/fixtures/codex.mjs"),
        )
        .unwrap();
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let result = runtime.block_on(async {
            let mut session = CodexSession::connect(
                root.join("codex.ps1").to_str().unwrap(),
                root.to_str().unwrap(),
            )
            .await
            .unwrap();
            session.new_thread(None).await.unwrap();
            let (_cancel, mut cancel_rx) = watch::channel(false);
            let (_steer_tx, mut steer_rx) = mpsc::unbounded_channel();
            session
                .send_prompt(
                    "completed-item",
                    None,
                    None,
                    PromptControl {
                        cancel: &mut cancel_rx,
                        steers: &mut steer_rx,
                        on_turn_started: &|_| {},
                        on_steer_response: &|_, _| {},
                    },
                    InteractionState::shared(),
                    |_| {},
                )
                .await
        });
        let _ = std::fs::remove_dir_all(root);
        assert_eq!(result.unwrap(), "Reply delivered as a completed item.");
    }

    #[test]
    fn rate_limit_summary_exposes_only_remaining_windows() {
        let summary = rate_limit_status(&json!({
            "rateLimits": {
                "primary": { "usedPercent": 20, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 5, "windowDurationMins": 10080 }
            },
            "accountId": "must-not-be-rendered"
        }));
        assert_eq!(summary.summary, "5-hour 80%  -  weekly 95%");
        assert!(!summary.summary.contains("account"));
    }

    #[test]
    fn rate_limit_status_marks_exhaustion_and_reset() {
        let reset = chrono::Utc::now().timestamp() + 300;
        let status = rate_limit_status(&json!({
            "rateLimits": {
                "primary": { "usedPercent": 100, "windowDurationMins": 300, "resetsAt": reset },
                "secondary": { "usedPercent": 20, "windowDurationMins": 10080 }
            }
        }));
        assert!(status.available);
        assert!(status.exhausted);
        assert_eq!(status.next_reset_at_unix, Some(reset));
        assert_eq!(status.summary, "5-hour 0%  -  weekly 80%");
    }

    #[test]
    fn paginated_turns_render_all_items_in_order() {
        let text = turns_text(&[
            json!({ "items": [{ "type": "userMessage", "text": "first" }] }),
            json!({ "items": [{ "type": "agentMessage", "text": "second" }] }),
        ]);
        assert_eq!(text, "\u{001e}user\nfirst\n\n\u{001e}assistant\nsecond");
    }

    #[test]
    fn recent_paginated_turns_are_reversed_for_chronological_display() {
        let newest_first = vec![
            json!({ "items": [{ "type": "agentMessage", "text": "newest" }] }),
            json!({ "items": [{ "type": "userMessage", "text": "older" }] }),
        ];
        let turns = recent_turns_in_display_order(&newest_first, 40);
        assert_eq!(
            turns_text(&turns),
            "\u{001e}user\nolder\n\n\u{001e}assistant\nnewest"
        );
    }

    #[test]
    fn failed_completed_turns_are_not_reported_as_success() {
        let message = json!({
            "method": "turn/completed",
            "params": {
                "turn": {
                    "status": "failed",
                    "error": { "message": "provider quota rejected the turn" }
                }
            }
        });
        assert_eq!(
            turn_failure(&message).as_deref(),
            Some("provider quota rejected the turn")
        );
        assert!(
            turn_failure(&json!({
                "params": { "turn": { "status": "completed" } }
            }))
            .is_none()
        );
    }

    #[test]
    fn image_view_items_become_markdown_images() {
        let text = thread_text(&json!({
            "thread": { "items": [
                { "type": "imageView", "path": "C:/repo/output.png" }
            ] }
        }));
        assert_eq!(text, "\u{001e}assistant\n![Image](C:/repo/output.png)");
        let structured = thread_text(&json!({
            "thread": { "items": [
                { "type": "imageView", "source": { "url": "data:image/png;base64,abc" } }
            ] }
        }));
        assert_eq!(
            structured,
            "\u{001e}assistant\n![Image](data:image/png;base64,abc)"
        );
    }
}
