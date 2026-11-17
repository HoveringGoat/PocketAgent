mod codex;
mod store;

use codex::{CodexSession, InteractionState, PromptRequest, SharedInteraction};
use pocketagent_relay::RelayNode;
use pocketagent_relay::config::RelayConfig;
use pocketagent_relay::transport::{RelayAuthRegistry, RelayTransportServer};
use pulldown_cmark::{Event, HeadingLevel, Options, Parser, Tag, TagEnd};
use std::{
    env,
    path::PathBuf,
    sync::{Arc, Mutex},
};
use store::DesktopStore;
use tokio::sync::mpsc::{self, UnboundedSender};
use tokio::{
    net::TcpListener,
    sync::{oneshot, watch},
};

slint::include_modules!();

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let window = AppWindow::new()?;
    let interaction = InteractionState::shared();
    let store = DesktopStore::load();
    let repository = if !store.repository_path.is_empty()
        && std::path::Path::new(&store.repository_path).is_dir()
    {
        store.repository_path.clone()
    } else {
        default_repository()
    };
    let executable = if store.codex_executable.is_empty() {
        default_codex_executable()
    } else {
        store.codex_executable.clone()
    };
    window.set_repository_path(repository.clone().into());
    window.set_codex_executable(executable.clone().into());

    {
        let interaction = interaction.clone();
        window.on_answer_interaction(move |answer| {
            let pending = interaction
                .0
                .lock()
                .expect("interaction lock should work")
                .pending
                .clone();
            let Some(pending) = pending else { return };
            let answer = answer.to_string();
            let response = if pending.method == "item/tool/requestUserInput" {
                let answers = pending
                    .question_ids
                    .iter()
                    .enumerate()
                    .map(|(index, id)| {
                        let value = answer.lines().nth(index).unwrap_or(&answer).to_owned();
                        (id.clone(), serde_json::json!({ "answers": [value] }))
                    })
                    .collect::<serde_json::Map<_, _>>();
                serde_json::json!({ "answers": answers })
            } else {
                serde_json::json!({ "decision": answer })
            };
            InteractionState::respond(&interaction, response);
        });
    }
    let codex_commands = start_codex_worker(window.as_weak(), interaction.clone());
    let relay_shutdown: Arc<Mutex<Option<oneshot::Sender<()>>>> = Arc::new(Mutex::new(None));
    {
        let commands = codex_commands.clone();
        window.on_connect_codex(move |repository, executable| {
            let _ = commands.send(CodexCommand::Connect {
                repository: repository.to_string(),
                executable: executable.to_string(),
            });
        });
    }
    {
        let commands = codex_commands.clone();
        window.on_new_conversation(move |model, effort| {
            let _ = commands.send(CodexCommand::NewConversation {
                model: model.to_string(),
                effort: effort.to_string(),
            });
        });
    }
    {
        let commands = codex_commands.clone();
        window.on_refresh_sessions(move || {
            let _ = commands.send(CodexCommand::RefreshSessions);
        });
    }
    {
        let commands = codex_commands.clone();
        window.on_select_session(move |id| {
            let _ = commands.send(CodexCommand::SelectSession(id.to_string()));
        });
    }
    {
        let commands = codex_commands.clone();
        window.on_archive_session(move || {
            let _ = commands.send(CodexCommand::ArchiveSelected);
        });
    }
    {
        let commands = codex_commands.clone();
        window.on_restore_session(move || {
            let _ = commands.send(CodexCommand::RestoreSelected);
        });
    }
    {
        let commands = codex_commands.clone();
        window.on_toggle_pin(move || {
            let _ = commands.send(CodexCommand::TogglePin);
        });
    }
    {
        let commands = codex_commands.clone();
        window.on_rename_session(move |title| {
            let _ = commands.send(CodexCommand::RenameSelected(title.to_string()));
        });
    }
    {
        let commands = codex_commands.clone();
        window.on_toggle_archived(move || {
            let _ = commands.send(CodexCommand::ToggleArchived);
        });
    }
    let startup_commands = codex_commands.clone();
    window.on_send_prompt(move |prompt, model, effort| {
        let _ = codex_commands.send(CodexCommand::Prompt {
            prompt: prompt.to_string(),
            model: model.to_string(),
            effort: effort.to_string(),
        });
    });
    {
        let commands = startup_commands.clone();
        window.on_stop_work(move || {
            let _ = commands.send(CodexCommand::Stop);
        });
    }

    let _ = startup_commands.send(CodexCommand::Connect {
        repository,
        executable,
    });

    {
        let weak = window.as_weak();
        let relay_shutdown = Arc::clone(&relay_shutdown);
        window.on_start_relay(move || {
            if relay_shutdown
                .lock()
                .expect("relay state lock should work")
                .is_some()
            {
                return;
            }
            let weak = weak.clone();
            let relay_shutdown = Arc::clone(&relay_shutdown);
            std::thread::spawn(move || {
                let runtime = match tokio::runtime::Builder::new_current_thread()
                    .enable_all()
                    .build()
                {
                    Ok(runtime) => runtime,
                    Err(error) => {
                        set_relay_error(&weak, format!("Runtime failed: {error}"));
                        return;
                    }
                };
                runtime.block_on(async move {
                    let listener = match TcpListener::bind("127.0.0.1:0").await {
                        Ok(listener) => listener,
                        Err(error) => {
                            set_relay_error(&weak, format!("Could not bind relay: {error}"));
                            return;
                        }
                    };
                    let address = match listener.local_addr() {
                        Ok(address) => address,
                        Err(error) => {
                            set_relay_error(
                                &weak,
                                format!("Could not read relay address: {error}"),
                            );
                            return;
                        }
                    };
                    let config = RelayConfig::new();
                    let auth = RelayAuthRegistry::default();
                    auth.register_host(config.host_id, config.host_token.into_bytes())
                        .await;
                    auth.register_client(config.client_id, config.client_token.into_bytes())
                        .await;
                    let (shutdown_tx, shutdown_rx) = oneshot::channel();
                    *relay_shutdown.lock().expect("relay state lock should work") =
                        Some(shutdown_tx);
                    set_relay_running(&weak, address.to_string(), config.host_id.to_string());
                    let server = RelayTransportServer::new(RelayNode::new(), auth);
                    let result = server.serve_until(listener, shutdown_rx).await;
                    *relay_shutdown.lock().expect("relay state lock should work") = None;
                    if let Err(error) = result {
                        set_relay_error(&weak, format!("Relay stopped with an error: {error}"));
                    } else {
                        set_relay_stopped(&weak);
                    }
                });
            });
        });
    }
    {
        let weak = window.as_weak();
        let relay_shutdown = Arc::clone(&relay_shutdown);
        window.on_stop_relay(move || {
            if let Some(shutdown) = relay_shutdown
                .lock()
                .expect("relay state lock should work")
                .take()
            {
                let _ = shutdown.send(());
            } else {
                set_relay_stopped(&weak);
            }
        });
    }

    window.run()?;
    Ok(())
}

#[derive(Clone)]
enum CodexCommand {
    Connect {
        repository: String,
        executable: String,
    },
    NewConversation {
        model: String,
        effort: String,
    },
    RefreshSessions,
    SelectSession(String),
    ArchiveSelected,
    RestoreSelected,
    TogglePin,
    RenameSelected(String),
    ToggleArchived,
    Prompt {
        prompt: String,
        model: String,
        effort: String,
    },
    Stop,
}

fn start_codex_worker(
    weak: slint::Weak<AppWindow>,
    interaction: SharedInteraction,
) -> UnboundedSender<CodexCommand> {
    let (sender, mut receiver) = mpsc::unbounded_channel();
    std::thread::spawn(move || {
        let runtime = match tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
        {
            Ok(runtime) => runtime,
            Err(error) => {
                set_codex_error(&weak, format!("Runtime failed: {error}"));
                return;
            }
        };
        runtime.block_on(async move {
            let mut session: Option<CodexSession> = None;
            let mut store = DesktopStore::load();
            let mut selected_id = None::<String>;
            let mut showing_archived = false;
            set_sessions(&weak, store.display_sessions());
            while let Some(command) = receiver.recv().await {
                match command {
                    CodexCommand::Connect {
                        repository,
                        executable,
                    } => {
                        set_codex_status(&weak, "Connecting to Codex...".into(), None);
                        match CodexSession::connect(&executable, &repository).await {
                            Ok(mut connected) => {
                                let sessions = connected.list_threads().await;
                                store.repository_path = repository;
                                store.codex_executable = executable;
                                session = Some(connected);
                                set_codex_status(&weak, "Codex is ready".into(), None);
                                if let Ok(models) = session
                                    .as_mut()
                                    .expect("session was set")
                                    .list_models()
                                    .await
                                {
                                    set_model_catalog(&weak, models.join("\n"));
                                }
                                match sessions {
                                    Ok(sessions) => {
                                        store.merge_threads(sessions);
                                        let _ = store.save();
                                        set_sessions(&weak, store.display_sessions());
                                    }
                                    Err(error) => set_codex_error(&weak, error.to_string()),
                                }
                            }
                            Err(error) => {
                                session = None;
                                set_codex_error(&weak, error.to_string());
                            }
                        }
                    }
                    CodexCommand::NewConversation { model, effort } => {
                        let Some(current) = session.as_mut() else {
                            set_codex_error(&weak, "Connect Codex first.".into());
                            continue;
                        };
                        match current
                            .new_thread((!model.trim().is_empty()).then_some(model.as_str()))
                            .await
                        {
                            Ok(id) => {
                                selected_id = Some(id.clone());
                                set_selected_session(&weak, id.clone());
                                store.merge_threads([(id, "New conversation".into())]);
                                let _ = store.set_model_options(
                                    selected_id.as_deref().unwrap_or_default(),
                                    Some(model),
                                    Some(effort),
                                );
                                let _ = store.save();
                                set_transcript(&weak, "New conversation ready".into());
                                refresh_sessions(current, &weak, &mut store).await;
                            }
                            Err(error) => set_codex_error(&weak, error.to_string()),
                        }
                    }
                    CodexCommand::RefreshSessions => {
                        let Some(current) = session.as_mut() else {
                            set_codex_error(&weak, "Connect Codex first.".into());
                            continue;
                        };
                        refresh_sessions(current, &weak, &mut store).await;
                    }
                    CodexCommand::SelectSession(id) => {
                        let Some(current) = session.as_mut() else {
                            set_codex_error(&weak, "Connect Codex first.".into());
                            continue;
                        };
                        let options = store
                            .conversations
                            .iter()
                            .find(|conversation| conversation.id == id)
                            .and_then(|conversation| conversation.model.clone())
                            .unwrap_or_default();
                        match current
                            .resume_thread(
                                &id,
                                (!options.trim().is_empty()).then_some(options.as_str()),
                            )
                            .await
                        {
                            Ok(history) => {
                                selected_id = Some(id.clone());
                                set_selected_session(&weak, id);
                                set_transcript(
                                    &weak,
                                    if history.is_empty() {
                                        "Conversation loaded".into()
                                    } else {
                                        history
                                    },
                                )
                            }
                            Err(error) => set_codex_error(&weak, error.to_string()),
                        }
                    }
                    CodexCommand::ArchiveSelected => {
                        if let Some(id) = selected_id.as_deref()
                            && store.set_archived(id, true)
                        {
                            let _ = store.save();
                            set_sessions(&weak, visible_sessions(&store, showing_archived));
                        }
                    }
                    CodexCommand::RestoreSelected => {
                        if let Some(id) = selected_id.as_deref()
                            && store.set_archived(id, false)
                        {
                            let _ = store.save();
                            set_sessions(&weak, visible_sessions(&store, showing_archived));
                        }
                    }
                    CodexCommand::TogglePin => {
                        if let Some(id) = selected_id.as_deref()
                            && store.toggle_pinned(id)
                        {
                            let _ = store.save();
                            set_sessions(&weak, visible_sessions(&store, showing_archived));
                        }
                    }
                    CodexCommand::RenameSelected(title) => {
                        if let Some(id) = selected_id.as_deref()
                            && store.rename(id, &title)
                        {
                            let _ = store.save();
                            set_sessions(&weak, visible_sessions(&store, showing_archived));
                        }
                    }
                    CodexCommand::ToggleArchived => {
                        showing_archived = !showing_archived;
                        set_sessions(&weak, visible_sessions(&store, showing_archived));
                        set_archived_view(&weak, showing_archived);
                    }
                    CodexCommand::Prompt {
                        prompt,
                        model,
                        effort,
                    } => {
                        let Some(current) = session.as_mut() else {
                            set_codex_error(&weak, "Connect Codex first.".into());
                            continue;
                        };
                        if let Some(id) = selected_id.as_deref()
                            && store.set_model_options(
                                id,
                                Some(model.clone()),
                                Some(effort.clone()),
                            )
                        {
                            let _ = store.save();
                        }
                        InteractionState::clear(&interaction);
                        clear_pending_interaction(&weak);
                        set_codex_status(&weak, "Codex is working...".into(), None);
                        let (stop_tx, mut stop_rx) = watch::channel(false);
                        let result = loop {
                            tokio::select! {
                                result = current.send_prompt(
                                    &prompt,
                                    (!model.trim().is_empty()).then_some(model.as_str()),
                                    (!effort.trim().is_empty()).then_some(effort.as_str()),
                                    &mut stop_rx,
                                    interaction.clone(),
                                    {
                                        let weak = weak.clone();
                                        move |request| set_pending_interaction(&weak, request)
                                    },
                                ) => break Some(result),
                                command = receiver.recv() => match command {
                                    Some(CodexCommand::Stop) => {
                                        let _ = stop_tx.send(true);
                                    }
                                    Some(_) => {}
                                    None => break None,
                                },
                            }
                        };
                        let Some(result) = result else { break };
                        InteractionState::clear(&interaction);
                        clear_pending_interaction(&weak);
                        match result {
                            Ok(response) => {
                                let transcript = current
                                    .current_thread_text()
                                    .await
                                    .ok()
                                    .filter(|text| !text.is_empty())
                                    .unwrap_or(response);
                                set_codex_status(&weak, "Codex is ready".into(), Some(transcript));
                                refresh_sessions(current, &weak, &mut store).await;
                            }
                            Err(error) if error.to_string() == "Turn stopped by user" => {
                                set_codex_status(&weak, "Codex is ready".into(), None);
                            }
                            Err(error) => set_codex_error(&weak, error.to_string()),
                        }
                    }
                    CodexCommand::Stop => {}
                }
            }
        });
    });
    sender
}

async fn refresh_sessions(
    session: &mut CodexSession,
    weak: &slint::Weak<AppWindow>,
    store: &mut DesktopStore,
) {
    match session.list_threads().await {
        Ok(sessions) => {
            store.merge_threads(sessions);
            let _ = store.save();
            set_sessions(weak, store.display_sessions());
        }
        Err(error) => set_codex_error(weak, error.to_string()),
    }
}

fn visible_sessions(store: &DesktopStore, showing_archived: bool) -> Vec<(String, String)> {
    if showing_archived {
        store.display_archived_sessions()
    } else {
        store.display_sessions()
    }
}

fn default_repository() -> String {
    if let Ok(repository) = env::var("POCKETAGENT_REPOSITORY")
        && PathBuf::from(&repository).is_dir()
    {
        return repository;
    }
    env::current_dir()
        .ok()
        .filter(|path| path.is_dir())
        .map(|path| path.to_string_lossy().into_owned())
        .unwrap_or_default()
}

fn default_codex_executable() -> String {
    if let Ok(executable) = env::var("POCKETAGENT_CODEX_EXECUTABLE")
        && !executable.trim().is_empty()
    {
        return executable;
    }
    if cfg!(windows)
        && let Ok(app_data) = env::var("APPDATA")
    {
        let wrapper = PathBuf::from(app_data).join("npm/codex.ps1");
        if wrapper.is_file() {
            return wrapper.to_string_lossy().into_owned();
        }
    }
    "codex".into()
}

fn set_codex_status(weak: &slint::Weak<AppWindow>, status: String, transcript: Option<String>) {
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_codex_status(status.into());
        if let Some(transcript) = transcript {
            set_transcript_blocks(&window, &transcript);
        }
    });
}

fn set_transcript(weak: &slint::Weak<AppWindow>, transcript: String) {
    let _ = weak.upgrade_in_event_loop(move |window| set_transcript_blocks(&window, &transcript));
}

fn set_transcript_blocks(window: &AppWindow, markdown: &str) {
    let blocks = markdown_blocks(markdown)
        .into_iter()
        .map(|block| MarkdownBlock {
            text: block.text.into(),
            kind: block.kind.into(),
        })
        .collect::<Vec<_>>();
    window.set_transcript_blocks(slint::ModelRc::new(slint::VecModel::from(blocks)));
}

fn set_sessions(weak: &slint::Weak<AppWindow>, sessions: Vec<(String, String)>) {
    let items = sessions
        .into_iter()
        .map(|(id, title)| SessionItem {
            id: id.into(),
            title: title.into(),
        })
        .collect::<Vec<_>>();
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_session_items(slint::ModelRc::new(slint::VecModel::from(items)));
    });
}

fn set_selected_session(weak: &slint::Weak<AppWindow>, id: String) {
    let _ = weak.upgrade_in_event_loop(move |window| window.set_current_session_id(id.into()));
}

fn set_archived_view(weak: &slint::Weak<AppWindow>, showing: bool) {
    let _ = weak.upgrade_in_event_loop(move |window| window.set_showing_archived(showing));
}

fn set_model_catalog(weak: &slint::Weak<AppWindow>, models: String) {
    let value = if models.is_empty() {
        "Provider returned no visible models".to_owned()
    } else {
        models
    };
    let _ = weak.upgrade_in_event_loop(move |window| window.set_model_catalog(value.into()));
}

fn set_pending_interaction(weak: &slint::Weak<AppWindow>, request: PromptRequest) {
    let decisions = request.decisions.join(",");
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_pending_interaction(request.prompt.into());
        window.set_interaction_method(request.method.into());
        window.set_interaction_decisions(decisions.into());
    });
}

fn clear_pending_interaction(weak: &slint::Weak<AppWindow>) {
    let _ = weak.upgrade_in_event_loop(|window| {
        window.set_pending_interaction("".into());
        window.set_interaction_method("".into());
        window.set_interaction_decisions("".into());
    });
}

fn set_codex_error(weak: &slint::Weak<AppWindow>, message: String) {
    set_codex_status(weak, format!("Codex error: {message}"), None);
}

fn set_relay_running(weak: &slint::Weak<AppWindow>, endpoint: String, host_id: String) {
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_relay_running(true);
        window.set_relay_status("Relay is running".into());
        window.set_relay_endpoint(format!("ws://{endpoint}  •  host {host_id}").into());
    });
}

fn set_relay_stopped(weak: &slint::Weak<AppWindow>) {
    let _ = weak.upgrade_in_event_loop(|window| {
        window.set_relay_running(false);
        window.set_relay_status("Relay is stopped".into());
        window.set_relay_endpoint("No endpoint".into());
    });
}

fn set_relay_error(weak: &slint::Weak<AppWindow>, message: String) {
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_relay_running(false);
        window.set_relay_status(message.into());
        window.set_relay_endpoint("No endpoint".into());
    });
}

struct ParsedBlock {
    text: String,
    kind: String,
}

fn markdown_blocks(markdown: &str) -> Vec<ParsedBlock> {
    let mut blocks = Vec::new();
    let mut current = String::new();
    let mut kind = "paragraph".to_owned();
    let mut list_depth = 0usize;
    let parser = Parser::new_ext(markdown, Options::all());
    for event in parser {
        match event {
            Event::Start(Tag::Heading { level, .. }) => {
                flush_block(&mut blocks, &mut current, &mut kind);
                kind = if matches!(level, HeadingLevel::H1 | HeadingLevel::H2) {
                    "heading".into()
                } else {
                    "subheading".into()
                };
            }
            Event::End(TagEnd::Heading(_)) => flush_block(&mut blocks, &mut current, &mut kind),
            Event::Start(Tag::Paragraph) => {
                if current.is_empty() {
                    kind = "paragraph".into();
                }
            }
            Event::End(TagEnd::Paragraph) => flush_block(&mut blocks, &mut current, &mut kind),
            Event::Start(Tag::Item) => {
                flush_block(&mut blocks, &mut current, &mut kind);
                kind = "bullet".into();
                current.push_str("• ");
                list_depth += 1;
            }
            Event::End(TagEnd::Item) => {
                flush_block(&mut blocks, &mut current, &mut kind);
                list_depth = list_depth.saturating_sub(1);
            }
            Event::Start(Tag::CodeBlock(_)) => {
                flush_block(&mut blocks, &mut current, &mut kind);
                kind = "code".into();
            }
            Event::End(TagEnd::CodeBlock) => flush_block(&mut blocks, &mut current, &mut kind),
            Event::Text(text) | Event::Code(text) => current.push_str(&text),
            Event::SoftBreak | Event::HardBreak => current.push('\n'),
            Event::Rule => {
                flush_block(&mut blocks, &mut current, &mut kind);
                blocks.push(ParsedBlock {
                    text: "────────".into(),
                    kind: "rule".into(),
                });
            }
            Event::Start(Tag::List(_)) => {
                if list_depth == 0 {
                    flush_block(&mut blocks, &mut current, &mut kind);
                }
            }
            Event::End(TagEnd::List(_)) => flush_block(&mut blocks, &mut current, &mut kind),
            _ => {}
        }
    }
    flush_block(&mut blocks, &mut current, &mut kind);
    blocks
}

fn flush_block(blocks: &mut Vec<ParsedBlock>, current: &mut String, kind: &mut String) {
    let text = current.trim_end_matches('\n').to_owned();
    if !text.is_empty() {
        blocks.push(ParsedBlock {
            text,
            kind: kind.clone(),
        });
    }
    current.clear();
    *kind = "paragraph".into();
}

#[cfg(test)]
mod tests {
    use super::markdown_blocks;

    #[test]
    fn markdown_blocks_preserve_headings_bullets_and_code() {
        let blocks = markdown_blocks("# Heading\n\n- one\n- two\n\n```rust\nlet x = 1;\n```");
        assert_eq!(blocks[0].kind, "heading");
        assert_eq!(blocks[1].text, "• one");
        assert_eq!(blocks[2].text, "• two");
        assert_eq!(blocks[3].kind, "code");
    }
}
