#![cfg_attr(all(windows, not(test)), windows_subsystem = "windows")]

mod codex;
mod diagnostics;
mod store;

use axum_server::tls_rustls::{RustlsAcceptor, RustlsConfig};
use base64::{Engine as _, engine::general_purpose::STANDARD as BASE64};
use codex::{
    CodexSession, InteractionState, PromptControl, PromptRequest, SharedInteraction, SteerRequest,
};
use pocketagent_crypto::SessionKey;
use pocketagent_host::{
    DeviceRegistry, HostAction, HostConversation, HostHttpState, HostModel, HostSchedule,
    HostUsage, SessionInfo, router as host_router,
};
use pocketagent_relay::RelayNode;
use pocketagent_relay::application::RelayApplicationHost;
use pocketagent_relay::config::RelayConfig;
use pocketagent_relay::transport::{RelayAuthRegistry, RelayTransportServer};
use pulldown_cmark::{Event, HeadingLevel, Options, Parser, Tag, TagEnd};
use qrcode::QrCode;
use rcgen::{CertificateParams, ExtendedKeyUsagePurpose, IsCa, KeyPair, PKCS_ECDSA_P256_SHA256};
use rustls::{
    ServerConfig,
    pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer},
};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashSet, VecDeque},
    env, fs,
    path::PathBuf,
    sync::{Arc, Mutex},
};
use store::{ConversationRecord, DesktopStore, now_unix};
use tokio::sync::mpsc::{self, UnboundedSender};
use tokio::{
    net::TcpListener,
    sync::{oneshot, watch},
};
use tower::ServiceExt;
use uuid::Uuid;

slint::include_modules!();

const CODEX_DEFAULT_MODEL_LABEL: &str = "Use Codex settings";

#[derive(Clone, Copy, Default)]
enum PendingFirewallAction {
    #[default]
    None,
    StartHost,
    PairDevice,
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    diagnostics::record("startup", "desktop_launch");
    // axum-server enables both rustls providers through its dependency graph;
    // select the provider used by its TLS acceptor before any server config is
    // built. Without this, host startup panics and leaves the UI falsely
    // claiming that the API is running.
    let _ = rustls::crypto::aws_lc_rs::default_provider().install_default();
    // FemtoVG's glyph atlas can bilinearly sample text at fractional
    // coordinates on Windows, producing the uneven/wavy strokes seen in
    // small light labels. The software renderer uses system fonts directly
    // and keeps glyphs on the pixel grid for stable small-control text.
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .select()?;
    let window = AppWindow::new()?;
    {
        let drafts = std::cell::RefCell::new(std::collections::HashMap::new());
        let weak = window.as_weak();
        window.on_switch_draft(move |id| {
            let Some(window) = weak.upgrade() else { return };
            let mut drafts = drafts.borrow_mut();
            let previous = window.get_current_session_id();
            if previous != id {
                drafts.insert(previous, window.get_composer_draft());
                window.set_composer_draft(drafts.remove(&id).unwrap_or_default());
                window.set_current_session_id(id);
                window.set_submission_warning("".into());
            }
        });
    }
    {
        let weak = window.as_weak();
        window.on_browse_folders(move |folder| {
            let Some(window) = weak.upgrade() else { return };
            match folder_choices(std::path::Path::new(folder.as_str())) {
                Ok(choices) => {
                    window.set_folder_choices(slint::ModelRc::new(slint::VecModel::from(choices)));
                    window.set_submission_warning("".into());
                }
                Err(error) => window
                    .set_submission_warning(format!("Could not browse folder: {error}").into()),
            }
        });
    }
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
    window.set_server_name(
        env::var("COMPUTERNAME")
            .or_else(|_| env::var("HOSTNAME"))
            .unwrap_or_else(|_| "Desktop".into())
            .into(),
    );

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
    let host_repository_name = std::path::Path::new(&repository)
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or_default()
        .to_owned();
    let host_state = HostHttpState::new(host_state_registry, host_actions, move |identity| {
        SessionInfo {
            authenticated: identity.is_some(),
            device_name: identity.map(|item| item.name.clone()),
            repository: Some(host_repository_name.clone()),
            host_name: std::env::var("COMPUTERNAME").unwrap_or_else(|_| "desktop".into()),
            version: env!("CARGO_PKG_VERSION").into(),
        }
    })
    .with_request_log(|event| diagnostics::record("host_request", event));
    let host_models = host_state.models.clone();
    let host_usage = host_state.usage.clone();
    let host_provider_status = host_state.provider_status.clone();
    let host_active_turn = host_state.active_turn.clone();
    let host_active_turn_started_at_ms = host_state.active_turn_started_at_ms.clone();
    let host_last_work_duration_ms = host_state.last_work_duration_ms.clone();
    let host_snapshots = host_state.snapshots.clone();
    *host_state
        .image_root
        .write()
        .expect("host image-root lock should not be poisoned") = repository.clone();
    host_state
        .conversations
        .write()
        .expect("host conversation lock should not be poisoned")
        .clear();
    let host_shutdown: Arc<Mutex<Option<oneshot::Sender<()>>>> = Arc::new(Mutex::new(None));
    let pending_firewall_action = Arc::new(Mutex::new(PendingFirewallAction::None));
    if let Ok(address) = configured_host_address() {
        window.set_firewall_dialog_port(address.port().to_string().into());
    }
    let host_endpoint = start_host_server(host_state.clone(), host_shutdown.clone());
    let relay_host_shutdown: Arc<Mutex<Option<oneshot::Sender<()>>>> = Arc::new(Mutex::new(None));
    start_relay_host(host_state.clone(), relay_host_shutdown.clone());
    match &host_endpoint {
        Ok(endpoint) => {
            window.set_host_status("Host API is running".into());
            window.set_host_endpoint(endpoint.clone().into());
        }
        Err(error) => {
            window.set_host_status(format!("Host API unavailable: {error}").into());
            window.set_host_endpoint("No endpoint".into());
        }
    }
    if let Ok(endpoint) = &host_endpoint
        && let Some(host) = endpoint_host(endpoint)
        && let Ok(identity) = load_host_tls_identity_for_host(Some(host))
    {
        let payload = pairing_payload(&host_pairing, endpoint, &identity.spki_hex);
        window.set_host_pairing(payload.clone().into());
        if let Ok(qr) = pairing_qr(&payload) {
            window.set_host_qr(qr);
        }
    }
    {
        let host_state = host_state.clone();
        let host_shutdown = host_shutdown.clone();
        let pending_firewall_action = pending_firewall_action.clone();
        let weak = window.as_weak();
        window.on_toggle_host(move || {
            let is_running = host_shutdown
                .lock()
                .map(|guard| guard.is_some())
                .unwrap_or(false);
            if is_running {
                if let Ok(mut guard) = host_shutdown.lock()
                    && let Some(shutdown) = guard.take()
                {
                    let _ = shutdown.send(());
                }
                diagnostics::record("host", "stopping_from_ui");
                if let Some(window) = weak.upgrade() {
                    window.set_host_status("Host API is stopped".into());
                    window.set_host_endpoint("No endpoint".into());
                }
            } else {
                if host_firewall_required() {
                    if let Ok(mut pending) = pending_firewall_action.lock() {
                        *pending = PendingFirewallAction::StartHost;
                    }
                    if let Some(window) = weak.upgrade() {
                        window.set_firewall_dialog_open(true);
                    }
                    return;
                }
                match start_host_server(host_state.clone(), host_shutdown.clone()) {
                    Ok(endpoint) => {
                        diagnostics::record("host", "started_from_ui");
                        if let Some(window) = weak.upgrade() {
                            window.set_host_status("Host API is running".into());
                            window.set_host_endpoint(endpoint.into());
                        }
                    }
                    Err(error) => {
                        diagnostics::record("host", "start_from_ui_failed");
                        if let Some(window) = weak.upgrade() {
                            window.set_host_status(format!("Host API unavailable: {error}").into());
                            window.set_host_endpoint("No endpoint".into());
                        }
                    }
                }
            }
        });
    }
    {
        let pending_firewall_action = pending_firewall_action.clone();
        let weak = window.as_weak();
        window.on_request_pairing(move || {
            let Some(window) = weak.upgrade() else { return };
            if window.get_pairing_open() {
                window.set_pairing_open(false);
                return;
            }
            if host_firewall_required() {
                if let Ok(mut pending) = pending_firewall_action.lock() {
                    *pending = PendingFirewallAction::PairDevice;
                }
                window.set_firewall_dialog_open(true);
            } else {
                window.set_pairing_open(true);
            }
        });
    }
    {
        let host_state = host_state.clone();
        let host_shutdown = host_shutdown.clone();
        let pending_firewall_action = pending_firewall_action.clone();
        let weak = window.as_weak();
        window.on_approve_firewall_access(move || {
            let action = pending_firewall_action
                .lock()
                .map(|mut pending| std::mem::take(&mut *pending))
                .unwrap_or_default();
            if let Some(window) = weak.upgrade() {
                window.set_firewall_dialog_open(false);
            }
            let result = configured_host_address().and_then(ensure_host_firewall_rule);
            match (action, result) {
                (PendingFirewallAction::PairDevice, Ok(())) => {
                    if let Some(window) = weak.upgrade() {
                        window.set_pairing_open(true);
                    }
                }
                (PendingFirewallAction::StartHost, Ok(())) => {
                    match start_host_server(host_state.clone(), host_shutdown.clone()) {
                        Ok(endpoint) => {
                            diagnostics::record("host", "started_from_ui");
                            if let Some(window) = weak.upgrade() {
                                window.set_host_status("Host API is running".into());
                                window.set_host_endpoint(endpoint.into());
                            }
                        }
                        Err(error) => {
                            diagnostics::record("host", "start_from_ui_failed");
                            if let Some(window) = weak.upgrade() {
                                window.set_host_status(
                                    format!("Host API unavailable: {error}").into(),
                                );
                                window.set_host_endpoint("No endpoint".into());
                            }
                        }
                    }
                }
                (_, Err(error)) => {
                    diagnostics::record("host", "firewall_setup_failed");
                    if let Some(window) = weak.upgrade() {
                        window.set_host_status(format!("Firewall setup failed: {error}").into());
                    }
                }
                (PendingFirewallAction::None, Ok(())) => {}
            }
        });
    }
    {
        let pending_firewall_action = pending_firewall_action.clone();
        let weak = window.as_weak();
        window.on_cancel_firewall_access(move || {
            if let Ok(mut pending) = pending_firewall_action.lock() {
                *pending = PendingFirewallAction::None;
            }
            if let Some(window) = weak.upgrade() {
                window.set_firewall_dialog_open(false);
            }
        });
    }
    {
        let weak = window.as_weak();
        window.on_run_host_diagnostics(move || {
            let weak = weak.clone();
            std::thread::spawn(move || {
                diagnostics::record("host", "diagnostics_started");
                let report = host_diagnostics_report();
                diagnostics::record("host", "diagnostics_completed");
                let _ = weak.upgrade_in_event_loop(move |window| {
                    window.set_host_diagnostics(report.into());
                });
            });
        });
    }
    let codex_commands = start_codex_worker(
        window.as_weak(),
        interaction.clone(),
        HostSharedState {
            conversations: host_conversations.clone(),
            schedules: host_schedules.clone(),
            registry: host_registry.clone(),
            models: host_models,
            usage: host_usage,
            provider_status: host_provider_status,
            active_turn: host_active_turn,
            active_turn_started_at_ms: host_active_turn_started_at_ms,
            last_work_duration_ms: host_last_work_duration_ms,
            snapshots: host_snapshots,
        },
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
                diagnostics::record("device", "access_change_invalid_device_id");
                return;
            };
            let result = if authorized {
                registry.grant_conversation(conversation_id.to_string(), device_id)
            } else {
                registry.revoke_conversation(conversation_id.as_str(), device_id);
                Ok(())
            };
            match result {
                Ok(()) => diagnostics::record(
                    "device",
                    &format!(
                        "access_{}_conversation={conversation_id} device={device_id}",
                        if authorized { "granted" } else { "revoked" }
                    ),
                ),
                Err(error) => {
                    diagnostics::record("device", &format!("access_change_failed_{error}"));
                    let _ = weak.upgrade_in_event_loop(move |window| {
                        window.set_host_status(format!("Device access failed: {error}").into());
                    });
                    return;
                }
            }
            set_host_devices(&weak, &registry, conversation_id.as_str());
        });
    }
    {
        let registry = host_registry.clone();
        let weak = window.as_weak();
        window.on_revoke_device(move |device_id| {
            let Ok(device_id) = uuid::Uuid::parse_str(device_id.as_str()) else {
                return;
            };
            if registry.revoke(device_id) {
                diagnostics::record("device", "revoked");
                set_host_status(
                    &weak,
                    "Device revoked; its token is no longer valid.".into(),
                );
                refresh_host_devices_for_selection(&weak, &registry);
            }
        });
    }
    {
        let registry = host_registry.clone();
        let weak = window.as_weak();
        let pairing_origin = host_endpoint.clone().ok();
        let pairing_spki = pairing_origin
            .as_deref()
            .and_then(endpoint_host)
            .and_then(|host| load_host_tls_identity_for_host(Some(host)).ok())
            .map(|identity| identity.spki_hex);
        window.on_renew_pairing(move || {
            let pairing = registry.renew_pairing();
            diagnostics::record("pairing", "renewed");
            let Some(endpoint) = pairing_origin.clone() else {
                return;
            };
            let Some(spki) = pairing_spki.clone() else {
                return;
            };
            let payload = pairing_payload(&pairing, &endpoint, &spki);
            let _ = weak.upgrade_in_event_loop(move |window| {
                window.set_host_pairing(payload.clone().into());
                if let Ok(qr) = pairing_qr(&payload) {
                    window.set_host_qr(qr);
                }
            });
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
        window.on_new_conversation(move |model, effort, working_directory| {
            let _ = commands.send(CodexCommand::NewConversation {
                model: normalize_model_selection(model.as_str()),
                effort: effort.to_string(),
                working_directory: working_directory.to_string(),
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
        let registry = host_registry.clone();
        let weak = window.as_weak();
        window.on_navigate(move |page| {
            let page = page.to_string();
            let registry = registry.clone();
            let _ = weak.upgrade_in_event_loop(move |window| {
                window.set_current_page(page.clone().into());
                if page == "devices" {
                    let conversation_id = window.get_current_session_id().to_string();
                    let items = registry
                        .device_access(&conversation_id)
                        .into_iter()
                        .map(|device| HostDeviceItem {
                            id: device.id.to_string().into(),
                            name: device.name.into(),
                            authorized: device.authorized,
                        })
                        .collect::<Vec<_>>();
                    window.set_host_devices(slint::ModelRc::new(slint::VecModel::from(items)));
                }
            });
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
            model: normalize_model_selection(model.as_str()),
            effort: effort.to_string(),
            schedule_id: None,
            conversation_id: None,
            expected_turn_id: None,
        });
    });
    {
        let commands = startup_commands.clone();
        window.on_schedule_prompt(move |prompt, model, effort, delay| {
            let _ = commands.send(CodexCommand::Schedule {
                prompt: prompt.to_string(),
                model: normalize_model_selection(model.as_str()),
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
    {
        let commands = startup_commands.clone();
        window.on_cancel_schedule(move |id| {
            let _ = commands.send(CodexCommand::CancelSchedule(id.to_string()));
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
                    set_relay_running(&weak, address.to_string(), &config);
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
    {
        let weak = window.as_weak();
        let state = host_state.clone();
        let relay_host_shutdown = Arc::clone(&relay_host_shutdown);
        window.on_connect_relay_host(move |url, host_id, token, session_key| {
            connect_relay_host_from_ui(
                state.clone(),
                weak.clone(),
                relay_host_shutdown.clone(),
                url.to_string(),
                host_id.to_string(),
                token.to_string(),
                session_key.to_string(),
            );
        });
    }
    {
        let weak = window.as_weak();
        let relay_host_shutdown = Arc::clone(&relay_host_shutdown);
        window.on_disconnect_relay_host(move || {
            if let Some(shutdown) = relay_host_shutdown
                .lock()
                .expect("relay host state lock should work")
                .take()
            {
                let _ = shutdown.send(());
            }
            set_relay_host_stopped(&weak);
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
        working_directory: String,
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
        expected_turn_id: Option<String>,
    },
    Schedule {
        prompt: String,
        model: String,
        effort: String,
        delay: String,
    },
    CancelSchedule(String),
    Host(HostAction),
    Stop,
}

#[derive(Clone)]
struct HostSharedState {
    conversations: Arc<std::sync::RwLock<Vec<HostConversation>>>,
    schedules: Arc<std::sync::RwLock<Vec<HostSchedule>>>,
    registry: DeviceRegistry,
    models: Arc<std::sync::RwLock<Vec<HostModel>>>,
    usage: Arc<std::sync::RwLock<HostUsage>>,
    provider_status: Arc<std::sync::RwLock<String>>,
    active_turn: Arc<std::sync::RwLock<Option<(String, String)>>>,
    active_turn_started_at_ms: Arc<std::sync::RwLock<Option<i64>>>,
    last_work_duration_ms: Arc<std::sync::RwLock<std::collections::HashMap<String, i64>>>,
    snapshots: Arc<std::sync::RwLock<std::collections::HashMap<String, serde_json::Value>>>,
}

fn start_codex_worker(
    weak: slint::Weak<AppWindow>,
    interaction: SharedInteraction,
    host: HostSharedState,
) -> UnboundedSender<CodexCommand> {
    let HostSharedState {
        conversations: host_conversations,
        schedules: host_schedules,
        registry: host_registry,
        models: host_models,
        usage: host_usage,
        provider_status: host_provider_status,
        active_turn: host_active_turn,
        active_turn_started_at_ms: host_active_turn_started_at_ms,
        last_work_duration_ms: host_last_work_duration_ms,
        snapshots: host_snapshots,
    } = host;
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
            let mut read_only_sessions = HashSet::<String>::new();
            let mut showing_archived = false;
            let mut deferred_commands = VecDeque::new();
            let mut archive_timer = tokio::time::interval(std::time::Duration::from_secs(600));
            archive_timer.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
            archive_timer.tick().await;
            let mut mirror_timer = tokio::time::interval(std::time::Duration::from_secs(3));
            mirror_timer.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
            mirror_timer.tick().await;
            if store.archive_stale(now_unix() - 7 * 24 * 60 * 60) > 0 {
                diagnostics::record("conversation", "auto_archived_stale");
                let _ = store.save();
            }
            sync_host_conversations(&host_conversations, &store);
            sync_host_schedules(&host_schedules, &store);
            set_scheduled_items(&weak, &store);
            set_sessions(&weak, store.display_sessions());
            for schedule in store
                .schedules
                .iter()
                .filter(|item| schedule_pending(&store, &item.id))
            {
                queue_schedule(
                    &scheduler,
                    host_schedules.clone(),
                    schedule.due_at_unix.saturating_sub(now_unix()) as u64,
                    QueuedSchedule {
                        id: schedule.id.clone(),
                        conversation_id: schedule.conversation_id.clone(),
                        prompt: schedule.prompt.clone(),
                        model: schedule.model.clone().unwrap_or_default(),
                        effort: schedule.reasoning_effort.clone().unwrap_or_default(),
                    },
                );
            }
            loop {
                let command = if let Some(command) = deferred_commands.pop_front() {
                    Some(command)
                } else {
                    tokio::select! {
                        _ = archive_timer.tick() => {
                            if store.archive_stale(now_unix() - 7 * 24 * 60 * 60) > 0 {
                                diagnostics::record("conversation", "auto_archived_stale");
                                let _ = store.save();
                                sync_host_conversations(&host_conversations, &store);
                                set_sessions(&weak, visible_sessions(&store, showing_archived));
                            }
                            continue;
                        }
                        _ = mirror_timer.tick() => {
                            let Some(id) = selected_id.clone() else { continue };
                            if !read_only_sessions.contains(&id) {
                                continue;
                            }
                            if let Some(current) = session.as_mut()
                                && let Ok(history) = current.read_thread(&id).await
                            {
                                set_transcript(&weak, history);
                            }
                            continue;
                        }
                        command = receiver.recv() => command,
                    }
                };
                let Some(command) = command else { break };
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
                                read_only_sessions.clear();
                                set_session_read_only(&weak, false);
                                let sessions = connected.list_threads().await;
                                store.repository_path = repository;
                                store.codex_executable = executable;
                                for conversation in &mut store.conversations {
                                    if conversation.working_directory.is_empty() {
                                        conversation.working_directory = store.repository_path.clone();
                                    }
                                }
                                session = Some(connected);
                                *host_provider_status
                                    .write()
                                    .expect("provider status lock should not be poisoned") = "ready".into();
                                set_codex_status(&weak, "Codex is ready".into(), None);
                                if let Ok(models) = session
                                    .as_mut()
                                    .expect("session was set")
                                    .list_models()
                                    .await
                                {
                                    let model_text = models.entries.join("\n");
                                    *host_models
                                        .write()
                                        .expect("model lock should not be poisoned") = models.api;
                                    set_model_catalog(&weak, model_text);
                                    // A provider catalog default is informational only. It is
                                    // never an implicit per-conversation override.
                                    set_selected_model_options(&weak, None, None);
                                }
                                if let Ok(usage) = session
                                    .as_mut()
                                    .expect("session was set")
                                    .usage_status()
                                    .await
                                {
                                    *host_usage
                                        .write()
                                        .expect("usage lock should not be poisoned") = usage.api;
                                    set_usage(&weak, usage.summary);
                                }
                                match sessions {
                                    Ok(sessions) => {
                                        store.refresh_registered_threads(sessions);
                                        let _ = store.save();
                                        sync_host_conversations(&host_conversations, &store);
                                        set_sessions(&weak, store.display_sessions());
                                    }
                                    Err(error) => set_codex_error(&weak, error.to_string()),
                                }
                            }
                            Err(error) => {
                                diagnostics::record("codex", "connect_failed");
                                session = None;
                                *host_provider_status
                                    .write()
                                    .expect("provider status lock should not be poisoned") = "disconnected".into();
                                set_codex_error(&weak, error.to_string());
                            }
                        }
                    }
                    CodexCommand::NewConversation {
                        model,
                        effort,
                        working_directory,
                    } => {
                        let Some(current) = session.as_mut() else {
                            set_codex_error(&weak, "Connect Codex first.".into());
                            continue;
                        };
                        let working_directory = if working_directory.trim().is_empty() {
                            store.repository_path.clone()
                        } else {
                            working_directory.trim().to_owned()
                        };
                        if !std::path::Path::new(&working_directory).is_dir() {
                            set_codex_error(&weak, "Working folder does not exist.".into());
                            continue;
                        }
                        let model = normalize_model_selection(model.as_str());
                        current.set_repository(working_directory.clone());
                        match current
                            .new_thread((!model.trim().is_empty()).then_some(model.as_str()))
                            .await
                        {
                            Ok(id) => {
                                diagnostics::record("conversation", "created");
                                read_only_sessions.remove(&id);
                                selected_id = Some(id.clone());
                                set_session_read_only(&weak, false);
                                set_selected_session(&weak, id.clone());
                                let _ = weak.upgrade_in_event_loop(|window| window.set_creating_session(false));
                                set_current_working_directory(&weak, working_directory.clone());
                                set_host_devices(&weak, &host_registry, &id);
                                let title = std::path::Path::new(&working_directory)
                                    .file_name()
                                    .and_then(|name| name.to_str())
                                    .filter(|name| !name.is_empty())
                                    .unwrap_or("New conversation")
                                    .to_owned();
                                store.register_thread(id.clone(), title);
                                if let Some(conversation) = store
                                    .conversations
                                    .iter_mut()
                                    .find(|conversation| conversation.id == id)
                                {
                                    conversation.working_directory = working_directory;
                                }
                                set_selected_model_options(
                                    &weak,
                                    (!model.trim().is_empty()).then_some(model.clone()),
                                    (!effort.trim().is_empty()).then_some(effort.clone()),
                                );
                                let _ = store.set_model_options(
                                    selected_id.as_deref().unwrap_or_default(),
                                    Some(model),
                                    Some(effort),
                                );
                                let _ = store.save();
                                if let Some(record) = store
                                    .conversations
                                    .iter()
                                    .find(|conversation| conversation.id == id)
                                {
                                    let snapshot =
                                        host_snapshot(record, "", &store.repository_path, None);
                                    host_snapshots
                                        .write()
                                        .expect("snapshot lock should not be poisoned")
                                        .insert(id.clone(), snapshot);
                                }
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
                        let (options, reasoning_effort) = store
                            .conversations
                            .iter()
                            .find(|conversation| conversation.id == id)
                            .map(|conversation| {
                                (
                                    conversation.model.clone().unwrap_or_default(),
                                    conversation.reasoning_effort.clone().unwrap_or_default(),
                                )
                            })
                            .unwrap_or_default();
                        let working_directory = store
                            .conversations
                            .iter()
                            .find(|conversation| conversation.id == id)
                            .map(|conversation| conversation.working_directory.clone())
                            .filter(|directory| !directory.is_empty())
                            .unwrap_or_else(|| store.repository_path.clone());
                        // Selection and local conversation actions must not depend on
                        // Codex being able to hydrate the remote rollout history.
                        // A missing rollout is recoverable for archive/restore/rename.
                        selected_id = Some(id.clone());
                        set_selected_session(&weak, id.clone());
                        set_current_working_directory(&weak, working_directory.clone());
                        set_selected_model_options(
                            &weak,
                            (!options.trim().is_empty()).then_some(options.clone()),
                            (!reasoning_effort.trim().is_empty()).then_some(reasoning_effort.clone()),
                        );
                        set_host_devices(&weak, &host_registry, &id);
                        set_session_read_only(&weak, false);
                        current.set_repository(working_directory.clone());
                        match current
                            .resume_thread(
                                &id,
                                (!options.trim().is_empty()).then_some(options.as_str()),
                            )
                            .await
                        {
                            Ok(history) => {
                                read_only_sessions.remove(&id);
                                set_session_read_only(&weak, false);
                                if let Some(record) = store
                                    .conversations
                                    .iter()
                                    .find(|conversation| conversation.id == id)
                                {
                                    let mut snapshot =
                                        host_snapshot(record, &history, &store.repository_path, None);
                                    let duration = host_last_work_duration_ms
                                        .read()
                                        .expect("work duration lock should not be poisoned")
                                        .get(&id)
                                        .copied();
                                    apply_snapshot_timing(&mut snapshot, None, duration);
                                    host_snapshots
                                        .write()
                                        .expect("snapshot lock should not be poisoned")
                                        .insert(id.clone(), snapshot);
                                }
                                set_transcript(
                                    &weak,
                                    if history.is_empty() {
                                        "Conversation loaded".into()
                                    } else {
                                        history
                                    },
                                )
                            }
                            Err(resume_error) => {
                                diagnostics::record(
                                    "conversation",
                                    &format!("resume_failed {resume_error}"),
                                );
                                match current.read_thread(&id).await {
                                Ok(history) => {
                                    // A read-only thread/read succeeds even when another
                                    // client owns the active writer lease.
                                    current.clear_thread();
                                    read_only_sessions.insert(id.clone());
                                    set_session_read_only(&weak, true);
                                    set_codex_status(
                                        &weak,
                                        "Read-only mirror (another client is writing)".into(),
                                        None,
                                    );
                                    set_transcript(
                                        &weak,
                                        if history.is_empty() {
                                            "Conversation is being mirrored; no messages yet.".into()
                                        } else {
                                            history
                                        },
                                    );
                                }
                                Err(read_error) => {
                                    diagnostics::record(
                                        "conversation",
                                        &format!("read_mirror_failed {read_error}"),
                                    );
                                    // Do not leave the previous thread attached after a
                                    // failed resume; later prompts must not target it.
                                    current.clear_thread();
                                    read_only_sessions.insert(id.clone());
                                    set_session_read_only(&weak, true);
                                    set_codex_error(
                                        &weak,
                                        format!(
                                            "Conversation could not be loaded for editing or mirroring. Resume: {resume_error}; read-only: {read_error}"
                                        ),
                                    );
                                    set_transcript(
                                        &weak,
                                        "Conversation history is unavailable. You can still archive, restore, or rename this conversation.".into(),
                                    );
                                }
                                }
                            }
                        }
                    }
                    CodexCommand::ArchiveSelected => {
                        let active = host_active_turn
                            .read()
                            .expect("active turn lock should not be poisoned")
                            .as_ref()
                            .is_some_and(|(conversation_id, _)| {
                                Some(conversation_id.as_str()) == selected_id.as_deref()
                            });
                        if active {
                            set_codex_error(
                                &weak,
                                "Finish or stop active work before archiving this conversation."
                                    .into(),
                            );
                        } else if let Some(id) = selected_id.as_deref()
                            && store.set_archived(id, true)
                        {
                            diagnostics::record("conversation", "archived");
                            let _ = store.save();
                            sync_host_conversations(&host_conversations, &store);
                            sync_host_schedules(&host_schedules, &store);
                            set_scheduled_items(&weak, &store);
                            clear_selected_conversation(&mut session, &mut selected_id, &weak);
                            set_sessions(&weak, visible_sessions(&store, showing_archived));
                        }
                    }
                    CodexCommand::RestoreSelected => {
                        if let Some(id) = selected_id.as_deref()
                            && store.set_archived(id, false)
                        {
                            diagnostics::record("conversation", "restored");
                            let _ = store.save();
                            sync_host_conversations(&host_conversations, &store);
                            sync_host_schedules(&host_schedules, &store);
                            set_scheduled_items(&weak, &store);
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
                        expected_turn_id: _expected_turn_id,
                    } => {
                        let model = normalize_model_selection(model.as_str());
                        if let Some(schedule_id) = schedule_id.as_deref()
                            && !schedule_pending(&store, schedule_id)
                        {
                            continue;
                        }
                        let target_id = conversation_id.as_deref().or(selected_id.as_deref());
                        if is_read_only_session(&read_only_sessions, target_id) {
                            set_codex_error(
                                &weak,
                                "This conversation is read-only while another client is writing."
                                    .into(),
                            );
                            continue;
                        }
                        let from_desktop = schedule_id.is_none() && conversation_id.is_none();
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
                            let target_directory = store
                                .conversations
                                .iter()
                                .find(|conversation| conversation.id == target)
                                .map(|conversation| conversation.working_directory.clone())
                                .filter(|directory| !directory.is_empty())
                                .unwrap_or_else(|| store.repository_path.clone());
                            current.set_repository(target_directory.clone());
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
                            set_current_working_directory(&weak, target_directory);
                            set_host_devices(&weak, &host_registry, target);
                        }
                        let turn_is_active = host_active_turn
                            .read()
                            .expect("active turn lock should not be poisoned")
                            .as_ref()
                            .is_some_and(|(active_id, _)| {
                                Some(active_id.as_str()) == selected_id.as_deref()
                            });
                        if !turn_is_active
                            && let Some(id) = selected_id.as_deref()
                            && store.set_model_options(
                                id,
                                Some(model.clone()),
                                Some(effort.clone()),
                            )
                        {
                            let _ = store.save();
                        }
                        if let Ok(usage) = current.usage_status().await {
                            *host_usage
                                .write()
                                .expect("usage lock should not be poisoned") = usage.api;
                            set_usage(&weak, usage.summary.clone());
                            if usage.exhausted {
                                let due_at = usage
                                    .next_reset_at_unix
                                    .unwrap_or_else(|| now_unix().saturating_add(5 * 60));
                                let delay = due_at
                                    .saturating_sub(now_unix())
                                    .max(1) as u64;
                                let queued_until_reset = can_queue_usage_exhausted(
                                    schedule_id.as_deref(),
                                    selected_id.as_deref(),
                                );
                                if let Some(schedule_id) = schedule_id.clone() {
                                    let _ = store.set_schedule_status(&schedule_id, "pending");
                                    let _ = store.save();
                                    sync_host_schedules(&host_schedules, &store);
                                    set_scheduled_items(&weak, &store);
                                    queue_schedule(
                                        &scheduler,
                                        host_schedules.clone(),
                                        delay,
                                        QueuedSchedule {
                                            id: schedule_id,
                                            conversation_id: selected_id.clone().unwrap_or_default(),
                                            prompt: prompt.clone(),
                                            model: model.clone(),
                                            effort: effort.clone(),
                                        },
                                    );
                                    set_codex_status(
                                        &weak,
                                        "Prompt waiting for usage reset".into(),
                                        None,
                                    );
                                } else if let Some(conversation_id) = selected_id.clone() {
                                    let id = store.add_schedule(
                                        conversation_id.clone(),
                                        prompt.clone(),
                                        due_at,
                                        (!model.trim().is_empty()).then_some(model.clone()),
                                        (!effort.trim().is_empty()).then_some(effort.clone()),
                                    );
                                    if let Err(error) = store.save() {
                                        store.schedules.retain(|job| job.id != id);
                                        set_codex_error(&weak, format!("Could not save queued prompt: {error}"));
                                        continue;
                                    }
                                    if from_desktop {
                                        acknowledge_draft(&weak, &conversation_id, &prompt);
                                    }
                                    sync_host_schedules(&host_schedules, &store);
                                    set_scheduled_items(&weak, &store);
                                    queue_schedule(
                                        &scheduler,
                                        host_schedules.clone(),
                                        delay,
                                        QueuedSchedule {
                                            id,
                                            conversation_id,
                                            prompt: prompt.clone(),
                                            model: model.clone(),
                                            effort: effort.clone(),
                                        },
                                    );
                                    set_codex_status(
                                        &weak,
                                        "Prompt queued until usage reset".into(),
                                        None,
                                    );
                                }
                                if queued_until_reset {
                                    continue;
                                }
                            }
                        }
                        if let Some(schedule_id) = schedule_id.as_deref() {
                            let _ = store.set_schedule_status(schedule_id, "sent");
                            let _ = store.save();
                            sync_host_schedules(&host_schedules, &store);
                            set_scheduled_items(&weak, &store);
                        }
                        InteractionState::clear(&interaction);
                        clear_pending_interaction(&weak);
                        set_codex_status(&weak, "Codex is working...".into(), None);
                        let turn_started = std::time::Instant::now();
                        let mut timing_interval =
                            tokio::time::interval(std::time::Duration::from_secs(1));
                        set_work_timing(&weak, format!("working {}", format_elapsed(turn_started.elapsed())));
                        let (stop_tx, mut stop_rx) = watch::channel(false);
                        let (steer_tx, mut steer_rx) = mpsc::unbounded_channel::<SteerRequest>();
                        let active_conversation_id = selected_id.clone().unwrap_or_default();
                        let active_turn_state = host_active_turn.clone();
                        let active_turn_started_at_state = host_active_turn_started_at_ms.clone();
                        let active_turn_started_at_ms = now_millis();
                        let on_turn_started = |turn_id: Option<String>| {
                            if from_desktop {
                                acknowledge_draft(&weak, &active_conversation_id, &prompt);
                            }
                            *active_turn_state
                                .write()
                                .expect("active turn lock should not be poisoned") =
                                turn_id.map(|turn_id| {
                                    *active_turn_started_at_state
                                        .write()
                                        .expect("active turn start lock should not be poisoned") =
                                        Some(active_turn_started_at_ms);
                                    (active_conversation_id.clone(), turn_id)
                                });
                        };
                        let on_steer_response = |text: &str, accepted: bool| {
                            if accepted {
                                acknowledge_draft(&weak, &active_conversation_id, text);
                            } else {
                                let _ = weak.upgrade_in_event_loop(|window| {
                                    window.set_submission_warning("Codex did not accept the steer.".into());
                                });
                            }
                        };
                        let result = {
                            let turn = current.send_prompt(
                                &prompt,
                                (!model.trim().is_empty()).then_some(model.as_str()),
                                (!effort.trim().is_empty()).then_some(effort.as_str()),
                                PromptControl {
                                    cancel: &mut stop_rx,
                                    steers: &mut steer_rx,
                                    on_turn_started: &on_turn_started,
                                    on_steer_response: &on_steer_response,
                                },
                                interaction.clone(),
                                {
                                    let weak = weak.clone();
                                    move |request| set_pending_interaction(&weak, request)
                                },
                            );
                            tokio::pin!(turn);
                            loop { tokio::select! {
                                _ = timing_interval.tick() => {
                                    set_work_timing(&weak, format!("working {}", format_elapsed(turn_started.elapsed())));
                                }
                                result = &mut turn => break Some(result),
                                command = receiver.recv() => match command {
                                    Some(CodexCommand::Stop) => {
                                        let _ = stop_tx.send(true);
                                    }
                                    Some(CodexCommand::Host(HostAction::Interrupt { conversation_id }))
                                        if selected_id.as_deref() == Some(conversation_id.as_str()) =>
                                    {
                                        let _ = stop_tx.send(true);
                                    }
                                    Some(CodexCommand::Prompt {
                                        prompt: steer_prompt,
                                        model: _steer_model,
                                        effort: _steer_effort,
                                        schedule_id: None,
                                        conversation_id,
                                        expected_turn_id,
                                    }) if conversation_id
                                        .as_deref()
                                        .is_none_or(|id| selected_id.as_deref() == Some(id)) =>
                                    {
                                        let _ = steer_tx.send(SteerRequest {
                                            prompt: steer_prompt,
                                            expected_turn_id,
                                            from_desktop: conversation_id.is_none(),
                                        });
                                    }
                                    Some(command) => deferred_commands.push_back(command),
                                    None => break None,
                                },
                            } }
                        };
                        let completed_at_ms = now_millis();
                        if let Some(started_at_ms) = *host_active_turn_started_at_ms
                            .read()
                            .expect("active turn start lock should not be poisoned")
                        {
                            host_last_work_duration_ms
                                .write()
                                .expect("work duration lock should not be poisoned")
                                .insert(
                                    active_conversation_id.clone(),
                                    completed_at_ms.saturating_sub(started_at_ms),
                                );
                        }
                        *host_active_turn_started_at_ms
                            .write()
                            .expect("active turn start lock should not be poisoned") = None;
                        *host_active_turn
                            .write()
                            .expect("active turn lock should not be poisoned") = None;
                        let turn_elapsed = format_elapsed(turn_started.elapsed());
                        let Some(result) = result else { break };
                        InteractionState::clear(&interaction);
                        clear_pending_interaction(&weak);
                        match result {
                            Ok(response) => {
                                set_work_timing(&weak, format!("worked {turn_elapsed}"));
                                let transcript = current
                                    .current_thread_text()
                                    .await
                                    .ok()
                                    .filter(|text| !text.is_empty())
                                    .unwrap_or_else(|| format!("\u{001e}assistant\n{response}"));
                                set_codex_status(
                                    &weak,
                                    "Codex is ready".into(),
                                    Some(transcript.clone()),
                                );
                                if let Some(id) = selected_id.as_deref() {
                                    store.mark_activity(id, now_unix());
                                    let _ = store.save();
                                    if let Some(record) = store
                                        .conversations
                                        .iter()
                                        .find(|conversation| conversation.id == id)
                                    {
                                        let mut snapshot = host_snapshot(
                                            record,
                                            &transcript,
                                            &store.repository_path,
                                            None,
                                        );
                                        let duration = host_last_work_duration_ms
                                            .read()
                                            .expect("work duration lock should not be poisoned")
                                            .get(id)
                                            .copied();
                                        apply_snapshot_timing(&mut snapshot, None, duration);
                                        host_snapshots
                                            .write()
                                            .expect("snapshot lock should not be poisoned")
                                            .insert(id.to_owned(), snapshot);
                                    }
                                }
                                if let Ok(usage) = current.usage_status().await {
                                    *host_usage
                                        .write()
                                        .expect("usage lock should not be poisoned") = usage.api;
                                    set_usage(&weak, usage.summary);
                                }
                                refresh_sessions(current, &weak, &mut store, &host_conversations)
                                    .await;
                            }
                            Err(error) if error.to_string() == "Turn stopped by user" => {
                                set_work_timing(&weak, format!("stopped after {turn_elapsed}"));
                                diagnostics::record("turn", "stopped");
                                if let Some(schedule_id) = schedule_id.as_deref() {
                                    let _ = store.set_schedule_status(schedule_id, "failed");
                                    let _ = store.save();
                                    sync_host_schedules(&host_schedules, &store);
                                    set_scheduled_items(&weak, &store);
                                }
                                set_codex_status(&weak, "Codex is ready".into(), None);
                            }
                            Err(error) => {
                                set_work_timing(&weak, format!("failed after {turn_elapsed}"));
                                diagnostics::record("turn", "failed");
                                if let Some(schedule_id) = schedule_id.as_deref() {
                                    let _ = store.set_schedule_status(schedule_id, "failed");
                                    let _ = store.save();
                                    sync_host_schedules(&host_schedules, &store);
                                    set_scheduled_items(&weak, &store);
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
                        let Some(due_at) = i64::try_from(delay)
                            .ok()
                            .and_then(|seconds| now_unix().checked_add(seconds))
                            .filter(|due_at| chrono::DateTime::from_timestamp(*due_at, 0).is_some())
                        else {
                            set_codex_error(&weak, "Schedule delay is too large.".into());
                            continue;
                        };
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
                            due_at,
                            (!model.trim().is_empty()).then_some(model.clone()),
                            (!effort.trim().is_empty()).then_some(effort.clone()),
                        );
                        if let Err(error) = store.save() {
                            store.schedules.retain(|job| job.id != id);
                            set_codex_error(&weak, format!("Could not save scheduled prompt: {error}"));
                            continue;
                        }
                        acknowledge_draft(&weak, &conversation_id, &prompt);
                        sync_host_schedules(&host_schedules, &store);
                        set_scheduled_items(&weak, &store);
                        queue_schedule(
                            &scheduler,
                            host_schedules.clone(),
                            delay,
                            QueuedSchedule {
                                id,
                                conversation_id,
                                prompt,
                                model,
                                effort,
                            },
                        );
                        set_codex_status(&weak, "Prompt scheduled".into(), None);
                    }
                    CodexCommand::CancelSchedule(id) => {
                        if store.set_schedule_status(&id, "canceled") {
                            let _ = store.save();
                            sync_host_schedules(&host_schedules, &store);
                            set_scheduled_items(&weak, &store);
                            set_codex_status(&weak, "Scheduled prompt canceled".into(), None);
                        }
                    }
                    CodexCommand::Host(action) => match action {
                        HostAction::Send {
                            conversation_id,
                            text,
                            expected_turn_id,
                        } => {
                            if is_read_only_session(&read_only_sessions, Some(&conversation_id)) {
                                diagnostics::record("turn", "read_only_rejected");
                                continue;
                            }
                            let _ = scheduler.send(CodexCommand::Prompt {
                                prompt: text,
                                model: String::new(),
                                effort: String::new(),
                                schedule_id: None,
                                conversation_id: Some(conversation_id),
                                expected_turn_id,
                            });
                        }
                        HostAction::Archive {
                            conversation_id,
                            archived,
                        } => {
                            if store.set_archived(&conversation_id, archived) {
                                let _ = store.save();
                                sync_host_conversations(&host_conversations, &store);
                                sync_host_schedules(&host_schedules, &store);
                                set_scheduled_items(&weak, &store);
                                set_sessions(&weak, visible_sessions(&store, showing_archived));
                                if archived
                                    && selected_id.as_deref() == Some(conversation_id.as_str())
                                {
                                    clear_selected_conversation(
                                        &mut session,
                                        &mut selected_id,
                                        &weak,
                                    );
                                }
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
                        HostAction::Create {
                            device_id,
                            title,
                            model,
                            effort,
                            working_directory,
                            response,
                        } => {
                            let result = async {
                                let model = normalize_model_selection(model.as_str());
                                let Some(current) = session.as_mut() else {
                                    return Err("Codex is disconnected.".to_owned());
                                };
                                let working_directory = if working_directory.trim().is_empty() {
                                    store.repository_path.clone()
                                } else {
                                    working_directory.trim().to_owned()
                                };
                                if working_directory.starts_with("\\\\")
                                    || working_directory.starts_with("//")
                                    || !std::path::Path::new(&working_directory).is_dir()
                                {
                                    return Err("Working directory does not exist.".to_owned());
                                }
                                current.set_repository(working_directory.clone());
                                let id = current
                                    .new_thread((!model.trim().is_empty()).then_some(model.as_str()))
                                    .await
                                    .map_err(|error| error.to_string())?;
                                let title = if title.trim().is_empty() {
                                    "New conversation".to_owned()
                                } else {
                                    title.trim().to_owned()
                                };
                                store.register_thread(id.clone(), title.clone());
                                if let Some(record) = store
                                    .conversations
                                    .iter_mut()
                                    .find(|conversation| conversation.id == id)
                                {
                                    record.working_directory = working_directory.clone();
                                }
                                let _ = store.set_model_options(
                                    &id,
                                    (!model.trim().is_empty()).then_some(model.clone()),
                                    (!effort.trim().is_empty()).then_some(effort.clone()),
                                );
                                let _ = store.save();
                                sync_host_conversations(&host_conversations, &store);
                                host_registry
                                    .grant_conversation(id.clone(), device_id)
                                    .map_err(|error| error.to_string())?;
                                selected_id = Some(id.clone());
                                set_selected_session(&weak, id.clone());
                                set_current_working_directory(&weak, working_directory.clone());
                                set_host_devices(&weak, &host_registry, &id);
                                set_transcript(&weak, "New conversation ready".into());
                                set_sessions(&weak, visible_sessions(&store, showing_archived));
                                if let Some(record) = store
                                    .conversations
                                    .iter()
                                    .find(|conversation| conversation.id == id)
                                {
                                    let snapshot =
                                        host_snapshot(record, "", &store.repository_path, None);
                                    host_snapshots
                                        .write()
                                        .expect("snapshot lock should not be poisoned")
                                        .insert(id.clone(), snapshot);
                                }
                                Ok(serde_json::json!({
                                    "id": id,
                                    "title": title,
                                    "status": "idle",
                                    "items": [],
                                    "requests": [],
                                    "archived": false,
                                    "pinned": false,
                                    "model": if model.trim().is_empty() { serde_json::Value::Null } else { serde_json::Value::String(model) },
                                    "reasoningEffort": if effort.trim().is_empty() { serde_json::Value::Null } else { serde_json::Value::String(effort) },
                                    "workingDirectory": working_directory
                                }))
                            }
                            .await;
                            let _ = response.send(result);
                        }
                        HostAction::SetModel {
                            conversation_id,
                            model,
                        } => {
                            if store.set_model_options(
                                &conversation_id,
                                (!model.trim().is_empty()).then_some(model),
                                store
                                    .conversations
                                    .iter()
                                    .find(|conversation| conversation.id == conversation_id)
                                    .and_then(|conversation| conversation.reasoning_effort.clone()),
                            ) {
                                let _ = store.save();
                            }
                        }
                        HostAction::SetReasoning {
                            conversation_id,
                            effort,
                        } => {
                            if store.set_model_options(
                                &conversation_id,
                                store
                                    .conversations
                                    .iter()
                                    .find(|conversation| conversation.id == conversation_id)
                                    .and_then(|conversation| conversation.model.clone()),
                                (!effort.trim().is_empty()).then_some(effort),
                            ) {
                                let _ = store.save();
                            }
                        }
                        HostAction::Interrupt { conversation_id } => {
                            if selected_id.as_deref() == Some(conversation_id.as_str()) {
                                let _ = scheduler.send(CodexCommand::Host(
                                    HostAction::Interrupt { conversation_id },
                                ));
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
                            set_scheduled_items(&weak, &store);
                            queue_schedule(
                                &scheduler,
                                host_schedules.clone(),
                                due_at_unix.saturating_sub(now_unix()) as u64,
                                QueuedSchedule {
                                    id: id.clone(),
                                    conversation_id: conversation_id.clone(),
                                    prompt: text.clone(),
                                    model: String::new(),
                                    effort: String::new(),
                                },
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
                                set_scheduled_items(&weak, &store);
                                let _ = response.send(Ok(()));
                            } else {
                                let _ = response.send(Err("Schedule not found.".into()));
                            }
                        }
                        HostAction::Read {
                            conversation_id,
                            before,
                            limit,
                            response,
                        } => {
                            let result = async {
                                let Some(current) = session.as_mut() else {
                                    return Err("Codex is disconnected.".to_owned());
                                };
                                // Mobile polling must not acquire Codex's writer lease or
                                // change the desktop's selected provider target.
                                let history = current
                                    .read_thread(&conversation_id)
                                    .await
                                    .map_err(|error| error.to_string())?;
                                let Some(record) = store
                                    .conversations
                                    .iter()
                                    .find(|conversation| conversation.id == conversation_id)
                                else {
                                    return Err("Conversation not found.".to_owned());
                                };
                                let history_items = history_items(&record.id, &history);
                                let end = if let Some(cursor) = before.as_deref() {
                                    history_items
                                        .iter()
                                        .position(|item| item["id"] == cursor)
                                        .ok_or_else(|| "Invalid history cursor.".to_owned())?
                                } else {
                                    history_items.len()
                                };
                                let start = end.saturating_sub(limit.clamp(1, 100));
                                let page = history_items[start..end].to_vec();
                                let history_truncated = start > 0;
                                let history_before = history_truncated.then(|| page.first().and_then(|item| item["id"].as_str()).unwrap_or_default().to_owned());
                                let active_turn_id = host_active_turn
                                    .read()
                                    .expect("active turn lock should not be poisoned")
                                    .as_ref()
                                    .filter(|(active_conversation, _)| active_conversation == &record.id)
                                    .map(|(_, turn_id)| turn_id.clone());
                                let mut snapshot = host_snapshot(
                                    record,
                                    &history,
                                    &store.repository_path,
                                    active_turn_id.as_deref(),
                                );
                                let started_at_ms = if active_turn_id.is_some() {
                                    *host_active_turn_started_at_ms
                                        .read()
                                        .expect("active turn start lock should not be poisoned")
                                } else {
                                    None
                                };
                                let duration = host_last_work_duration_ms
                                    .read()
                                    .expect("work duration lock should not be poisoned")
                                    .get(&record.id)
                                    .copied();
                                apply_snapshot_timing(&mut snapshot, started_at_ms, duration);
                                snapshot["items"] = serde_json::Value::Array(page);
                                snapshot["historyTruncated"] = serde_json::json!(history_truncated);
                                snapshot["historyBefore"] = serde_json::json!(history_before);
                                host_snapshots
                                    .write()
                                    .expect("snapshot lock should not be poisoned")
                                    .insert(record.id.clone(), snapshot.clone());
                                Ok(snapshot)
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

struct QueuedSchedule {
    id: String,
    conversation_id: String,
    prompt: String,
    model: String,
    effort: String,
}

fn queue_schedule(
    sender: &UnboundedSender<CodexCommand>,
    schedules: Arc<std::sync::RwLock<Vec<HostSchedule>>>,
    delay: u64,
    schedule: QueuedSchedule,
) {
    let sender = sender.clone();
    tokio::spawn(async move {
        tokio::time::sleep(std::time::Duration::from_secs(delay)).await;
        if !schedules
            .read()
            .expect("host schedule lock should not be poisoned")
            .iter()
            .any(|item| item.id == schedule.id && item.status == "pending")
        {
            return;
        }
        let _ = sender.send(CodexCommand::Prompt {
            prompt: schedule.prompt,
            model: schedule.model,
            effort: schedule.effort,
            schedule_id: Some(schedule.id),
            conversation_id: Some(schedule.conversation_id),
            expected_turn_id: None,
        });
    });
}

fn schedule_pending(store: &DesktopStore, schedule_id: &str) -> bool {
    store.schedules.iter().any(|schedule| {
        schedule.id == schedule_id
            && schedule.status == "pending"
            && store.conversations.iter().any(|conversation| {
                conversation.id == schedule.conversation_id && !conversation.archived
            })
    })
}

async fn refresh_sessions(
    session: &mut CodexSession,
    weak: &slint::Weak<AppWindow>,
    store: &mut DesktopStore,
    host_conversations: &Arc<std::sync::RwLock<Vec<HostConversation>>>,
) {
    match session.list_threads().await {
        Ok(sessions) => {
            store.refresh_registered_threads(sessions);
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
            working_directory: conversation.working_directory.clone(),
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

fn set_scheduled_items(weak: &slint::Weak<AppWindow>, store: &DesktopStore) {
    let items = store
        .schedules
        .iter()
        .map(|schedule| ScheduledItem {
            id: schedule.id.clone().into(),
            conversation_id: schedule.conversation_id.clone().into(),
            text: schedule.prompt.clone().into(),
            due_at: chrono::DateTime::<chrono::Utc>::from_timestamp(schedule.due_at_unix, 0)
                .map(|value| value.to_rfc3339())
                .unwrap_or_default()
                .into(),
            status: schedule.status.clone().into(),
        })
        .collect::<Vec<_>>();
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_scheduled_items(slint::ModelRc::new(slint::VecModel::from(items)));
    });
}

fn history_items(conversation_id: &str, history: &str) -> Vec<serde_json::Value> {
    let segments = if history.contains(TRANSCRIPT_MARKER) {
        history.split(TRANSCRIPT_MARKER).collect::<Vec<_>>()
    } else {
        vec![history]
    };
    let mut items = Vec::new();
    for (index, segment) in segments.into_iter().enumerate() {
        if segment.trim().is_empty() {
            continue;
        }
        let (header, body) = if history.contains(TRANSCRIPT_MARKER) && index > 0 {
            segment.split_once('\n').unwrap_or(("assistant", segment))
        } else {
            ("assistant", segment)
        };
        let item_type = match header {
            "user" => "userMessage",
            "assistant" => "agentMessage",
            _ => "commandExecution",
        };
        let chunks = body.trim_end().chars().collect::<Vec<_>>();
        for (chunk_index, chunk) in chunks.chunks(8_000).enumerate() {
            let text = chunk.iter().collect::<String>();
            items.push(serde_json::json!({
                "id": format!("history-{conversation_id}-{index}-{chunk_index}"),
                "type": item_type,
                "text": if item_type == "commandExecution" { "" } else { text.as_str() },
                "detail": if item_type == "commandExecution" { text.as_str() } else { "" },
                "status": "completed"
            }));
        }
    }
    items
}

fn host_snapshot(
    record: &ConversationRecord,
    history: &str,
    repository: &str,
    active_turn_id: Option<&str>,
) -> serde_json::Value {
    serde_json::json!({
        "id": record.id,
        "title": record.title,
        "status": if active_turn_id.is_some() { "working" } else { "idle" },
        "activeTurnId": active_turn_id,
        "items": history_items(&record.id, history),
        "requests": [],
        "error": null,
        "repository": std::path::Path::new(repository)
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or_default(),
        "workingDirectory": record.working_directory,
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
        "lastMessageAt": record.last_activity_unix
    })
}

fn apply_snapshot_timing(
    snapshot: &mut serde_json::Value,
    active_started_at_ms: Option<i64>,
    last_work_duration_ms: Option<i64>,
) {
    snapshot["activeTurnStartedAt"] = serde_json::json!(
        active_started_at_ms
            .and_then(chrono::DateTime::<chrono::Utc>::from_timestamp_millis)
            .map(|value| value.to_rfc3339())
    );
    snapshot["lastWorkDurationMs"] = serde_json::json!(last_work_duration_ms);
}

fn now_millis() -> i64 {
    chrono::Utc::now().timestamp_millis()
}

fn visible_sessions(store: &DesktopStore, showing_archived: bool) -> Vec<(String, String, bool)> {
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
    resolve_codex_from_paths(
        env::var_os("APPDATA").map(PathBuf::from),
        env::var_os("USERPROFILE").map(PathBuf::from),
        env::var("PATH").ok().as_deref(),
    )
}

fn resolve_codex_from_paths(
    app_data: Option<PathBuf>,
    user_profile: Option<PathBuf>,
    path_value: Option<&str>,
) -> String {
    let extension_root = user_profile
        .as_deref()
        .map(|profile| profile.join(".vscode/extensions"));
    let architecture = if cfg!(target_arch = "aarch64") {
        "windows-aarch64"
    } else {
        "windows-x86_64"
    };
    if let Some(root) = extension_root.filter(|path| path.is_dir()) {
        let mut candidates = Vec::new();
        if let Ok(entries) = fs::read_dir(root) {
            for entry in entries.flatten() {
                let name = entry.file_name().to_string_lossy().into_owned();
                let Some(version) = name.strip_prefix("openai.chatgpt-") else {
                    continue;
                };
                if version.contains("insider") {
                    continue;
                }
                let version = version.split('-').next().and_then(|value| {
                    let mut parts = value.split('.');
                    Some((
                        parts.next()?.parse::<u32>().ok()?,
                        parts.next()?.parse::<u32>().ok()?,
                        parts.next()?.parse::<u32>().ok()?,
                    ))
                });
                let Some(version) = version else { continue };
                let binary = entry.path().join(format!("bin/{architecture}/codex.exe"));
                if binary.is_file() {
                    candidates.push((version, binary));
                }
            }
        }
        candidates.sort_by_key(|left| std::cmp::Reverse(left.0));
        if let Some((_, binary)) = candidates.into_iter().next() {
            return binary.to_string_lossy().into_owned();
        }
    }

    for directory in std::env::split_paths(std::ffi::OsStr::new(path_value.unwrap_or_default())) {
        let binary = directory.join(if cfg!(windows) { "codex.exe" } else { "codex" });
        if binary.is_file() {
            return binary.to_string_lossy().into_owned();
        }
        if cfg!(windows) {
            let npm = directory.join(
                "node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe",
            );
            if npm.is_file() {
                return npm.to_string_lossy().into_owned();
            }
        }
    }

    if cfg!(windows)
        && let Some(app_data) = app_data
    {
        let wrapper = app_data.join("npm/codex.ps1");
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
        && let Ok(mut config) = serde_json::from_str::<RelayConfig>(&contents)
    {
        if config.session_key.is_empty() {
            config.session_key = SessionKey::random().to_wire();
            fs::write(&path, serde_json::to_vec_pretty(&config)?)?;
        }
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
        let available = codex_status_available(&status, window.get_codex_available());
        window.set_codex_available(available);
        window.set_codex_status(status.into());
        if let Some(transcript) = transcript {
            set_transcript_blocks(&window, &transcript);
        }
    });
}

fn codex_status_available(status: &str, previous: bool) -> bool {
    if status == "Codex is disconnected" || status.starts_with("Connecting to Codex") {
        false
    } else if status.starts_with("Codex error:") {
        previous
    } else {
        true
    }
}

fn set_transcript(weak: &slint::Weak<AppWindow>, transcript: String) {
    let _ = weak.upgrade_in_event_loop(move |window| set_transcript_blocks(&window, &transcript));
}

fn clear_selected_conversation(
    session: &mut Option<CodexSession>,
    selected_id: &mut Option<String>,
    weak: &slint::Weak<AppWindow>,
) {
    if let Some(session) = session.as_mut() {
        session.clear_thread();
    }
    *selected_id = None;
    set_selected_session(weak, String::new());
    set_session_read_only(weak, false);
    set_current_working_directory(weak, String::new());
    set_selected_model_options(weak, None, None);
    set_transcript(weak, "No conversation selected".into());
}

fn set_transcript_blocks(window: &AppWindow, markdown: &str) {
    let working_directory = window.get_current_working_directory().to_string();
    let blocks = markdown_blocks(markdown)
        .into_iter()
        .map(|block| {
            let image = block
                .image_path
                .as_deref()
                .and_then(|path| image_from_markdown_path(path, &working_directory));
            MarkdownBlock {
                text: block.text.into(),
                kind: block.kind.into(),
                role: block.role.into(),
                summary: block.summary.into(),
                collapsible: block.collapsible,
                has_image: image.is_some(),
                image: image.unwrap_or_default(),
            }
        })
        .collect::<Vec<_>>();
    window.set_transcript_content_y(-1_000_000.0);
    window.set_transcript_blocks(slint::ModelRc::new(slint::VecModel::from(blocks)));
}

fn image_from_markdown_path(path: &str, working_directory: &str) -> Option<slint::Image> {
    if let Some(bytes) = decode_data_image(path) {
        return image_from_bytes(&bytes);
    }
    let path = if let Some(path) = path.strip_prefix("file:///") {
        if path.as_bytes().get(1) == Some(&b':') {
            path.to_owned()
        } else {
            format!("/{path}")
        }
    } else {
        path.strip_prefix("file://").unwrap_or(path).to_owned()
    };
    let path = std::path::Path::new(&path);
    let path = if path.is_absolute() || working_directory.trim().is_empty() {
        path.to_owned()
    } else {
        std::path::Path::new(working_directory).join(path)
    };
    slint::Image::load_from_path(&path).ok()
}

fn decode_data_image(source: &str) -> Option<Vec<u8>> {
    let (metadata, payload) = source.strip_prefix("data:")?.split_once(',')?;
    if !metadata
        .split(';')
        .any(|part| part.eq_ignore_ascii_case("base64"))
    {
        return None;
    }
    BASE64
        .decode(payload.replace(['\r', '\n', ' ', '\t'], ""))
        .ok()
}

fn image_from_bytes(bytes: &[u8]) -> Option<slint::Image> {
    let decoded = image::load_from_memory(bytes).ok()?.into_rgba8();
    let buffer = slint::SharedPixelBuffer::<slint::Rgba8Pixel>::clone_from_slice(
        decoded.as_raw(),
        decoded.width(),
        decoded.height(),
    );
    Some(slint::Image::from_rgba8(buffer))
}

fn set_sessions(weak: &slint::Weak<AppWindow>, sessions: Vec<(String, String, bool)>) {
    let items = sessions
        .into_iter()
        .map(|(id, title, pinned)| SessionItem {
            id: id.into(),
            title: title.into(),
            pinned,
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

fn refresh_host_devices_for_selection(weak: &slint::Weak<AppWindow>, registry: &DeviceRegistry) {
    let registry = registry.clone();
    let _ = weak.upgrade_in_event_loop(move |window| {
        let conversation_id = window.get_current_session_id().to_string();
        let items = registry
            .device_access(&conversation_id)
            .into_iter()
            .map(|device| HostDeviceItem {
                id: device.id.to_string().into(),
                name: device.name.into(),
                authorized: device.authorized,
            })
            .collect::<Vec<_>>();
        window.set_host_devices(slint::ModelRc::new(slint::VecModel::from(items)));
    });
}

fn set_selected_session(weak: &slint::Weak<AppWindow>, id: String) {
    let _ = weak.upgrade_in_event_loop(move |window| window.invoke_switch_draft(id.into()));
}

fn set_session_read_only(weak: &slint::Weak<AppWindow>, read_only: bool) {
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_selected_session_read_only(read_only);
    });
}

fn is_read_only_session(read_only_sessions: &HashSet<String>, target_id: Option<&str>) -> bool {
    target_id.is_some_and(|id| read_only_sessions.contains(id))
}

fn folder_choices(folder: &std::path::Path) -> std::io::Result<Vec<slint::SharedString>> {
    let folder = std::path::absolute(folder)?;
    let mut children = std::fs::read_dir(&folder)?
        .filter_map(Result::ok)
        .filter(|entry| entry.file_type().is_ok_and(|kind| kind.is_dir()))
        .map(|entry| entry.path())
        .collect::<Vec<_>>();
    children.sort_unstable();
    Ok(std::iter::once("Choose a folder…".into())
        .chain(
            folder
                .parent()
                .map(std::path::Path::to_path_buf)
                .into_iter()
                .chain(children)
                .map(|path| path.to_string_lossy().as_ref().into()),
        )
        .collect())
}

fn acknowledge_draft(weak: &slint::Weak<AppWindow>, id: &str, text: &str) {
    let id = id.to_owned();
    let text = text.to_owned();
    let _ = weak.upgrade_in_event_loop(move |window| {
        if window.get_current_session_id() == id && window.get_composer_draft() == text {
            window.set_composer_draft("".into());
        }
    });
}

fn set_current_working_directory(weak: &slint::Weak<AppWindow>, directory: String) {
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_current_working_directory(directory.into());
    });
}

fn set_selected_model_options(
    weak: &slint::Weak<AppWindow>,
    model: Option<String>,
    effort: Option<String>,
) {
    let model = model.unwrap_or_else(|| CODEX_DEFAULT_MODEL_LABEL.to_owned());
    let effort = effort.unwrap_or_default();
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_selected_model(model.into());
        window.set_selected_effort(effort.into());
    });
}

fn set_archived_view(weak: &slint::Weak<AppWindow>, showing: bool) {
    let _ = weak.upgrade_in_event_loop(move |window| window.set_showing_archived(showing));
}

fn normalize_model_selection(value: &str) -> String {
    let value = value.trim();
    if value.is_empty() || value == CODEX_DEFAULT_MODEL_LABEL {
        String::new()
    } else {
        value.to_owned()
    }
}

fn set_model_catalog(weak: &slint::Weak<AppWindow>, models: String) {
    let value = if models.is_empty() {
        "Provider returned no visible models".to_owned()
    } else {
        models.clone()
    };
    let mut model_options = models
        .lines()
        .filter_map(|line| {
            line.split_once(" - ")
                .map(|(model, _)| model.to_owned())
                .or_else(|| (!line.trim().is_empty()).then(|| line.trim().to_owned()))
        })
        .collect::<Vec<_>>();
    // The consent-preserving option must be the initial choice. Provider
    // catalog defaults are available choices, never implicit overrides.
    model_options.insert(0, CODEX_DEFAULT_MODEL_LABEL.to_owned());
    let options = model_options;
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_model_catalog(value.into());
        let options = options
            .into_iter()
            .map(slint::SharedString::from)
            .collect::<Vec<_>>();
        window.set_model_options(slint::ModelRc::new(slint::VecModel::from(options)));
    });
}

fn set_usage(weak: &slint::Weak<AppWindow>, usage: String) {
    let _ = weak.upgrade_in_event_loop(move |window| window.set_usage_summary(usage.into()));
}

fn can_queue_usage_exhausted(schedule_id: Option<&str>, conversation_id: Option<&str>) -> bool {
    schedule_id.is_some() || conversation_id.is_some()
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

fn set_work_timing(weak: &slint::Weak<AppWindow>, timing: String) {
    let _ = weak.upgrade_in_event_loop(move |window| window.set_work_timing(timing.into()));
}

fn format_elapsed(duration: std::time::Duration) -> String {
    let seconds = duration.as_secs();
    if seconds < 60 {
        format!("{seconds}s")
    } else {
        format!("{}m {:02}s", seconds / 60, seconds % 60)
    }
}

fn set_relay_running(weak: &slint::Weak<AppWindow>, endpoint: String, config: &RelayConfig) {
    let enrollment = format!(
        "Host ID: {}\nHost token: {}\nClient ID: {}\nClient token: {}\nSession key: {}",
        config.host_id,
        config.host_token,
        config.client_id,
        config.client_token,
        config.session_key,
    );
    let session_key = config.session_key.clone();
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_relay_running(true);
        window.set_relay_status("Relay is running".into());
        window.set_relay_endpoint(format!("ws://{endpoint}").into());
        window.set_relay_session_key(format!("Session key: {session_key}").into());
        window.set_relay_enrollment(enrollment.into());
    });
}

fn set_relay_stopped(weak: &slint::Weak<AppWindow>) {
    let _ = weak.upgrade_in_event_loop(|window| {
        window.set_relay_running(false);
        window.set_relay_status("Relay is stopped".into());
        window.set_relay_endpoint("No endpoint".into());
        window.set_relay_session_key("Session key unavailable".into());
        window.set_relay_enrollment("Relay enrollment is not loaded".into());
    });
}

fn set_relay_error(weak: &slint::Weak<AppWindow>, message: String) {
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_relay_running(false);
        window.set_relay_status(message.into());
        window.set_relay_endpoint("No endpoint".into());
        window.set_relay_session_key("Session key unavailable".into());
        window.set_relay_enrollment("Relay enrollment is not loaded".into());
    });
}

fn set_relay_host_status(weak: &slint::Weak<AppWindow>, message: String) {
    let _ = weak.upgrade_in_event_loop(move |window| window.set_relay_host_status(message.into()));
}

fn set_relay_host_connected(weak: &slint::Weak<AppWindow>, connected: bool) {
    let _ = weak.upgrade_in_event_loop(move |window| window.set_relay_host_connected(connected));
}

fn set_relay_host_stopped(weak: &slint::Weak<AppWindow>) {
    let _ = weak.upgrade_in_event_loop(|window| {
        window.set_relay_host_connected(false);
        window.set_relay_host_status("Outbound relay host disconnected.".into());
    });
}

fn set_host_status(weak: &slint::Weak<AppWindow>, status: String) {
    let _ = weak.upgrade_in_event_loop(move |window| window.set_host_status(status.into()));
}

struct HostTlsIdentity {
    certificate_der: Vec<u8>,
    key_der: Vec<u8>,
    spki_hex: String,
}

fn load_host_tls_identity_for_host(
    advertised_host: Option<&str>,
) -> Result<HostTlsIdentity, String> {
    let directory = DesktopStore::path()
        .parent()
        .ok_or_else(|| "desktop data directory is unavailable".to_owned())?
        .to_owned();
    fs::create_dir_all(&directory).map_err(|error| error.to_string())?;
    let certificate_path = directory.join("host-certificate.der");
    let key_path = directory.join("host-key.der");
    let spki_path = directory.join("host-spki.sha256");
    let advertised =
        advertised_host.map(|value| value.split(':').next().unwrap_or(value).to_owned());
    let lan_address = detect_lan_ipv4().map(|address| address.to_string());
    let names = host_certificate_names(advertised.as_deref(), lan_address.as_deref());
    if certificate_path.exists() && key_path.exists() && spki_path.exists() {
        let certificate_der = fs::read(&certificate_path).map_err(|error| error.to_string())?;
        let key_der = fs::read(&key_path).map_err(|error| error.to_string())?;
        // Keep the persisted signing key/SPKI pin, but refresh the leaf SANs
        // so a LAN-origin QR remains valid after upgrading from loopback-only
        // builds. Existing paired devices keep the same public-key pin.
        if (lan_address.is_some() || advertised.is_some())
            && let Ok(key_pair) = KeyPair::from_pkcs8_der_and_sign_algo(
                &PrivatePkcs8KeyDer::from(key_der.as_slice()),
                &PKCS_ECDSA_P256_SHA256,
            )
            && let Ok(cert) = server_certificate_params(names.clone())
                .and_then(|params| params.self_signed(&key_pair))
        {
            let refreshed = cert.der().to_vec();
            fs::write(&certificate_path, &refreshed).map_err(|error| error.to_string())?;
            return Ok(HostTlsIdentity {
                certificate_der: refreshed,
                key_der,
                spki_hex: fs::read_to_string(spki_path)
                    .map_err(|error| error.to_string())?
                    .trim()
                    .to_owned(),
            });
        }
        return Ok(HostTlsIdentity {
            certificate_der,
            key_der,
            spki_hex: fs::read_to_string(spki_path)
                .map_err(|error| error.to_string())?
                .trim()
                .to_owned(),
        });
    }
    let key_pair =
        KeyPair::generate_for(&PKCS_ECDSA_P256_SHA256).map_err(|error| error.to_string())?;
    let certificate = server_certificate_params(names)
        .map_err(|error| error.to_string())?
        .self_signed(&key_pair)
        .map_err(|error| error.to_string())?;
    let certificate_der = certificate.der().to_vec();
    let key_der = key_pair.serialize_der();
    let spki_der = key_pair.public_key_der();
    let spki_hex = bytes_to_hex(&Sha256::digest(spki_der));
    fs::write(&certificate_path, &certificate_der).map_err(|error| error.to_string())?;
    fs::write(&key_path, &key_der).map_err(|error| error.to_string())?;
    fs::write(&spki_path, &spki_hex).map_err(|error| error.to_string())?;
    Ok(HostTlsIdentity {
        certificate_der,
        key_der,
        spki_hex,
    })
}

fn server_certificate_params(names: Vec<String>) -> Result<CertificateParams, rcgen::Error> {
    let mut params = CertificateParams::new(names)?;
    params.is_ca = IsCa::NoCa;
    params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
    Ok(params)
}

fn host_certificate_names(advertised: Option<&str>, lan_address: Option<&str>) -> Vec<String> {
    let mut names = vec!["localhost".to_owned(), "127.0.0.1".to_owned()];
    for name in [advertised, lan_address].into_iter().flatten() {
        if !names.iter().any(|existing| existing == name) {
            names.push(name.to_owned());
        }
    }
    names
}

fn bytes_to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn pairing_payload(
    display: &pocketagent_host::PairingDisplay,
    endpoint: &str,
    spki: &str,
) -> String {
    format!(
        "pocketagent://pair?v=1&origin={endpoint}&spki={spki}&ticket={}",
        display.ticket
    )
}

fn endpoint_host(endpoint: &str) -> Option<&str> {
    let authority = endpoint
        .strip_prefix("https://")
        .or_else(|| endpoint.strip_prefix("http://"))?
        .split('/')
        .next()?;
    Some(
        authority
            .rsplit_once(':')
            .map_or(authority, |(host, _)| host),
    )
}

fn pairing_qr(payload: &str) -> Result<slint::Image, String> {
    let code = QrCode::new(payload.as_bytes()).map_err(|error| error.to_string())?;
    let bitmap = code
        .render::<image::Luma<u8>>()
        .quiet_zone(true)
        .min_dimensions(420, 420)
        .build();
    let (width, height) = bitmap.dimensions();
    let mut pixels = slint::SharedPixelBuffer::<slint::Rgba8Pixel>::new(width, height);
    for (index, pixel) in pixels.make_mut_slice().iter_mut().enumerate() {
        let x = (index as u32) % width;
        let y = (index as u32) / width;
        let value = bitmap.get_pixel(x, y).0[0];
        *pixel = slint::Rgba8Pixel {
            r: value,
            g: value,
            b: value,
            a: 0xff,
        };
    }
    Ok(slint::Image::from_rgba8(pixels))
}

fn resolve_advertised_endpoint(
    requested_address: std::net::SocketAddr,
    bound_address: std::net::SocketAddr,
    configured_advertise: Option<&str>,
    detected_public: Option<std::net::Ipv4Addr>,
) -> String {
    if let Some(value) = configured_advertise {
        if value.contains(':') {
            return value.to_owned();
        }
        return format!("{value}:{}", bound_address.port());
    }

    let host = if (requested_address.ip().is_unspecified() || requested_address.ip().is_loopback())
        && detected_public.is_some()
    {
        detected_public
            .map(|address| address.to_string())
            .unwrap_or_else(|| requested_address.ip().to_string())
    } else if requested_address.ip().is_unspecified() {
        bound_address.ip().to_string()
    } else {
        requested_address.ip().to_string()
    };
    format!("{host}:{}", bound_address.port())
}

fn detect_lan_ipv4() -> Option<std::net::Ipv4Addr> {
    let socket = std::net::UdpSocket::bind((std::net::Ipv4Addr::UNSPECIFIED, 0)).ok()?;
    // UDP connect only selects the route; it does not send a packet. This lets
    // us discover the interface Android can reach without requiring a probe.
    socket
        .connect((std::net::Ipv4Addr::new(192, 0, 2, 1), 9))
        .ok()?;
    match socket.local_addr().ok()?.ip() {
        std::net::IpAddr::V4(address) if !address.is_loopback() => Some(address),
        _ => None,
    }
}

fn detect_public_ipv4() -> Result<std::net::Ipv4Addr, String> {
    let client = reqwest::blocking::Client::builder()
        .no_proxy()
        .timeout(std::time::Duration::from_secs(8))
        .build()
        .map_err(|error| format!("public address client failed: {error}"))?;
    let response = client
        .get("https://api.ipify.org?format=json")
        .send()
        .map_err(|error| format!("public address lookup failed: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "public address lookup returned {}",
            response.status()
        ));
    }
    let body = response
        .json::<serde_json::Value>()
        .map_err(|error| format!("public address response was invalid: {error}"))?;
    let value = body
        .get("ip")
        .and_then(serde_json::Value::as_str)
        .ok_or_else(|| "public address response did not include an IPv4 address".to_owned())?;
    let address = value
        .parse::<std::net::Ipv4Addr>()
        .map_err(|error| format!("public address was not IPv4: {error}"))?;
    if !is_public_ipv4(address) {
        return Err(format!(
            "public address lookup returned a non-public address {address}"
        ));
    }
    Ok(address)
}

fn is_public_ipv4(address: std::net::Ipv4Addr) -> bool {
    let octets = address.octets();
    !address.is_loopback()
        && !address.is_private()
        && !address.is_link_local()
        && octets[0] > 0
        && octets[0] < 224
        && !(octets[0] == 100 && (64..=127).contains(&octets[1]))
}

fn host_diagnostics_report() -> String {
    let mut checks = Vec::new();
    let configured_bind = env::var("POCKETAGENT_HOST_BIND").ok();
    let requested = configured_bind
        .clone()
        .unwrap_or_else(|| "0.0.0.0:8788".to_owned());
    let Ok(requested_address) = requested.parse::<std::net::SocketAddr>() else {
        return format!("FAIL: invalid POCKETAGENT_HOST_BIND `{requested}`.");
    };
    let probe_address = if requested_address.ip().is_unspecified() {
        std::net::SocketAddr::from(([127, 0, 0, 1], requested_address.port()))
    } else {
        requested_address
    };
    let configured_advertise = env::var("POCKETAGENT_HOST_ADVERTISE")
        .ok()
        .filter(|value| !value.trim().is_empty());
    let detected_public = if configured_advertise.is_none()
        && (configured_bind.is_none() || requested_address.ip().is_unspecified())
    {
        detect_public_ipv4().ok()
    } else {
        None
    };
    let advertised = configured_advertise
        .as_deref()
        .map(|value| value.split(':').next().unwrap_or(value).to_owned())
        .or_else(|| detected_public.map(|address| address.to_string()))
        .unwrap_or_else(|| requested_address.ip().to_string());
    checks.push(format!(
        "Listener: {requested}; pairing address: https://{advertised}:{}.",
        requested_address.port()
    ));
    #[cfg(windows)]
    if requested_address.ip().is_loopback() {
        checks.push("Windows Firewall: not required for a loopback listener.".into());
    } else if firewall_rule_exists(requested_address.port()) {
        checks.push(format!(
            "Windows Firewall: PASS (PocketAgent inbound TCP {}).",
            requested_address.port()
        ));
    } else {
        checks.push(format!(
            "Windows Firewall: FAIL (no inbound allow rule for TCP {}). Start the host and approve the prompt.",
            requested_address.port()
        ));
    }
    #[cfg(not(windows))]
    checks.push("Windows Firewall: not applicable on this platform.".into());
    if let Some(public) = detected_public {
        checks.push(format!("Public IPv4 lookup: PASS ({public})."));
    } else if configured_advertise.is_some() {
        checks
            .push("Public IPv4 lookup: skipped because POCKETAGENT_HOST_ADVERTISE is set.".into());
    } else {
        checks.push("Public IPv4 lookup: FAIL; set POCKETAGENT_HOST_ADVERTISE manually.".into());
    }
    match std::net::TcpStream::connect_timeout(&probe_address, std::time::Duration::from_secs(2)) {
        Ok(_) => checks.push(format!("Local TCP listener: PASS ({probe_address}).")),
        Err(error) => checks.push(format!(
            "Local TCP listener: FAIL ({probe_address}): {error}. The host status must not be treated as running."
        )),
    }
    let client = reqwest::blocking::Client::builder()
        .no_proxy()
        .danger_accept_invalid_certs(true)
        .timeout(std::time::Duration::from_secs(5))
        .build();
    match client {
        Ok(client) => {
            let url = format!("https://{probe_address}/api/session");
            match client.get(url).send() {
                Ok(response) => {
                    let status = response.status();
                    match response.text() {
                    Ok(body) if body.contains("version") && body.contains("authenticated") => {
                        checks.push(format!("Local HTTPS/API: PASS (HTTP {status})."));
                    }
                    Ok(_) => checks.push(format!(
                        "Local HTTPS/API: FAIL (HTTP {status} did not return PocketAgent metadata)."
                    )),
                    Err(error) => checks.push(format!("Local HTTPS/API: FAIL reading response: {error}.")),
                    }
                }
                Err(error) => checks.push(format!("Local HTTPS/API: FAIL: {error}.")),
            }
        }
        Err(error) => checks.push(format!("Local HTTPS/API client could not start: {error}.")),
    }
    let data_directory = DesktopStore::path().parent().map(|path| path.to_owned());
    if let Some(directory) = data_directory {
        let certificate = directory.join("host-certificate.der");
        let key = directory.join("host-key.der");
        checks.push(format!(
            "TLS identity files: certificate={}, key={}, SPKI pin={}.",
            if certificate.exists() {
                "present"
            } else {
                "missing"
            },
            if key.exists() { "present" } else { "missing" },
            if directory.join("host-spki.sha256").exists() {
                "present"
            } else {
                "missing"
            },
        ));
    }
    checks.push("External reachability: not proven locally. Test from the phone with Wi-Fi disabled; confirm router forwarding and Windows Firewall for the listener port.".into());
    checks.join("\n")
}

fn configured_host_address() -> Result<std::net::SocketAddr, String> {
    let requested = env::var("POCKETAGENT_HOST_BIND").unwrap_or_else(|_| "0.0.0.0:8788".to_owned());
    requested
        .parse::<std::net::SocketAddr>()
        .map_err(|error| format!("invalid host bind address: {error}"))
}

fn host_firewall_required() -> bool {
    let Ok(address) = configured_host_address() else {
        return false;
    };
    if address.ip().is_loopback() {
        return false;
    }
    #[cfg(windows)]
    {
        !firewall_rule_exists(address.port())
    }
    #[cfg(not(windows))]
    {
        false
    }
}

fn start_host_server(
    state: HostHttpState,
    shutdown_slot: Arc<Mutex<Option<oneshot::Sender<()>>>>,
) -> Result<String, String> {
    let configured_bind = env::var("POCKETAGENT_HOST_BIND").ok();
    let requested_address = configured_host_address()?;
    let requested = requested_address.to_string();
    let configured_advertise = env::var("POCKETAGENT_HOST_ADVERTISE")
        .ok()
        .filter(|value| !value.trim().is_empty());
    let needs_public_advertise = configured_advertise.is_none()
        && (configured_bind.is_none() || requested_address.ip().is_unspecified());
    let detected_public = if needs_public_advertise {
        Some(detect_public_ipv4().map_err(|error| {
            format!(
                "public pairing address unavailable: {error}; set POCKETAGENT_HOST_ADVERTISE to a public IPv4"
            )
        })?)
    } else {
        None
    };
    let advertised_host = configured_advertise
        .as_deref()
        .map(|value| value.split(':').next().unwrap_or(value))
        .map(str::to_owned)
        .or_else(|| detected_public.map(|address| address.to_string()));
    let tls_identity = load_host_tls_identity_for_host(advertised_host.as_deref())?;
    let listener = match std::net::TcpListener::bind(&requested) {
        Ok(listener) => listener,
        Err(error) => {
            diagnostics::record("host", "bind_failed");
            return Err(error.to_string());
        }
    };
    let _ = listener.set_nonblocking(true);
    let endpoint = listener
        .local_addr()
        .map(|address| {
            let advertised = resolve_advertised_endpoint(
                requested_address,
                address,
                configured_advertise.as_deref(),
                detected_public,
            );
            format!("https://{advertised}")
        })
        .unwrap_or_else(|_| format!("https://{requested}"));
    let display_endpoint = endpoint.clone();
    let return_endpoint = display_endpoint.clone();
    let (shutdown, shutdown_signal) = oneshot::channel();
    let (ready_tx, ready_rx) = oneshot::channel::<Result<(), String>>();
    if let Ok(mut slot) = shutdown_slot.lock() {
        *slot = Some(shutdown);
    } else {
        return Err("host shutdown state is unavailable".into());
    }
    std::thread::spawn(move || {
        let Ok(runtime) = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
        else {
            diagnostics::record("host", "runtime_failed");
            let _ = ready_tx.send(Err("host runtime could not start".into()));
            return;
        };
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let startup_endpoint = display_endpoint.clone();
            runtime.block_on(async move {
                diagnostics::record("host", &format!("starting endpoint={startup_endpoint}"));
                let server_config = match ServerConfig::builder()
                    .with_no_client_auth()
                    .with_single_cert(
                        vec![CertificateDer::from(tls_identity.certificate_der)],
                        PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(tls_identity.key_der)),
                    ) {
                    Ok(config) => config,
                    Err(error) => {
                        let message = format!("TLS configuration failed: {error}");
                        diagnostics::record("host", &format!("tls_config_failed_{error}"));
                        let _ = ready_tx.send(Err(message));
                        return;
                    }
                };
                let tls_config = RustlsConfig::from_config(Arc::new(server_config));
                let handle = axum_server::Handle::new();
                let shutdown_handle = handle.clone();
                tokio::spawn(async move {
                    let _ = shutdown_signal.await;
                    shutdown_handle.graceful_shutdown(Some(std::time::Duration::from_secs(2)));
                });
                let server = match axum_server::from_tcp(listener) {
                    Ok(server) => server
                        .acceptor(RustlsAcceptor::new(tls_config))
                        .handle(handle.clone()),
                    Err(error) => {
                        let message = format!("HTTPS listener could not start: {error}");
                        diagnostics::record("host", &format!("listener_failed_{error}"));
                        let _ = ready_tx.send(Err(message));
                        return;
                    }
                };
                let server_task =
                    tokio::spawn(server.serve(host_router(state).into_make_service()));
                match handle.listening().await {
                    Some(address) => {
                        diagnostics::record("host", &format!("listening local={address}"));
                        let _ = ready_tx.send(Ok(()));
                    }
                    None => {
                        let message = "HTTPS server failed to bind its listener".to_owned();
                        diagnostics::record("host", "listening_failed");
                        let _ = ready_tx.send(Err(message));
                        return;
                    }
                }
                match server_task.await {
                    Ok(Ok(())) => diagnostics::record("host", "stopped"),
                    Ok(Err(error)) => diagnostics::record("host", &format!("stopped_{error}")),
                    Err(error) => {
                        diagnostics::record("host", &format!("server_task_failed_{error}"))
                    }
                }
            });
        }));
        if let Err(payload) = result {
            let detail = payload
                .downcast_ref::<&str>()
                .copied()
                .or_else(|| payload.downcast_ref::<String>().map(String::as_str))
                .unwrap_or("unknown panic payload");
            diagnostics::record("host", &format!("server_thread_panicked_{detail}"));
        }
    });
    match ready_rx.blocking_recv() {
        Ok(Ok(())) => Ok(return_endpoint),
        Ok(Err(error)) => {
            if let Ok(mut slot) = shutdown_slot.lock() {
                *slot = None;
            }
            Err(error)
        }
        Err(_) => {
            if let Ok(mut slot) = shutdown_slot.lock() {
                *slot = None;
            }
            Err("HTTPS host thread exited before it became ready".into())
        }
    }
}

fn ensure_host_firewall_rule(address: std::net::SocketAddr) -> Result<(), String> {
    if address.ip().is_loopback() {
        return Ok(());
    }

    #[cfg(windows)]
    {
        let port = address.port();
        if firewall_rule_exists(port) {
            diagnostics::record("host", "firewall_rule_present");
            return Ok(());
        }
        diagnostics::record("host", "firewall_rule_missing");
        if configure_firewall_rule(port) && firewall_rule_exists(port) {
            diagnostics::record("host", "firewall_rule_configured");
            return Ok(());
        }
        diagnostics::record("host", "firewall_rule_configure_failed");
        Err(format!(
            "Windows Firewall rule for TCP {port} was not created; the host was not started"
        ))
    }

    #[cfg(not(windows))]
    {
        let _ = address;
        Ok(())
    }
}

#[cfg(windows)]
fn firewall_rule_name(port: u16) -> String {
    format!("PocketAgent inbound TCP {port}")
}

#[cfg(windows)]
fn firewall_rule_command_line(port: u16) -> String {
    format!(
        "advfirewall firewall add rule name=\"{}\" dir=in action=allow protocol=TCP localport={port} profile=any edge=no",
        firewall_rule_name(port)
    )
}

#[cfg(windows)]
fn firewall_rule_exists(port: u16) -> bool {
    use std::os::windows::process::CommandExt;
    use std::process::Stdio;

    std::process::Command::new("netsh.exe")
        .args([
            "advfirewall",
            "firewall",
            "show",
            "rule",
            &format!("name={}", firewall_rule_name(port)),
        ])
        .creation_flags(0x0800_0000)
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .map(|status| status.success())
        .unwrap_or(false)
}

#[cfg(windows)]
fn configure_firewall_rule(port: u16) -> bool {
    use windows_sys::Win32::Foundation::{CloseHandle, WAIT_OBJECT_0};
    use windows_sys::Win32::System::Threading::{
        GetExitCodeProcess, INFINITE, WaitForSingleObject,
    };
    use windows_sys::Win32::UI::Shell::{
        SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW, ShellExecuteExW,
    };

    let arguments = firewall_rule_command_line(port);
    let verb = "runas\0".encode_utf16().collect::<Vec<_>>();
    let file = "netsh.exe\0".encode_utf16().collect::<Vec<_>>();
    let arguments = arguments
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    let mut execute = SHELLEXECUTEINFOW {
        cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOCLOSEPROCESS,
        lpVerb: verb.as_ptr(),
        lpFile: file.as_ptr(),
        lpParameters: arguments.as_ptr(),
        nShow: 0,
        ..Default::default()
    };
    let launched = unsafe { ShellExecuteExW(&mut execute) } != 0;
    if !launched || execute.hProcess.is_null() {
        return false;
    }
    let completed = unsafe { WaitForSingleObject(execute.hProcess, INFINITE) } == WAIT_OBJECT_0;
    let mut exit_code = 1u32;
    let succeeded = completed
        && unsafe { GetExitCodeProcess(execute.hProcess, &mut exit_code) != 0 }
        && exit_code == 0;
    unsafe { CloseHandle(execute.hProcess) };
    succeeded
}

fn start_relay_host(
    state: HostHttpState,
    relay_host_shutdown: Arc<Mutex<Option<oneshot::Sender<()>>>>,
) {
    let Ok(url) = env::var("POCKETAGENT_RELAY_HOST_URL") else {
        return;
    };
    let Ok(host_id) = env::var("POCKETAGENT_RELAY_HOST_ID")
        .ok()
        .and_then(|value| Uuid::parse_str(&value).ok())
        .ok_or(())
    else {
        diagnostics::record("relay_host", "invalid_host_id");
        return;
    };
    let Ok(token) = env::var("POCKETAGENT_RELAY_HOST_TOKEN") else {
        diagnostics::record("relay_host", "missing_token");
        return;
    };
    let Some(key) = env::var("POCKETAGENT_RELAY_SESSION_KEY")
        .ok()
        .and_then(|value| parse_session_key(&value))
    else {
        diagnostics::record("relay_host", "invalid_session_key");
        return;
    };
    start_relay_host_connection(state, url, host_id, token, key, None, relay_host_shutdown);
}

fn connect_relay_host_from_ui(
    state: HostHttpState,
    weak: slint::Weak<AppWindow>,
    relay_host_shutdown: Arc<Mutex<Option<oneshot::Sender<()>>>>,
    url: String,
    host_id: String,
    token: String,
    session_key: String,
) {
    let Some(host_id) = Uuid::parse_str(host_id.trim()).ok() else {
        set_relay_host_status(&weak, "Invalid relay host ID.".into());
        return;
    };
    let Some(key) = parse_session_key(session_key.trim()) else {
        set_relay_host_status(&weak, "Invalid relay session key.".into());
        return;
    };
    if url.trim().is_empty() || token.trim().is_empty() {
        set_relay_host_status(&weak, "Relay URL and token are required.".into());
        return;
    }
    set_relay_host_connected(&weak, false);
    set_relay_host_status(&weak, "Connecting outbound relay host...".into());
    start_relay_host_connection(
        state,
        url,
        host_id,
        token,
        key,
        Some(weak),
        relay_host_shutdown,
    );
}

fn start_relay_host_connection(
    state: HostHttpState,
    url: String,
    host_id: Uuid,
    token: String,
    key: SessionKey,
    status: Option<slint::Weak<AppWindow>>,
    relay_host_shutdown: Arc<Mutex<Option<oneshot::Sender<()>>>>,
) {
    if let Some(shutdown) = relay_host_shutdown
        .lock()
        .expect("relay host state lock should work")
        .take()
    {
        let _ = shutdown.send(());
    }
    diagnostics::record("relay_host", "connecting");
    std::thread::spawn(move || {
        let Ok(runtime) = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
        else {
            diagnostics::record("relay_host", "runtime_failed");
            if let Some(status) = status.as_ref() {
                set_relay_host_status(status, "Relay host runtime failed.".into());
            }
            return;
        };
        runtime.block_on(async move {
            let handler_state = state;
            let host = RelayApplicationHost::connect(
                &url,
                host_id,
                token.into_bytes(),
                key,
                move |request| {
                    let state = handler_state.clone();
                    async move {
                        let mut builder = axum::http::Request::builder()
                            .method(request.method.as_str())
                            .uri(request.path.as_str());
                        for (name, value) in request.headers {
                            if let (Ok(name), Ok(value)) = (
                                axum::http::header::HeaderName::from_bytes(name.as_bytes()),
                                axum::http::HeaderValue::from_str(&value),
                            ) {
                                builder = builder.header(name, value);
                            }
                        }
                        let Ok(http_request) = builder.body(axum::body::Body::from(request.body))
                        else {
                            return pocketagent_protocol::ApplicationResponse::new(
                                400,
                                "text/plain",
                                b"invalid relay request".to_vec(),
                            );
                        };
                        let response = match host_router(state).oneshot(http_request).await {
                            Ok(response) => response,
                            Err(error) => {
                                return pocketagent_protocol::ApplicationResponse::new(
                                    502,
                                    "text/plain",
                                    error.to_string().into_bytes(),
                                );
                            }
                        };
                        let status = response.status().as_u16();
                        let content_type = response
                            .headers()
                            .get(axum::http::header::CONTENT_TYPE)
                            .and_then(|value| value.to_str().ok())
                            .unwrap_or("application/octet-stream")
                            .to_owned();
                        let body = match axum::body::to_bytes(
                            response.into_body(),
                            16 * 1024 * 1024,
                        )
                        .await
                        {
                            Ok(body) => body.to_vec(),
                            Err(error) => error.to_string().into_bytes(),
                        };
                        pocketagent_protocol::ApplicationResponse::new(status, content_type, body)
                    }
                },
            )
            .await;
            match host {
                Ok(host) => {
                    let (shutdown_tx, shutdown_rx) = oneshot::channel();
                    *relay_host_shutdown
                        .lock()
                        .expect("relay host state lock should work") = Some(shutdown_tx);
                    diagnostics::record("relay_host", "ready");
                    if let Some(status) = status.as_ref() {
                        set_relay_host_status(status, "Outbound relay host connected.".into());
                        set_relay_host_connected(status, true);
                    }
                    if let Err(error) = host.serve_until(shutdown_rx).await {
                        diagnostics::record("relay_host", &format!("stopped_{error}"));
                        if let Some(status) = status.as_ref() {
                            set_relay_host_status(
                                status,
                                format!("Outbound relay host stopped: {error}"),
                            );
                            set_relay_host_connected(status, false);
                        }
                    } else if let Some(status) = status.as_ref() {
                        set_relay_host_status(status, "Outbound relay host disconnected.".into());
                        set_relay_host_connected(status, false);
                    }
                }
                Err(error) => {
                    diagnostics::record("relay_host", &format!("connect_failed_{error}"));
                    if let Some(status) = status.as_ref() {
                        set_relay_host_status(
                            status,
                            format!("Outbound relay host failed: {error}"),
                        );
                        set_relay_host_connected(status, false);
                    }
                }
            }
        });
    });
}

fn parse_session_key(value: &str) -> Option<SessionKey> {
    let (id, bytes) = value.split_once(':')?;
    let id = Uuid::parse_str(id).ok()?;
    if bytes.len() != 64 {
        return None;
    }
    let mut decoded = [0u8; 32];
    for (index, chunk) in bytes.as_bytes().chunks(2).enumerate() {
        decoded[index] = u8::from_str_radix(std::str::from_utf8(chunk).ok()?, 16).ok()?;
    }
    Some(SessionKey::from_bytes(id, decoded))
}

struct ParsedBlock {
    text: String,
    kind: String,
    role: String,
    summary: String,
    collapsible: bool,
    image_path: Option<String>,
}

const TRANSCRIPT_MARKER: char = '\u{001e}';

fn markdown_blocks(markdown: &str) -> Vec<ParsedBlock> {
    if !markdown.contains(TRANSCRIPT_MARKER) {
        return markdown_blocks_segment(markdown, "system", false, "");
    }
    markdown
        .split(TRANSCRIPT_MARKER)
        .enumerate()
        .filter_map(|(index, segment)| {
            if segment.is_empty() {
                return None;
            }
            let (header, body) = segment.split_once('\n').unwrap_or(("system", segment));
            if index == 0 {
                return Some(markdown_blocks_segment(segment, "system", false, ""));
            }
            if let Some(summary) = header.strip_prefix("activity|") {
                return Some(vec![ParsedBlock {
                    text: body.trim().to_owned(),
                    kind: "activity".into(),
                    role: "activity".into(),
                    summary: summary.trim().to_owned(),
                    collapsible: true,
                    image_path: None,
                }]);
            }
            Some(markdown_blocks_segment(body, header, false, ""))
        })
        .flatten()
        .collect()
}

fn markdown_blocks_segment(
    markdown: &str,
    role: &str,
    collapsible: bool,
    summary: &str,
) -> Vec<ParsedBlock> {
    let mut blocks = Vec::new();
    let mut current = String::new();
    let mut kind = "paragraph".to_owned();
    let mut image_path = None;
    let mut list_depth = 0usize;
    let parser = Parser::new_ext(markdown, Options::all());
    for event in parser {
        match event {
            Event::Start(Tag::Heading { level, .. }) => {
                flush_block(
                    &mut blocks,
                    &mut current,
                    &mut kind,
                    &mut image_path,
                    role,
                    collapsible,
                    summary,
                );
                kind = if matches!(level, HeadingLevel::H1 | HeadingLevel::H2) {
                    "heading".into()
                } else {
                    "subheading".into()
                };
            }
            Event::End(TagEnd::Heading(_)) => flush_block(
                &mut blocks,
                &mut current,
                &mut kind,
                &mut image_path,
                role,
                collapsible,
                summary,
            ),
            Event::Start(Tag::Paragraph) => {
                if current.is_empty() {
                    kind = "paragraph".into();
                }
            }
            Event::End(TagEnd::Paragraph) => flush_block(
                &mut blocks,
                &mut current,
                &mut kind,
                &mut image_path,
                role,
                collapsible,
                summary,
            ),
            Event::Start(Tag::Item) => {
                flush_block(
                    &mut blocks,
                    &mut current,
                    &mut kind,
                    &mut image_path,
                    role,
                    collapsible,
                    summary,
                );
                kind = "bullet".into();
                current.push_str("* ");
                list_depth += 1;
            }
            Event::End(TagEnd::Item) => {
                flush_block(
                    &mut blocks,
                    &mut current,
                    &mut kind,
                    &mut image_path,
                    role,
                    collapsible,
                    summary,
                );
                list_depth = list_depth.saturating_sub(1);
            }
            Event::Start(Tag::CodeBlock(_)) => {
                flush_block(
                    &mut blocks,
                    &mut current,
                    &mut kind,
                    &mut image_path,
                    role,
                    collapsible,
                    summary,
                );
                kind = "code".into();
            }
            Event::End(TagEnd::CodeBlock) => flush_block(
                &mut blocks,
                &mut current,
                &mut kind,
                &mut image_path,
                role,
                collapsible,
                summary,
            ),
            Event::Start(Tag::Image { dest_url, .. }) => {
                flush_block(
                    &mut blocks,
                    &mut current,
                    &mut kind,
                    &mut image_path,
                    role,
                    collapsible,
                    summary,
                );
                kind = "image".into();
                image_path = Some(dest_url.to_string());
            }
            Event::End(TagEnd::Image) => flush_block(
                &mut blocks,
                &mut current,
                &mut kind,
                &mut image_path,
                role,
                collapsible,
                summary,
            ),
            Event::Text(text) | Event::Code(text) => current.push_str(&text),
            Event::SoftBreak | Event::HardBreak => current.push('\n'),
            Event::Rule => {
                flush_block(
                    &mut blocks,
                    &mut current,
                    &mut kind,
                    &mut image_path,
                    role,
                    collapsible,
                    summary,
                );
                blocks.push(ParsedBlock {
                    text: "---".into(),
                    kind: "rule".into(),
                    role: role.into(),
                    summary: summary.into(),
                    collapsible,
                    image_path: None,
                });
            }
            Event::Start(Tag::List(_)) => {
                if list_depth == 0 {
                    flush_block(
                        &mut blocks,
                        &mut current,
                        &mut kind,
                        &mut image_path,
                        role,
                        collapsible,
                        summary,
                    );
                }
            }
            Event::End(TagEnd::List(_)) => flush_block(
                &mut blocks,
                &mut current,
                &mut kind,
                &mut image_path,
                role,
                collapsible,
                summary,
            ),
            _ => {}
        }
    }
    flush_block(
        &mut blocks,
        &mut current,
        &mut kind,
        &mut image_path,
        role,
        collapsible,
        summary,
    );
    blocks
}

fn flush_block(
    blocks: &mut Vec<ParsedBlock>,
    current: &mut String,
    kind: &mut String,
    image_path: &mut Option<String>,
    role: &str,
    collapsible: bool,
    summary: &str,
) {
    let text = current.trim_end_matches('\n').to_owned();
    if !text.is_empty() || image_path.is_some() {
        blocks.push(ParsedBlock {
            text,
            kind: kind.clone(),
            role: role.into(),
            summary: summary.into(),
            collapsible,
            image_path: image_path.take(),
        });
    }
    current.clear();
    *kind = "paragraph".into();
    *image_path = None;
}

#[cfg(test)]
mod tests {
    #[cfg(windows)]
    use super::firewall_rule_name;
    use super::{
        can_queue_usage_exhausted, codex_status_available, decode_data_image, endpoint_host,
        firewall_rule_command_line, host_certificate_names, image_from_bytes, is_public_ipv4,
        is_read_only_session, markdown_blocks, normalize_model_selection, parse_session_key,
        resolve_advertised_endpoint, resolve_codex_from_paths, schedule_pending,
        server_certificate_params,
    };
    use crate::store::{DesktopStore, ScheduledMessage};
    use std::collections::HashSet;
    use uuid::Uuid;

    #[cfg(windows)]
    #[test]
    fn firewall_rule_is_port_based_and_stable_across_builds() {
        let arguments = firewall_rule_command_line(8788);
        assert_eq!(firewall_rule_name(8788), "PocketAgent inbound TCP 8788");
        assert!(arguments.contains("localport=8788"));
        assert!(arguments.contains("profile=any"));
        assert!(arguments.contains("name=\"PocketAgent inbound TCP 8788\""));
        assert!(!arguments.contains(".exe"));
    }

    #[test]
    fn server_certificate_is_a_leaf_with_server_auth_usage() {
        let params = server_certificate_params(vec!["localhost".into()]).unwrap();
        assert!(matches!(params.is_ca, rcgen::IsCa::NoCa));
        assert!(
            params
                .extended_key_usages
                .iter()
                .any(|usage| matches!(usage, rcgen::ExtendedKeyUsagePurpose::ServerAuth))
        );
    }

    #[test]
    fn android_history_preserves_message_roles_and_activity() {
        let items = super::history_items(
            "fixture",
            "\u{001e}user\nHello\n\n\u{001e}assistant\nHi\n\n\u{001e}activity\ncommand output",
        );
        assert_eq!(items.len(), 3);
        assert_eq!(items[0]["type"], "userMessage");
        assert_eq!(items[0]["text"], "Hello");
        assert_eq!(items[1]["type"], "agentMessage");
        assert_eq!(items[1]["text"], "Hi");
        assert_eq!(items[2]["detail"], "command output");
    }

    #[test]
    fn markdown_blocks_preserve_headings_bullets_and_code() {
        let blocks = markdown_blocks("# Heading\n\n- one\n- two\n\n```rust\nlet x = 1;\n```");
        assert_eq!(blocks[0].kind, "heading");
        assert_eq!(blocks[1].text, "* one");
        assert_eq!(blocks[2].text, "* two");
        assert_eq!(blocks[3].kind, "code");
    }

    #[test]
    fn model_selection_uses_provider_default_label_without_forcing_configured_model() {
        assert_eq!(normalize_model_selection("Use Codex settings"), "");
        assert_eq!(normalize_model_selection(""), "");
        assert_eq!(normalize_model_selection("gpt-6-astra"), "gpt-6-astra");
    }

    #[test]
    fn active_writer_mirror_blocks_local_commands_but_keeps_other_sessions_writable() {
        let mut read_only = HashSet::new();
        read_only.insert("thread-mirrored".to_owned());
        assert!(is_read_only_session(&read_only, Some("thread-mirrored")));
        assert!(!is_read_only_session(&read_only, Some("thread-writable")));
        assert!(!is_read_only_session(&read_only, None));
    }

    #[test]
    fn codex_resolution_prefers_latest_vscode_native_binary_over_npm_wrapper() {
        let root = std::env::temp_dir().join(format!(
            "pocketagent-codex-resolver-{}",
            uuid::Uuid::new_v4()
        ));
        let extensions = root.join("profile/.vscode/extensions");
        let older = extensions.join("openai.chatgpt-1.2.3/bin/windows-x86_64/codex.exe");
        let newer = extensions.join("openai.chatgpt-2.4.1/bin/windows-x86_64/codex.exe");
        let npm = root.join("appdata/npm/codex.ps1");
        std::fs::create_dir_all(older.parent().unwrap()).unwrap();
        std::fs::create_dir_all(newer.parent().unwrap()).unwrap();
        std::fs::create_dir_all(npm.parent().unwrap()).unwrap();
        std::fs::write(&older, b"").unwrap();
        std::fs::write(&newer, b"").unwrap();
        std::fs::write(&npm, b"").unwrap();

        let resolved = resolve_codex_from_paths(
            Some(root.join("appdata")),
            Some(root.join("profile")),
            Some(""),
        );
        assert_eq!(std::path::PathBuf::from(resolved), newer);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn markdown_blocks_capture_image_destinations() {
        let blocks = markdown_blocks("![diagram](C:/repo/output.png)");
        assert_eq!(blocks[0].kind, "image");
        assert_eq!(blocks[0].image_path.as_deref(), Some("C:/repo/output.png"));
    }

    #[test]
    fn transcript_blocks_preserve_roles_and_collapsible_activity() {
        let blocks = markdown_blocks(
            "\u{001e}user\nhello\n\u{001e}activity|Command: cargo test\ncargo test\n\npassed\n\u{001e}assistant\ndone",
        );
        assert_eq!(blocks[0].role, "user");
        assert_eq!(blocks[1].role, "activity");
        assert!(blocks[1].collapsible);
        assert_eq!(blocks[1].summary, "Command: cargo test");
        assert_eq!(blocks[2].role, "assistant");
    }

    #[test]
    fn relay_session_key_parser_requires_uuid_and_32_bytes() {
        let id = Uuid::new_v4();
        let value = format!("{id}:{}", "ab".repeat(32));
        let key = parse_session_key(&value).expect("valid relay key should parse");
        assert_eq!(key.id(), id);
        assert!(parse_session_key("not-a-key").is_none());
        assert!(parse_session_key(&format!("{id}:00")).is_none());
    }

    #[test]
    fn canceled_schedule_is_not_pending_for_dispatch() {
        let mut store = DesktopStore::default();
        store.register_thread("thread-1".into(), "Test thread".into());
        store.schedules.push(ScheduledMessage {
            id: "job-1".into(),
            conversation_id: "thread-1".into(),
            prompt: "hello".into(),
            due_at_unix: 10,
            model: None,
            reasoning_effort: None,
            status: "pending".into(),
        });
        assert!(schedule_pending(&store, "job-1"));
        // Old stores could contain an archived conversation with a pending job.
        store.conversations[0].archived = true;
        assert!(!schedule_pending(&store, "job-1"));
        store.conversations[0].archived = false;
        store.schedules[0].status = "canceled".into();
        assert!(!schedule_pending(&store, "job-1"));
    }

    #[test]
    fn exhausted_usage_only_defers_prompts_with_a_target() {
        assert!(can_queue_usage_exhausted(Some("job-1"), None));
        assert!(can_queue_usage_exhausted(None, Some("thread-1")));
        assert!(!can_queue_usage_exhausted(None, None));
    }

    #[test]
    fn provider_errors_preserve_composer_when_the_session_is_connected() {
        assert!(codex_status_available("Codex is ready", false));
        assert!(codex_status_available("Codex error: turn failed", true));
        assert!(!codex_status_available(
            "Codex error: connect failed",
            false
        ));
        assert!(!codex_status_available("Connecting to Codex...", true));
        assert!(!codex_status_available("Codex is disconnected", true));
    }

    #[test]
    fn data_uri_images_are_decoded_for_the_native_renderer() {
        let source = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        let bytes = decode_data_image(source).expect("base64 image data should decode");
        assert!(image_from_bytes(&bytes).is_some());
    }

    #[test]
    fn pairing_advertises_a_reachable_host_by_default() {
        let requested = "0.0.0.0:8788".parse().unwrap();
        let bound = "0.0.0.0:8788".parse().unwrap();
        let endpoint = resolve_advertised_endpoint(
            requested,
            bound,
            None,
            Some("203.0.113.42".parse().unwrap()),
        );
        assert_eq!(endpoint, "203.0.113.42:8788");

        let endpoint = resolve_advertised_endpoint(requested, bound, Some("192.168.1.43"), None);
        assert_eq!(endpoint, "192.168.1.43:8788");
    }

    #[test]
    fn public_ipv4_filter_rejects_private_and_reserved_ranges() {
        assert!(is_public_ipv4("8.8.8.8".parse().unwrap()));
        assert!(!is_public_ipv4("192.168.1.42".parse().unwrap()));
        assert!(!is_public_ipv4("100.64.0.1".parse().unwrap()));
        assert!(!is_public_ipv4("169.254.1.1".parse().unwrap()));
    }

    #[test]
    fn pairing_endpoint_extracts_the_host_for_certificate_pinning() {
        assert_eq!(
            endpoint_host("https://203.0.113.42:8788"),
            Some("203.0.113.42")
        );
        assert_eq!(endpoint_host("http://127.0.0.1:43123"), Some("127.0.0.1"));
        assert_eq!(endpoint_host("not-an-endpoint"), None);
    }

    #[test]
    fn explicit_loopback_bind_remains_local_for_development() {
        let requested = "127.0.0.1:0".parse().unwrap();
        let bound = "127.0.0.1:43123".parse().unwrap();
        let endpoint = resolve_advertised_endpoint(requested, bound, None, None);
        assert_eq!(endpoint, "127.0.0.1:43123");
    }

    #[test]
    fn generated_certificate_names_include_the_detected_lan_address() {
        let names = host_certificate_names(Some("pocketagent.local"), Some("192.168.1.42"));
        assert!(names.iter().any(|name| name == "localhost"));
        assert!(names.iter().any(|name| name == "127.0.0.1"));
        assert!(names.iter().any(|name| name == "pocketagent.local"));
        assert!(names.iter().any(|name| name == "192.168.1.42"));
    }
}
