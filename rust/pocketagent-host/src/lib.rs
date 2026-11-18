//! Authentication and conversation-access primitives for the desktop host.
//!
//! The host keeps provider credentials local. Devices receive short-lived pairing
//! credentials and are granted access to individual conversations; possession of a
//! host or relay connection alone does not grant conversation access.

use axum::{
    Json, Router,
    extract::{Path, Query, State},
    http::{HeaderMap, StatusCode},
    response::IntoResponse,
    routing::{get, post},
};
use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use rand::{Rng, rng};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet, VecDeque},
    fs, io,
    path::{Path as FsPath, PathBuf},
    sync::{Arc, Mutex, RwLock},
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use subtle::ConstantTimeEq;
use thiserror::Error;
use uuid::Uuid;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct HostConversation {
    pub id: String,
    pub title: String,
    pub archived: bool,
    pub pinned: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct HostSchedule {
    pub id: String,
    pub conversation_id: String,
    pub text: String,
    pub due_at: String,
    pub status: String,
}

#[derive(Debug)]
pub enum HostAction {
    Send {
        conversation_id: String,
        text: String,
    },
    Archive {
        conversation_id: String,
        archived: bool,
    },
    Pin {
        conversation_id: String,
        pinned: bool,
    },
    Rename {
        conversation_id: String,
        title: String,
    },
    Create {
        device_id: Uuid,
        title: String,
        model: String,
        effort: String,
        response: tokio::sync::oneshot::Sender<Result<serde_json::Value, String>>,
    },
    Schedule {
        conversation_id: String,
        text: String,
        due_at_unix: i64,
        response: tokio::sync::oneshot::Sender<Result<serde_json::Value, String>>,
    },
    CancelSchedule {
        schedule_id: String,
        response: tokio::sync::oneshot::Sender<Result<(), String>>,
    },
    Read {
        conversation_id: String,
        response: tokio::sync::oneshot::Sender<Result<serde_json::Value, String>>,
    },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionInfo {
    pub authenticated: bool,
    pub device_name: Option<String>,
    pub repository: Option<String>,
    pub host_name: String,
    pub version: String,
}

type SessionProvider = dyn Fn(Option<&DeviceIdentity>) -> SessionInfo + Send + Sync;

#[derive(Clone)]
pub struct HostHttpState {
    pub registry: DeviceRegistry,
    pub conversations: Arc<RwLock<Vec<HostConversation>>>,
    pub schedules: Arc<RwLock<Vec<HostSchedule>>>,
    pub image_root: Arc<RwLock<String>>,
    pub models: Arc<RwLock<String>>,
    pub usage: Arc<RwLock<String>>,
    pub provider_status: Arc<RwLock<String>>,
    pub actions: tokio::sync::mpsc::UnboundedSender<HostAction>,
    pub session: Arc<SessionProvider>,
}

impl HostHttpState {
    pub fn new(
        registry: DeviceRegistry,
        actions: tokio::sync::mpsc::UnboundedSender<HostAction>,
        session: impl Fn(Option<&DeviceIdentity>) -> SessionInfo + Send + Sync + 'static,
    ) -> Self {
        Self {
            registry,
            conversations: Arc::new(RwLock::new(Vec::new())),
            schedules: Arc::new(RwLock::new(Vec::new())),
            image_root: Arc::new(RwLock::new(String::new())),
            models: Arc::new(RwLock::new(String::new())),
            usage: Arc::new(RwLock::new(String::new())),
            provider_status: Arc::new(RwLock::new("disconnected".into())),
            actions,
            session: Arc::new(session),
        }
    }
}

const PAIRING_LIFETIME: Duration = Duration::from_secs(10 * 60);
const TOKEN_LIFETIME: Duration = Duration::from_secs(365 * 24 * 60 * 60);
const MAX_PAIRING_ATTEMPTS: usize = 10;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct DeviceIdentity {
    pub id: Uuid,
    pub name: String,
    pub expires_at_unix: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct PairingDisplay {
    pub code: String,
    pub expires_at_unix: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct DeviceAccess {
    pub id: Uuid,
    pub name: String,
    pub authorized: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct PairedDevice {
    pub identity: DeviceIdentity,
    /// Returned only once, from the pairing operation. Store it on the client.
    pub token: String,
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum HostAuthError {
    #[error("device name must be between 1 and 80 characters")]
    InvalidDeviceName,
    #[error("pairing code is invalid, expired, used, or rate limited")]
    InvalidPairingCode,
    #[error("device token is invalid or expired")]
    InvalidToken,
    #[error("device is not authorized for this conversation")]
    ConversationDenied,
    #[error("could not persist device authorization")]
    Persistence,
}

#[derive(Debug, Serialize, Deserialize)]
struct RegistryState {
    code: String,
    expires_at_unix: i64,
    consumed: bool,
    attempts: VecDeque<i64>,
    devices: HashMap<Uuid, DeviceRecord>,
    conversations: HashMap<String, HashSet<Uuid>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct DeviceRecord {
    identity: DeviceIdentity,
    token_digest: [u8; 32],
    revoked: bool,
}

#[derive(Clone, Debug)]
pub struct DeviceRegistry {
    state: Arc<Mutex<RegistryState>>,
    persistence_path: Option<Arc<PathBuf>>,
}

impl Default for DeviceRegistry {
    fn default() -> Self {
        Self::new()
    }
}

impl DeviceRegistry {
    pub fn new() -> Self {
        let now = now_unix();
        Self {
            state: Arc::new(Mutex::new(RegistryState {
                code: new_pairing_code(),
                expires_at_unix: now + PAIRING_LIFETIME.as_secs() as i64,
                consumed: false,
                attempts: VecDeque::new(),
                devices: HashMap::new(),
                conversations: HashMap::new(),
            })),
            persistence_path: None,
        }
    }

    pub fn load(path: impl AsRef<FsPath>) -> io::Result<Self> {
        let path = path.as_ref().to_owned();
        if !path.exists() {
            return Ok(Self {
                persistence_path: Some(Arc::new(path)),
                ..Self::new()
            });
        }
        let bytes = fs::read(&path)?;
        let state = serde_json::from_slice(&bytes).map_err(io::Error::other)?;
        Ok(Self {
            state: Arc::new(Mutex::new(state)),
            persistence_path: Some(Arc::new(path)),
        })
    }

    pub fn save(&self) -> io::Result<()> {
        let Some(path) = self.persistence_path.as_deref() else {
            return Ok(());
        };
        let state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        save_state(path, &state)
    }

    pub fn pairing_display(&self) -> PairingDisplay {
        let state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        PairingDisplay {
            code: state.code.clone(),
            expires_at_unix: state.expires_at_unix,
        }
    }

    pub fn renew_pairing(&self) -> PairingDisplay {
        let mut state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        state.code = new_pairing_code();
        state.expires_at_unix = now_unix() + PAIRING_LIFETIME.as_secs() as i64;
        state.consumed = false;
        state.attempts.clear();
        let _ = self.save_state(&state);
        PairingDisplay {
            code: state.code.clone(),
            expires_at_unix: state.expires_at_unix,
        }
    }

    pub fn pair(&self, name: &str, candidate: &str) -> Result<PairedDevice, HostAuthError> {
        let name = name.trim();
        if name.is_empty() || name.chars().count() > 80 {
            return Err(HostAuthError::InvalidDeviceName);
        }
        let mut state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        let now = now_unix();
        while state
            .attempts
            .front()
            .is_some_and(|attempt| *attempt <= now - 60)
        {
            state.attempts.pop_front();
        }
        if state.attempts.len() >= MAX_PAIRING_ATTEMPTS
            || state.consumed
            || now >= state.expires_at_unix
        {
            return Err(HostAuthError::InvalidPairingCode);
        }
        state.attempts.push_back(now);
        if !constant_time_equal(candidate.as_bytes(), state.code.as_bytes()) {
            return Err(HostAuthError::InvalidPairingCode);
        }
        state.consumed = true;
        let id = Uuid::new_v4();
        let expires_at_unix = now + TOKEN_LIFETIME.as_secs() as i64;
        let token = new_token();
        state.devices.insert(
            id,
            DeviceRecord {
                identity: DeviceIdentity {
                    id,
                    name: name.to_owned(),
                    expires_at_unix,
                },
                token_digest: digest(&token),
                revoked: false,
            },
        );
        let paired = PairedDevice {
            identity: DeviceIdentity {
                id,
                name: name.to_owned(),
                expires_at_unix,
            },
            token,
        };
        if self.save_state(&state).is_err() {
            return Err(HostAuthError::Persistence);
        }
        Ok(paired)
    }

    pub fn authenticate(&self, token: &str) -> Option<DeviceIdentity> {
        let digest = digest(token);
        let now = now_unix();
        let state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        state
            .devices
            .values()
            .find(|device| {
                !device.revoked
                    && device.identity.expires_at_unix > now
                    && device.token_digest.ct_eq(&digest).into()
            })
            .map(|device| device.identity.clone())
    }

    pub fn revoke(&self, device_id: Uuid) -> bool {
        let mut state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        let Some(device) = state.devices.get_mut(&device_id) else {
            return false;
        };
        device.revoked = true;
        for devices in state.conversations.values_mut() {
            devices.remove(&device_id);
        }
        let _ = self.save_state(&state);
        true
    }

    pub fn grant_conversation(
        &self,
        conversation_id: impl Into<String>,
        device_id: Uuid,
    ) -> Result<(), HostAuthError> {
        let mut state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        if !state.devices.contains_key(&device_id) {
            return Err(HostAuthError::InvalidToken);
        }
        state
            .conversations
            .entry(conversation_id.into())
            .or_default()
            .insert(device_id);
        let _ = self.save_state(&state);
        Ok(())
    }

    pub fn revoke_conversation(&self, conversation_id: &str, device_id: Uuid) -> bool {
        let mut state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        let changed = state
            .conversations
            .get_mut(conversation_id)
            .is_some_and(|devices| devices.remove(&device_id));
        if changed {
            let _ = self.save_state(&state);
        }
        changed
    }

    pub fn can_access(&self, conversation_id: &str, device_id: Uuid) -> bool {
        let state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        state
            .conversations
            .get(conversation_id)
            .is_some_and(|devices| devices.contains(&device_id))
    }

    pub fn authorized_conversations(&self, device_id: Uuid) -> Vec<String> {
        let state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        state
            .conversations
            .iter()
            .filter(|(_, devices)| devices.contains(&device_id))
            .map(|(id, _)| id.clone())
            .collect()
    }

    pub fn device_access(&self, conversation_id: &str) -> Vec<DeviceAccess> {
        let state = self
            .state
            .lock()
            .expect("registry lock should not be poisoned");
        let authorized = state.conversations.get(conversation_id);
        state
            .devices
            .values()
            .filter(|device| !device.revoked && device.identity.expires_at_unix > now_unix())
            .map(|device| DeviceAccess {
                id: device.identity.id,
                name: device.identity.name.clone(),
                authorized: authorized.is_some_and(|devices| devices.contains(&device.identity.id)),
            })
            .collect()
    }
}

impl DeviceRegistry {
    fn save_state(&self, state: &RegistryState) -> io::Result<()> {
        let Some(path) = self.persistence_path.as_deref() else {
            return Ok(());
        };
        save_state(path, state)
    }
}

fn save_state(path: &FsPath, state: &RegistryState) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let temporary = path.with_extension("json.tmp");
    fs::write(
        &temporary,
        serde_json::to_vec_pretty(state).map_err(io::Error::other)?,
    )?;
    match fs::rename(&temporary, path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {
            fs::remove_file(path)?;
            fs::rename(temporary, path)
        }
        Err(error) => Err(error),
    }
}

#[derive(Debug, Deserialize)]
struct PairInput {
    code: Option<String>,
    #[serde(rename = "deviceName")]
    device_name: Option<String>,
}

#[derive(Debug, Deserialize)]
struct MessageInput {
    text: Option<String>,
}

#[derive(Debug, Deserialize)]
struct CreateInput {
    title: Option<String>,
    model: Option<String>,
    #[serde(rename = "reasoningEffort")]
    reasoning_effort: Option<String>,
}

#[derive(Debug, Deserialize)]
struct PinInput {
    pinned: bool,
}

#[derive(Debug, Deserialize)]
struct RenameInput {
    title: Option<String>,
}

#[derive(Debug, Deserialize, Default)]
struct ArchivedQuery {
    archived: Option<bool>,
}

#[derive(Debug, Deserialize)]
struct ImageQuery {
    path: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ScheduleInput {
    text: Option<String>,
    #[serde(rename = "dueAt")]
    due_at: Option<String>,
}

pub fn router(state: HostHttpState) -> Router {
    Router::new()
        .route("/", get(root))
        .route("/api/session", get(session))
        .route("/api/provider/status", get(provider_status))
        .route("/api/models", get(models))
        .route("/api/usage", get(usage))
        .route("/api/devices/pair", post(pair))
        .route(
            "/api/conversations",
            get(list_conversations).post(create_conversation),
        )
        .route("/api/conversations/{id}", get(get_conversation))
        .route("/api/conversations/{id}/history", get(get_history))
        .route("/api/conversations/{id}/images", get(get_image))
        .route("/api/scheduled", get(list_schedules))
        .route(
            "/api/conversations/{id}/scheduled",
            get(list_conversation_schedules).post(schedule),
        )
        .route("/api/scheduled/{id}/cancel", post(cancel_schedule))
        .route("/api/conversations/{id}/messages", post(send_message))
        .route("/api/conversations/{id}/archive", post(archive))
        .route("/api/conversations/{id}/restore", post(restore))
        .route("/api/conversations/{id}/pin", post(pin))
        .route("/api/conversations/{id}/rename", post(rename))
        .with_state(state)
}

async fn root() -> impl IntoResponse {
    Json(serde_json::json!({ "application": "PocketAgent Rust Host" }))
}

async fn session(State(state): State<HostHttpState>, headers: HeaderMap) -> impl IntoResponse {
    let identity = authenticate_headers(&state.registry, &headers);
    Json((state.session)(identity.as_ref()))
}

async fn provider_status(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
) -> impl IntoResponse {
    if require_identity(&state.registry, &headers).is_none() {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    let status = state
        .provider_status
        .read()
        .expect("provider status lock should not be poisoned")
        .clone();
    Json(serde_json::json!({ "status": status })).into_response()
}

async fn models(State(state): State<HostHttpState>, headers: HeaderMap) -> impl IntoResponse {
    if require_identity(&state.registry, &headers).is_none() {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    let models = state
        .models
        .read()
        .expect("model lock should not be poisoned")
        .clone();
    Json(serde_json::json!({ "models": models.lines().map(str::to_owned).collect::<Vec<_>>() }))
        .into_response()
}

async fn usage(State(state): State<HostHttpState>, headers: HeaderMap) -> impl IntoResponse {
    if require_identity(&state.registry, &headers).is_none() {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    let summary = state
        .usage
        .read()
        .expect("usage lock should not be poisoned")
        .clone();
    Json(serde_json::json!({ "summary": summary })).into_response()
}

async fn pair(
    State(state): State<HostHttpState>,
    Json(input): Json<PairInput>,
) -> impl IntoResponse {
    let Some(code) = input.code.as_deref() else {
        return auth_error(StatusCode::UNAUTHORIZED, HostAuthError::InvalidPairingCode);
    };
    let Some(name) = input.device_name.as_deref() else {
        return auth_error(StatusCode::BAD_REQUEST, HostAuthError::InvalidDeviceName);
    };
    match state.registry.pair(name, code) {
        Ok(paired) => (
            StatusCode::OK,
            Json(serde_json::json!({
                "token": paired.token,
                "deviceId": paired.identity.id,
                "expiresAt": paired.identity.expires_at_unix,
                "deviceName": paired.identity.name
            })),
        )
            .into_response(),
        Err(error) => auth_error(StatusCode::UNAUTHORIZED, error),
    }
}

async fn create_conversation(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Json(input): Json<CreateInput>,
) -> impl IntoResponse {
    let Some(identity) = require_identity(&state.registry, &headers) else {
        return StatusCode::UNAUTHORIZED.into_response();
    };
    let (sender, receiver) = tokio::sync::oneshot::channel();
    if state
        .actions
        .send(HostAction::Create {
            device_id: identity.id,
            title: input.title.unwrap_or_default(),
            model: input.model.unwrap_or_default(),
            effort: input.reasoning_effort.unwrap_or_default(),
            response: sender,
        })
        .is_err()
    {
        return StatusCode::SERVICE_UNAVAILABLE.into_response();
    }
    match tokio::time::timeout(std::time::Duration::from_secs(30), receiver).await {
        Ok(Ok(Ok(snapshot))) => (StatusCode::OK, Json(snapshot)).into_response(),
        Ok(Ok(Err(_))) | Ok(Err(_)) => StatusCode::BAD_GATEWAY.into_response(),
        Err(_) => StatusCode::GATEWAY_TIMEOUT.into_response(),
    }
}

async fn list_conversations(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Query(query): Query<ArchivedQuery>,
) -> impl IntoResponse {
    let Some(identity) = require_identity(&state.registry, &headers) else {
        return StatusCode::UNAUTHORIZED.into_response();
    };
    let archived = query.archived.unwrap_or(false);
    let items = state
        .conversations
        .read()
        .expect("conversation lock should not be poisoned")
        .iter()
        .filter(|conversation| {
            conversation.archived == archived
                && state.registry.can_access(&conversation.id, identity.id)
        })
        .cloned()
        .collect::<Vec<_>>();
    Json(items).into_response()
}

async fn send_message(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Json(input): Json<MessageInput>,
) -> impl IntoResponse {
    let Some(identity) = require_identity(&state.registry, &headers) else {
        return StatusCode::UNAUTHORIZED.into_response();
    };
    if !state.registry.can_access(&id, identity.id) {
        return StatusCode::NOT_FOUND.into_response();
    }
    let Some(text) = input.text.filter(|text| !text.trim().is_empty()) else {
        return StatusCode::BAD_REQUEST.into_response();
    };
    if state
        .actions
        .send(HostAction::Send {
            conversation_id: id,
            text,
        })
        .is_ok()
    {
        StatusCode::ACCEPTED.into_response()
    } else {
        StatusCode::SERVICE_UNAVAILABLE.into_response()
    }
}

async fn get_conversation(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> impl IntoResponse {
    read_conversation(state, headers, id, false).await
}

async fn get_history(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> impl IntoResponse {
    read_conversation(state, headers, id, true).await
}

async fn get_image(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Query(query): Query<ImageQuery>,
) -> impl IntoResponse {
    let Some(identity) = require_identity(&state.registry, &headers) else {
        return StatusCode::UNAUTHORIZED.into_response();
    };
    if !state.registry.can_access(&id, identity.id) {
        return StatusCode::NOT_FOUND.into_response();
    }
    let Some(relative) = query.path.filter(|path| !path.trim().is_empty()) else {
        return StatusCode::BAD_REQUEST.into_response();
    };
    if relative.starts_with("data:") || std::path::Path::new(&relative).is_absolute() {
        return StatusCode::BAD_REQUEST.into_response();
    }
    let root = state
        .image_root
        .read()
        .expect("image root lock should not be poisoned")
        .clone();
    let Ok(root) = std::fs::canonicalize(root) else {
        return StatusCode::NOT_FOUND.into_response();
    };
    let candidate = root.join(relative);
    let Ok(candidate) = std::fs::canonicalize(candidate) else {
        return StatusCode::NOT_FOUND.into_response();
    };
    if !candidate.starts_with(&root) {
        return StatusCode::NOT_FOUND.into_response();
    }
    let Ok(bytes) = std::fs::read(&candidate) else {
        return StatusCode::NOT_FOUND.into_response();
    };
    let content_type = match candidate
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or_default()
        .to_ascii_lowercase()
        .as_str()
    {
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
    ([(axum::http::header::CONTENT_TYPE, content_type)], bytes).into_response()
}

async fn list_schedules(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
) -> impl IntoResponse {
    let Some(identity) = require_identity(&state.registry, &headers) else {
        return StatusCode::UNAUTHORIZED.into_response();
    };
    let schedules = state
        .schedules
        .read()
        .expect("schedule lock should not be poisoned")
        .iter()
        .filter(|schedule| {
            state
                .registry
                .can_access(&schedule.conversation_id, identity.id)
        })
        .cloned()
        .collect::<Vec<_>>();
    Json(schedules).into_response()
}

async fn list_conversation_schedules(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> impl IntoResponse {
    let Some(identity) = require_identity(&state.registry, &headers) else {
        return StatusCode::UNAUTHORIZED.into_response();
    };
    if !state.registry.can_access(&id, identity.id) {
        return StatusCode::NOT_FOUND.into_response();
    }
    let schedules = state
        .schedules
        .read()
        .expect("schedule lock should not be poisoned")
        .iter()
        .filter(|schedule| schedule.conversation_id == id)
        .cloned()
        .collect::<Vec<_>>();
    Json(schedules).into_response()
}

async fn schedule(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Json(input): Json<ScheduleInput>,
) -> impl IntoResponse {
    let Some(identity) = require_identity(&state.registry, &headers) else {
        return StatusCode::UNAUTHORIZED.into_response();
    };
    if !state.registry.can_access(&id, identity.id) {
        return StatusCode::NOT_FOUND.into_response();
    }
    let Some(text) = input.text.filter(|text| !text.trim().is_empty()) else {
        return StatusCode::BAD_REQUEST.into_response();
    };
    let Some(due_at) = input.due_at else {
        return StatusCode::BAD_REQUEST.into_response();
    };
    let Ok(parsed) = chrono::DateTime::parse_from_rfc3339(&due_at) else {
        return StatusCode::BAD_REQUEST.into_response();
    };
    let due_at_unix = parsed.timestamp();
    if due_at_unix <= now_unix() {
        return StatusCode::BAD_REQUEST.into_response();
    }
    let (sender, receiver) = tokio::sync::oneshot::channel();
    if state
        .actions
        .send(HostAction::Schedule {
            conversation_id: id,
            text,
            due_at_unix,
            response: sender,
        })
        .is_err()
    {
        return StatusCode::SERVICE_UNAVAILABLE.into_response();
    }
    match tokio::time::timeout(std::time::Duration::from_secs(5), receiver).await {
        Ok(Ok(Ok(value))) => (StatusCode::ACCEPTED, Json(value)).into_response(),
        Ok(Ok(Err(_))) | Ok(Err(_)) => StatusCode::BAD_GATEWAY.into_response(),
        Err(_) => StatusCode::GATEWAY_TIMEOUT.into_response(),
    }
}

async fn cancel_schedule(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> impl IntoResponse {
    let Some(identity) = require_identity(&state.registry, &headers) else {
        return StatusCode::UNAUTHORIZED;
    };
    let Some(schedule) = state
        .schedules
        .read()
        .expect("schedule lock should not be poisoned")
        .iter()
        .find(|schedule| schedule.id == id)
        .cloned()
    else {
        return StatusCode::NOT_FOUND;
    };
    if !state
        .registry
        .can_access(&schedule.conversation_id, identity.id)
    {
        return StatusCode::NOT_FOUND;
    }
    let (sender, receiver) = tokio::sync::oneshot::channel();
    if state
        .actions
        .send(HostAction::CancelSchedule {
            schedule_id: id,
            response: sender,
        })
        .is_err()
    {
        return StatusCode::SERVICE_UNAVAILABLE;
    }
    match tokio::time::timeout(std::time::Duration::from_secs(5), receiver).await {
        Ok(Ok(Ok(()))) => StatusCode::NO_CONTENT,
        _ => StatusCode::BAD_GATEWAY,
    }
}

async fn read_conversation(
    state: HostHttpState,
    headers: HeaderMap,
    id: String,
    history_only: bool,
) -> axum::response::Response {
    let Some(identity) = require_identity(&state.registry, &headers) else {
        return StatusCode::UNAUTHORIZED.into_response();
    };
    if !state.registry.can_access(&id, identity.id) {
        return StatusCode::NOT_FOUND.into_response();
    }
    let (sender, receiver) = tokio::sync::oneshot::channel();
    if state
        .actions
        .send(HostAction::Read {
            conversation_id: id,
            response: sender,
        })
        .is_err()
    {
        return StatusCode::SERVICE_UNAVAILABLE.into_response();
    }
    match tokio::time::timeout(std::time::Duration::from_secs(30), receiver).await {
        Ok(Ok(Ok(snapshot))) => {
            if history_only {
                let items = snapshot
                    .get("items")
                    .cloned()
                    .unwrap_or_else(|| serde_json::json!([]));
                Json(serde_json::json!({
                    "items": items,
                    "hasMore": false,
                    "before": null
                }))
                .into_response()
            } else {
                Json(snapshot).into_response()
            }
        }
        Ok(Ok(Err(_))) | Ok(Err(_)) => StatusCode::BAD_GATEWAY.into_response(),
        Err(_) => StatusCode::GATEWAY_TIMEOUT.into_response(),
    }
}

async fn archive(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> impl IntoResponse {
    mutate(&state, headers, id, HostActionKind::Archive(true))
}

async fn restore(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> impl IntoResponse {
    mutate(&state, headers, id, HostActionKind::Archive(false))
}

async fn pin(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Json(input): Json<PinInput>,
) -> impl IntoResponse {
    mutate(&state, headers, id, HostActionKind::Pin(input.pinned))
}

async fn rename(
    State(state): State<HostHttpState>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Json(input): Json<RenameInput>,
) -> impl IntoResponse {
    let Some(title) = input.title.filter(|title| !title.trim().is_empty()) else {
        return StatusCode::BAD_REQUEST;
    };
    mutate(&state, headers, id, HostActionKind::Rename(title))
}

enum HostActionKind {
    Archive(bool),
    Pin(bool),
    Rename(String),
}

fn mutate(
    state: &HostHttpState,
    headers: HeaderMap,
    id: String,
    kind: HostActionKind,
) -> StatusCode {
    let Some(identity) = require_identity(&state.registry, &headers) else {
        return StatusCode::UNAUTHORIZED;
    };
    if !state.registry.can_access(&id, identity.id) {
        return StatusCode::NOT_FOUND;
    }
    let action = match kind {
        HostActionKind::Archive(value) => HostAction::Archive {
            conversation_id: id,
            archived: value,
        },
        HostActionKind::Pin(value) => HostAction::Pin {
            conversation_id: id,
            pinned: value,
        },
        HostActionKind::Rename(value) => HostAction::Rename {
            conversation_id: id,
            title: value,
        },
    };
    if state.actions.send(action).is_ok() {
        StatusCode::ACCEPTED
    } else {
        StatusCode::SERVICE_UNAVAILABLE
    }
}

fn authenticate_headers(registry: &DeviceRegistry, headers: &HeaderMap) -> Option<DeviceIdentity> {
    let value = headers
        .get(axum::http::header::AUTHORIZATION)?
        .to_str()
        .ok()?;
    let token = value.strip_prefix("Bearer ")?;
    registry.authenticate(token)
}

fn require_identity(registry: &DeviceRegistry, headers: &HeaderMap) -> Option<DeviceIdentity> {
    authenticate_headers(registry, headers)
}

fn auth_error(status: StatusCode, error: HostAuthError) -> axum::response::Response {
    (
        status,
        Json(serde_json::json!({ "error": error.to_string() })),
    )
        .into_response()
}

fn new_pairing_code() -> String {
    format!("{:06}", rng().random_range(100_000..1_000_000))
}

fn new_token() -> String {
    let mut bytes = [0u8; 32];
    rng().fill(&mut bytes);
    URL_SAFE_NO_PAD.encode(bytes)
}

fn digest(token: &str) -> [u8; 32] {
    Sha256::digest(token.as_bytes()).into()
}

fn constant_time_equal(left: &[u8], right: &[u8]) -> bool {
    if left.len() != right.len() {
        return false;
    }
    left.ct_eq(right).into()
}

fn now_unix() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

#[cfg(test)]
mod tests {
    use super::*;
    use http_body_util::BodyExt;
    use tower::ServiceExt;

    #[test]
    fn pairing_is_single_use_and_tokens_authenticate() {
        let registry = DeviceRegistry::new();
        let display = registry.pairing_display();
        let paired = registry
            .pair("Android phone", &display.code)
            .expect("pairing should work");
        assert_eq!(
            registry.authenticate(&paired.token),
            Some(paired.identity.clone())
        );
        assert_eq!(
            registry.pair("second", &display.code),
            Err(HostAuthError::InvalidPairingCode)
        );
    }

    #[test]
    fn conversation_access_is_explicit_and_revocation_removes_it() {
        let registry = DeviceRegistry::new();
        let display = registry.pairing_display();
        let paired = registry.pair("phone", &display.code).unwrap();
        registry
            .grant_conversation("thread-1", paired.identity.id)
            .unwrap();
        assert!(registry.can_access("thread-1", paired.identity.id));
        assert!(!registry.can_access("thread-2", paired.identity.id));
        assert!(registry.revoke_conversation("thread-1", paired.identity.id));
        assert!(!registry.can_access("thread-1", paired.identity.id));
    }

    #[test]
    fn revoked_device_loses_authentication_and_access() {
        let registry = DeviceRegistry::new();
        let display = registry.pairing_display();
        let paired = registry.pair("phone", &display.code).unwrap();
        registry
            .grant_conversation("thread-1", paired.identity.id)
            .unwrap();
        assert!(registry.revoke(paired.identity.id));
        assert_eq!(registry.authenticate(&paired.token), None);
        assert!(!registry.can_access("thread-1", paired.identity.id));
    }

    #[test]
    fn persisted_registry_restores_tokens_and_access() {
        let path = std::env::temp_dir().join(format!("pocketagent-host-{}.json", Uuid::new_v4()));
        let registry = DeviceRegistry::load(&path).unwrap();
        let display = registry.pairing_display();
        let paired = registry.pair("phone", &display.code).unwrap();
        registry
            .grant_conversation("thread-1", paired.identity.id)
            .unwrap();
        let restored = DeviceRegistry::load(&path).unwrap();
        assert_eq!(
            restored.authenticate(&paired.token),
            Some(paired.identity.clone())
        );
        assert!(restored.can_access("thread-1", paired.identity.id));
        let _ = std::fs::remove_file(path);
    }

    #[tokio::test]
    async fn http_routes_require_access_and_forward_authorized_actions() {
        let registry = DeviceRegistry::new();
        let display = registry.pairing_display();
        let (actions, mut received) = tokio::sync::mpsc::unbounded_channel();
        let state = HostHttpState::new(registry.clone(), actions, |_| SessionInfo {
            authenticated: false,
            device_name: None,
            repository: None,
            host_name: "test-host".into(),
            version: "test".into(),
        });
        state.conversations.write().unwrap().push(HostConversation {
            id: "thread-1".into(),
            title: "Test".into(),
            archived: false,
            pinned: false,
        });
        let app = router(state.clone());
        let pair = app
            .clone()
            .oneshot(
                axum::http::Request::post("/api/devices/pair")
                    .header("content-type", "application/json")
                    .body(axum::body::Body::from(
                        serde_json::json!({"code": display.code, "deviceName": "phone"})
                            .to_string(),
                    ))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(pair.status(), StatusCode::OK);
        let token = serde_json::from_slice::<serde_json::Value>(
            &pair.into_body().collect().await.unwrap().to_bytes(),
        )
        .unwrap()["token"]
            .as_str()
            .unwrap()
            .to_owned();
        let create_app = router(state.clone());
        let create_token = token.clone();
        let create_task = tokio::spawn(async move {
            create_app
                .oneshot(
                    axum::http::Request::post("/api/conversations")
                        .header("authorization", format!("Bearer {create_token}"))
                        .header("content-type", "application/json")
                        .body(axum::body::Body::from(
                            serde_json::json!({"title": "Remote"}).to_string(),
                        ))
                        .unwrap(),
                )
                .await
                .unwrap()
        });
        let create_action = received.recv().await.unwrap();
        if let HostAction::Create { response, .. } = create_action {
            response
                .send(Ok(
                    serde_json::json!({ "id": "created", "title": "Remote" }),
                ))
                .unwrap();
        } else {
            panic!("expected create action");
        }
        assert_eq!(create_task.await.unwrap().status(), StatusCode::OK);
        for path in ["/api/models", "/api/usage", "/api/provider/status"] {
            let response = app
                .clone()
                .oneshot(
                    axum::http::Request::get(path)
                        .header("authorization", format!("Bearer {token}"))
                        .body(axum::body::Body::empty())
                        .unwrap(),
                )
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::OK);
        }
        let denied = app
            .clone()
            .oneshot(
                axum::http::Request::get("/api/conversations")
                    .header("authorization", format!("Bearer {token}"))
                    .body(axum::body::Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(denied.status(), StatusCode::OK);
        let body = denied.into_body().collect().await.unwrap().to_bytes();
        assert_eq!(
            serde_json::from_slice::<Vec<HostConversation>>(&body)
                .unwrap()
                .len(),
            0
        );
        let identity = registry.authenticate(&token).unwrap();
        registry
            .grant_conversation("thread-1", identity.id)
            .unwrap();
        let read_app = router(state.clone());
        let read_token = token.clone();
        let read_task = tokio::spawn(async move {
            read_app
                .oneshot(
                    axum::http::Request::get("/api/conversations/thread-1")
                        .header("authorization", format!("Bearer {read_token}"))
                        .body(axum::body::Body::empty())
                        .unwrap(),
                )
                .await
                .unwrap()
        });
        let read_action = received.recv().await.unwrap();
        if let HostAction::Read { response, .. } = read_action {
            response
                .send(Ok(serde_json::json!({ "id": "thread-1", "items": [] })))
                .unwrap();
        } else {
            panic!("expected read action");
        }
        assert_eq!(read_task.await.unwrap().status(), StatusCode::OK);
        let accepted = app
            .oneshot(
                axum::http::Request::post("/api/conversations/thread-1/messages")
                    .header("authorization", format!("Bearer {token}"))
                    .header("content-type", "application/json")
                    .body(axum::body::Body::from(
                        serde_json::json!({"text": "hello"}).to_string(),
                    ))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(accepted.status(), StatusCode::ACCEPTED);
        assert!(
            matches!(received.recv().await.unwrap(), HostAction::Send { conversation_id, text } if conversation_id == "thread-1" && text == "hello")
        );
    }

    #[tokio::test]
    async fn image_route_is_scoped_to_the_repository_root() {
        let root = std::env::temp_dir().join(format!("pocketagent-image-{}", Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("image.png"), [1u8, 2, 3]).unwrap();
        let registry = DeviceRegistry::new();
        let display = registry.pairing_display();
        let paired = registry.pair("phone", &display.code).unwrap();
        registry
            .grant_conversation("thread-1", paired.identity.id)
            .unwrap();
        let (actions, _received) = tokio::sync::mpsc::unbounded_channel();
        let state = HostHttpState::new(registry, actions, |_| SessionInfo {
            authenticated: false,
            device_name: None,
            repository: None,
            host_name: "test".into(),
            version: "test".into(),
        });
        *state.image_root.write().unwrap() = root.to_string_lossy().into_owned();
        let app = router(state);
        let response = app
            .clone()
            .oneshot(
                axum::http::Request::get("/api/conversations/thread-1/images?path=image.png")
                    .header("authorization", format!("Bearer {}", paired.token))
                    .body(axum::body::Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(
            response
                .into_body()
                .collect()
                .await
                .unwrap()
                .to_bytes()
                .as_ref(),
            [1, 2, 3]
        );
        let traversal = app
            .oneshot(
                axum::http::Request::get("/api/conversations/thread-1/images?path=../secret")
                    .header("authorization", format!("Bearer {}", paired.token))
                    .body(axum::body::Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(traversal.status(), StatusCode::NOT_FOUND);
        let _ = std::fs::remove_dir_all(root);
    }
}
