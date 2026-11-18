mod codex;
mod diagnostics;
mod store;

use codex::{CodexSession, InteractionState, PromptRequest, SharedInteraction};
use pocketagent_host::{
    DeviceRegistry, HostAction, HostConversation, HostHttpState, HostSchedule, SessionInfo,
    router as host_router,
};
use pocketagent_relay::RelayNode;
use pocketagent_relay::config::RelayConfig;
use pocketagent_relay::transport::{RelayAuthRegistry, RelayTransportServer};
use pulldown_cmark::{Event, HeadingLevel, Options, Parser, Tag, TagEnd};
use std::{
    env, fs,
    path::PathBuf,
    sync::{Arc, Mutex},
};
use store::{DesktopStore, now_unix};
use tokio::sync::mpsc::{self, UnboundedSender};
use tokio::{
    net::TcpListener,
    sync::{oneshot, watch},
};

slint::include_modules!();

fn main() -> Result<(), Box<dyn std::error::Error>> {
    diagnostics::record("startup", "desktop_launch");
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
    let host_registry_path = DesktopStore::path()
        .parent()
        .unwrap_or_else(|| std::path::Path::new("."))
        .join("devices.json");
    let host_registry = DeviceRegistry::load(&host_registry_path).unwrap_or_default();
    host_registry.renew_pairing();
    let host_pairing = host_registry.pairing_display();
    let host_conversations = Arc::new(std::sync::RwLock::new(Vec::<HostConversation>::new()));
    let host_schedules = Arc::new(std::sync::RwLock::new(Vec::<HostSchedule>::new()));
    let (host_actions, mut host_action_receiver) = mpsc::unbounded_channel::<HostAction>();
    let host_state_registry = host_registry.clone();
    let host_state = HostHttpState::new(host_state_registry, host_actions, move |identity| {
        SessionInfo {
            authenticated: identity.is_some(),
            device_name: identity.map(|item| item.name.clone()),
            repository: None,
            host_name: std::env::var("COMPUTERNAME").unwrap_or_else(|_| "desktop".into()),
            version: env!("CARGO_PKG_VERSION").into(),
        }
    });
    *host_state
        .image_root
        .write()
        .expect("host image-root lock should not be poisoned") = repository.clone();
    host_state
        .conversations
        .write()
        .expect("host conversation lock should not be poisoned")
        .clear();
    let host_endpoint = start_host_server(host_state.clone());
    window.set_host_status("Host API is running".into());
    window.set_host_endpoint(host_endpoint.into());
    window.set_host_pairing(format!("Pairing code: {} (single use)", host_pairing.code).into());
    let codex_commands = start_codex_worker(
        window.as_weak(),
        interaction.clone(),
        host_conversations.clone(),
        host_registry.clone(),
        host_schedules.clone(),
    );
    let host_commands = codex_commands.clone();
    std::thread::spawn(move || {
        while let Some(action) = host_action_receiver.blocking_recv() {
            if host_commands.send(CodexCommand::Host(action)).is_err() {
                break;
            }
        }
    });
    {
        let registry = host_registry.clone();
        let weak = window.as_weak();
        window.on_set_device_access(move |conversation_id, device_id, authorized| {
            let Ok(device_id) = uuid::Uuid::parse_str(device_id.as_str()) else {
                return;
            };
            if authorized {
                let _ = registry.grant_conversation(conversation_id.to_string(), device_id);
            } else {
                let _ = registry.revoke_conversation(conversation_id.as_str(), device_id);
            }
            set_host_devices(&weak, &registry, conversation_id.as_str());
        });
    }
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
            schedule_id: None,
            conversation_id: None,
        });
    });
    {
        let commands = startup_commands.clone();
        window.on_schedule_prompt(move |prompt, model, effort, delay| {
            let _ = commands.send(CodexCommand::Schedule {
                prompt: prompt.to_string(),
                model: model.to_string(),
                effort: effort.to_string(),
                delay: delay.to_string(),
            });
        });
    }
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
        window.on_start_relay(move |bind_address| {
            diagnostics::record("relay", "start_requested");
            if relay_shutdown
                .lock()
                .expect("relay state lock should work")
                .is_some()
            {
                return;
            }
            let weak = weak.clone();
            let relay_shutdown = Arc::clone(&relay_shutdown);
            let bind_address = bind_address.to_string();
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
                    let config = match load_relay_config() {
                        Ok(config) => config,
                        Err(error) => {
                            set_relay_error(
                                &weak,
                                format!("Could not load relay identity: {error}"),
                            );
                            return;
                        }
                    };
                    let listener = match TcpListener::bind(&bind_address).await {
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
                    let auth = RelayAuthRegistry::default();
                    auth.register_host(config.host_id, config.host_token.clone().into_bytes())
                        .await;
                    auth.register_client(
                        config.client_id,
                        config.client_token.clone().into_bytes(),
                    )
                    .await;
                    let (shutdown_tx, shutdown_rx) = oneshot::channel();
                    *relay_shutdown.lock().expect("relay state lock should work") =
                        Some(shutdown_tx);
                    set_relay_running(&weak, address.to_string(), config.host_id.to_string());
                    let server = RelayTransportServer::new(RelayNode::new(), auth);
                    let result = server.serve_until(listener, shutdown_rx).await;
                    *relay_shutdown.lock().expect("relay state lock should work") = None;
                    if let Err(error) = result {
                        diagnostics::record("relay", "stopped_error");
                        set_relay_error(&weak, format!("Relay stopped with an error: {error}"));
                    } else {
                        diagnostics::record("relay", "stopped");
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
            diagnostics::record("relay", "stop_requested");
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
        schedule_id: Option<String>,
        conversation_id: Option<String>,
    },
    Schedule {
        prompt: String,
        model: String,
        effort: String,
        delay: String,
    },
    Host(HostAction),
    Stop,
}

fn start_codex_worker(
    weak: slint::Weak<AppWindow>,
    interaction: SharedInteraction,
    host_conversations: Arc<std::sync::RwLock<Vec<HostConversation>>>,
    host_registry: DeviceRegistry,
    host_schedules: Arc<std::sync::RwLock<Vec<HostSchedule>>>,
) -> UnboundedSender<CodexCommand> {
    let (sender, mut receiver) = mpsc::unbounded_channel();
    let scheduler = sender.clone();
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
            sync_host_conversations(&host_conversations, &store);
            sync_host_schedules(&host_schedules, &store);
            if store.archive_stale(now_unix() - 7 * 24 * 60 * 60) > 0 {
                diagnostics::record("conversation", "auto_archived_stale");
                let _ = store.save();
            }
            set_sessions(&weak, store.display_sessions());
            for schedule in store
                .schedules
                .iter()
                .filter(|item| item.status == "pending")
            {
                queue_schedule(
                    &scheduler,
                    schedule.due_at_unix.saturating_sub(now_unix()) as u64,
                    schedule.id.clone(),
                    schedule.conversation_id.clone(),
                    schedule.prompt.clone(),
                    schedule.model.clone().unwrap_or_default(),
                    schedule.reasoning_effort.clone().unwrap_or_default(),
                );
            }
            while let Some(command) = receiver.recv().await {
                match command {
                    CodexCommand::Connect {
                        repository,
                        executable,
                    } => {
                        diagnostics::record("codex", "connect_start");
                        set_codex_status(&weak, "Connecting to Codex...".into(), None);
                        match CodexSession::connect(&executable, &repository).await {
                            Ok(mut connected) => {
                                diagnostics::record("codex", "connect_ready");
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
                                if let Ok(usage) = session
                                    .as_mut()
                                    .expect("session was set")
                                    .usage_summary()
                                    .await
                                {
                                    set_usage(&weak, usage);
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
                                diagnostics::record("codex", "connect_failed");
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
                                diagnostics::record("conversation", "created");
                                selected_id = Some(id.clone());
                                set_selected_session(&weak, id.clone());
                                set_host_devices(&weak, &host_registry, &id);
                                store.merge_threads([(id, "New conversation".into())]);
                                let _ = store.set_model_options(
                                    selected_id.as_deref().unwrap_or_default(),
                                    Some(model),
                                    Some(effort),
                                );
                                let _ = store.save();
                                set_transcript(&weak, "New conversation ready".into());
                                refresh_sessions(current, &weak, &mut store, &host_conversations)
                                    .await;
                            }
                            Err(error) => set_codex_error(&weak, error.to_string()),
                        }
                    }
                    CodexCommand::RefreshSessions => {
                        let Some(current) = session.as_mut() else {
                            set_codex_error(&weak, "Connect Codex first.".into());
                            continue;
                        };
                        refresh_sessions(current, &weak, &mut store, &host_conversations).await;
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
                                set_selected_session(&weak, id.clone());
                                set_host_devices(&weak, &host_registry, &id);
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
                            diagnostics::record("conversation", "archived");
                            let _ = store.save();
                            set_sessions(&weak, visible_sessions(&store, showing_archived));
                        }
                    }
                    CodexCommand::RestoreSelected => {
                        if let Some(id) = selected_id.as_deref()
                            && store.set_archived(id, false)
                        {
                            diagnostics::record("conversation", "restored");
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
                        schedule_id,
                        conversation_id,
                    } => {
                        diagnostics::record("turn", "start");
                        let Some(current) = session.as_mut() else {
                            set_codex_error(&weak, "Connect Codex first.".into());
                            continue;
                        };
                        if let Some(target) = conversation_id.as_deref()
                            && selected_id.as_deref() != Some(target)
                        {
                            let target_model = store
                                .conversations
                                .iter()
                                .find(|conversation| conversation.id == target)
                                .and_then(|conversation| conversation.model.clone());
                            if current
                                .resume_thread(target, target_model.as_deref())
                                .await
                                .is_err()
                            {
                                set_codex_error(
                                    &weak,
                                    "Scheduled conversation could not be resumed.".into(),
                                );
                                continue;
                            }
                            selected_id = Some(target.to_owned());
                            set_selected_session(&weak, target.to_owned());
                            set_host_devices(&weak, &host_registry, target);
                        }
                        if let Some(id) = selected_id.as_deref()
                            && store.set_model_options(
                                id,
                                Some(model.clone()),
                                Some(effort.clone()),
                            )
                        {
                            let _ = store.save();
                        }
                        if let Some(schedule_id) = schedule_id.as_deref() {
                            let _ = store.set_schedule_status(schedule_id, "sent");
                            let _ = store.save();
                            sync_host_schedules(&host_schedules, &store);
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
                                if let Some(id) = selected_id.as_deref() {
                                    store.mark_activity(id, now_unix());
                                    let _ = store.save();
                                }
                                if let Ok(usage) = current.usage_summary().await {
                                    set_usage(&weak, usage);
                                }
                                refresh_sessions(current, &weak, &mut store, &host_conversations)
                                    .await;
                            }
                            Err(error) if error.to_string() == "Turn stopped by user" => {
                                diagnostics::record("turn", "stopped");
                                if let Some(schedule_id) = schedule_id.as_deref() {
                                    let _ = store.set_schedule_status(schedule_id, "failed");
                                    let _ = store.save();
                                    sync_host_schedules(&host_schedules, &store);
                                }
                                set_codex_status(&weak, "Codex is ready".into(), None);
                            }
                            Err(error) => {
                                diagnostics::record("turn", "failed");
                                if let Some(schedule_id) = schedule_id.as_deref() {
                                    let _ = store.set_schedule_status(schedule_id, "failed");
                                    let _ = store.save();
                                    sync_host_schedules(&host_schedules, &store);
                                }
                                set_codex_error(&weak, error.to_string());
                            }
                        }
                    }
                    CodexCommand::Schedule {
                        prompt,
                        model,
                        effort,
                        delay,
                    } => {
                        let Ok(delay) = delay.trim().parse::<u64>() else {
                            set_codex_error(&weak, "Schedule delay must be seconds.".into());
                            continue;
                        };
                        if prompt.trim().is_empty() || delay == 0 {
                            set_codex_error(&weak, "Enter a prompt and a positive delay.".into());
                            continue;
                        }
                        let Some(conversation_id) = selected_id.clone() else {
                            set_codex_error(
                                &weak,
                                "Select a conversation before scheduling.".into(),
                            );
                            continue;
                        };
                        let id = store.add_schedule(
                            conversation_id.clone(),
                            prompt.clone(),
                            now_unix() + delay as i64,
                            (!model.trim().is_empty()).then_some(model.clone()),
                            (!effort.trim().is_empty()).then_some(effort.clone()),
                        );
                        let _ = store.save();
                        sync_host_schedules(&host_schedules, &store);
                        queue_schedule(
                            &scheduler,
                            delay,
                            id,
                            conversation_id,
                            prompt,
                            model,
                            effort,
                        );
                        set_codex_status(&weak, "Prompt scheduled".into(), None);
                    }
                    CodexCommand::Host(action) => match action {
                        HostAction::Send {
                            conversation_id,
                            text,
                        } => {
                            let _ = scheduler.send(CodexCommand::Prompt {
                                prompt: text,
                                model: String::new(),
                                effort: String::new(),
                                schedule_id: None,
                                conversation_id: Some(conversation_id),
                            });
                        }
                        HostAction::Archive {
                            conversation_id,
                            archived,
                        } => {
                            if store.set_archived(&conversation_id, archived) {
                                let _ = store.save();
                                sync_host_conversations(&host_conversations, &store);
                                set_sessions(&weak, visible_sessions(&store, showing_archived));
                            }
                        }
                        HostAction::Pin {
                            conversation_id,
                            pinned,
                        } => {
                            if store.set_pinned(&conversation_id, pinned) {
                                let _ = store.save();
                                sync_host_conversations(&host_conversations, &store);
                                set_sessions(&weak, visible_sessions(&store, showing_archived));
                            }
                        }
                        HostAction::Rename {
                            conversation_id,
                            title,
                        } => {
                            if store.rename(&conversation_id, &title) {
                                let _ = store.save();
                                sync_host_conversations(&host_conversations, &store);
                                set_sessions(&weak, visible_sessions(&store, showing_archived));
                            }
                        }
                        HostAction::Schedule {
                            conversation_id,
                            text,
                            due_at_unix,
                            response,
                        } => {
                            let id = store.add_schedule(
                                conversation_id.clone(),
                                text.clone(),
                                due_at_unix,
                                None,
                                None,
                            );
                            let _ = store.save();
                            sync_host_schedules(&host_schedules, &store);
                            queue_schedule(
                                &scheduler,
                                due_at_unix.saturating_sub(now_unix()) as u64,
                                id.clone(),
                                conversation_id.clone(),
                                text.clone(),
                                String::new(),
                                String::new(),
                            );
                            let due_at =
                                chrono::DateTime::<chrono::Utc>::from_timestamp(due_at_unix, 0)
                                    .map(|value| value.to_rfc3339())
                                    .unwrap_or_default();
                            let _ = response.send(Ok(serde_json::json!({
                                "id": id,
                                "conversationId": conversation_id,
                                "text": text,
                                "dueAt": due_at,
                                "status": "pending"
                            })));
                        }
                        HostAction::CancelSchedule {
                            schedule_id,
                            response,
                        } => {
                            if store.set_schedule_status(&schedule_id, "canceled") {
                                let _ = store.save();
                                sync_host_schedules(&host_schedules, &store);
                                let _ = response.send(Ok(()));
                            } else {
                                let _ = response.send(Err("Schedule not found.".into()));
                            }
                        }
                        HostAction::Read {
                            conversation_id,
                            response,
                        } => {
                            let result = async {
                                let Some(current) = session.as_mut() else {
                                    return Err("Codex is disconnected.".to_owned());
                                };
                                if selected_id.as_deref() != Some(conversation_id.as_str()) {
                                    let model = store
                                        .conversations
                                        .iter()
                                        .find(|conversation| conversation.id == conversation_id)
                                        .and_then(|conversation| conversation.model.clone());
                                    current
                                        .resume_thread(&conversation_id, model.as_deref())
                                        .await
                                        .map_err(|error| error.to_string())?;
                                    selected_id = Some(conversation_id.clone());
                                }
                                let history = current
                                    .current_thread_text()
                                    .await
                                    .map_err(|error| error.to_string())?;
                                let Some(record) = store
                                    .conversations
                                    .iter()
                                    .find(|conversation| conversation.id == conversation_id)
                                else {
                                    return Err("Conversation not found.".to_owned());
                                };
                                Ok(serde_json::json!({
                                    "id": record.id,
                                    "title": record.title,
                                    "status": "idle",
                                    "activeTurnId": null,
                                    "items": [{
                                        "id": format!("history-{}", record.id),
                                        "type": "assistant",
                                        "text": history,
                                        "detail": "",
                                        "status": "completed"
                                    }],
                                    "requests": [],
                                    "error": null,
                                    "repository": std::path::Path::new(&store.repository_path)
                                        .file_name()
                                        .and_then(|name| name.to_str())
                                        .unwrap_or_default(),
                                    "workingDirectory": store.repository_path,
                                    "hostName": std::env::var("COMPUTERNAME").unwrap_or_default(),
                                    "permissionMode": "default",
                                    "sandboxMode": null,
                                    "approvalPolicy": null,
                                    "activeTurnStartedAt": null,
                                    "lastWorkDurationMs": null,
                                    "model": record.model,
                                    "archived": record.archived,
                                    "reasoningEffort": record.reasoning_effort,
                                    "historyTruncated": false,
                                    "historyBefore": null,
                                    "pinned": record.pinned,
                                    "lastMessageAt": null
                                }))
                            }
                            .await;
                            let _ = response.send(result);
                        }
                    },
                    CodexCommand::Stop => {}
                }
            }
        });
    });
    sender
}

fn queue_schedule(
    sender: &UnboundedSender<CodexCommand>,
    delay: u64,
    schedule_id: String,
    conversation_id: String,
    prompt: String,
    model: String,
    effort: String,
) {
    let sender = sender.clone();
    tokio::spawn(async move {
        tokio::time::sleep(std::time::Duration::from_secs(delay)).await;
        let _ = sender.send(CodexCommand::Prompt {
            prompt,
            model,
            effort,
            schedule_id: Some(schedule_id),
            conversation_id: Some(conversation_id),
        });
    });
}

async fn refresh_sessions(
    session: &mut CodexSession,
    weak: &slint::Weak<AppWindow>,
    store: &mut DesktopStore,
    host_conversations: &Arc<std::sync::RwLock<Vec<HostConversation>>>,
) {
    match session.list_threads().await {
        Ok(sessions) => {
            store.merge_threads(sessions);
            let _ = store.save();
            sync_host_conversations(host_conversations, store);
            set_sessions(weak, store.display_sessions());
        }
        Err(error) => set_codex_error(weak, error.to_string()),
    }
}

fn sync_host_conversations(
    snapshot: &Arc<std::sync::RwLock<Vec<HostConversation>>>,
    store: &DesktopStore,
) {
    let mut target = snapshot
        .write()
        .expect("host conversation lock should not be poisoned");
    *target = store
        .conversations
        .iter()
        .map(|conversation| HostConversation {
            id: conversation.id.clone(),
            title: conversation.title.clone(),
            archived: conversation.archived,
            pinned: conversation.pinned,
        })
        .collect();
}

fn sync_host_schedules(snapshot: &Arc<std::sync::RwLock<Vec<HostSchedule>>>, store: &DesktopStore) {
    let mut target = snapshot
        .write()
        .expect("host schedule lock should not be poisoned");
    *target = store
        .schedules
        .iter()
        .map(|schedule| HostSchedule {
            id: schedule.id.clone(),
            conversation_id: schedule.conversation_id.clone(),
            text: schedule.prompt.clone(),
            due_at: chrono::DateTime::<chrono::Utc>::from_timestamp(schedule.due_at_unix, 0)
                .map(|value| value.to_rfc3339())
                .unwrap_or_default(),
            status: schedule.status.clone(),
        })
        .collect();
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

fn relay_config_path() -> PathBuf {
    if let Ok(local_app_data) = env::var("LOCALAPPDATA") {
        return PathBuf::from(local_app_data).join("PocketAgent-rust/relay.json");
    }
    if let Ok(data_home) = env::var("XDG_DATA_HOME") {
        return PathBuf::from(data_home).join("PocketAgent-rust/relay.json");
    }
    env::current_dir()
        .unwrap_or_else(|_| PathBuf::from("."))
        .join(".pocketagent-rust/relay.json")
}

fn load_relay_config() -> Result<RelayConfig, Box<dyn std::error::Error + Send + Sync>> {
    let path = relay_config_path();
    if let Ok(contents) = fs::read_to_string(&path)
        && let Ok(config) = serde_json::from_str(&contents)
    {
        return Ok(config);
    }
    let config = RelayConfig::new();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::write(path, serde_json::to_vec_pretty(&config)?)?;
    Ok(config)
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

fn set_host_devices(
    weak: &slint::Weak<AppWindow>,
    registry: &DeviceRegistry,
    conversation_id: &str,
) {
    let items = registry
        .device_access(conversation_id)
        .into_iter()
        .map(|device| HostDeviceItem {
            id: device.id.to_string().into(),
            name: device.name.into(),
            authorized: device.authorized,
        })
        .collect::<Vec<_>>();
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_host_devices(slint::ModelRc::new(slint::VecModel::from(items)));
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

fn set_usage(weak: &slint::Weak<AppWindow>, usage: String) {
    let _ = weak.upgrade_in_event_loop(move |window| window.set_usage_summary(usage.into()));
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
        window.set_relay_endpoint(format!("ws://{endpoint}  |  host {host_id}").into());
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

fn start_host_server(state: HostHttpState) -> String {
    let requested = env::var("POCKETAGENT_HOST_BIND").unwrap_or_else(|_| "127.0.0.1:8788".into());
    if requested
        .parse::<std::net::SocketAddr>()
        .map(|address| !address.ip().is_loopback())
        .unwrap_or(false)
        && env::var("POCKETAGENT_HOST_ALLOW_INSECURE_REMOTE").as_deref() != Ok("1")
    {
        diagnostics::record("host", "remote_bind_rejected_without_tls");
        return "Host API unavailable: non-loopback HTTP requires explicit insecure opt-in".into();
    }
    let listener = match std::net::TcpListener::bind(&requested) {
        Ok(listener) => listener,
        Err(error) => {
            diagnostics::record("host", "bind_failed");
            return format!("Host API unavailable: {error}");
        }
    };
    let _ = listener.set_nonblocking(true);
    let endpoint = listener
        .local_addr()
        .map(|address| format!("http://{address}"))
        .unwrap_or_else(|_| format!("http://{requested}"));
    let display_endpoint = endpoint.clone();
    std::thread::spawn(move || {
        let Ok(runtime) = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
        else {
            diagnostics::record("host", "runtime_failed");
            return;
        };
        runtime.block_on(async move {
            let Ok(listener) = TcpListener::from_std(listener) else {
                diagnostics::record("host", "listener_failed");
                return;
            };
            diagnostics::record("host", "ready");
            if let Err(error) = axum::serve(listener, host_router(state)).await {
                diagnostics::record("host", &format!("stopped_{error}"));
            }
        });
    });
    display_endpoint
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
                current.push_str("* ");
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
                    text: "---".into(),
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
        assert_eq!(blocks[1].text, "* one");
        assert_eq!(blocks[2].text, "* two");
        assert_eq!(blocks[3].kind, "code");
    }
}
