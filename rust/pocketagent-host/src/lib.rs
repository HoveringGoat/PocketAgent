//! Authentication and conversation-access primitives for the desktop host.
//!
//! The host keeps provider credentials local. Devices receive short-lived pairing
//! credentials and are granted access to individual conversations; possession of a
//! host or relay connection alone does not grant conversation access.

use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use rand::{Rng, rng};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet, VecDeque},
    sync::{Arc, Mutex},
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use subtle::ConstantTimeEq;
use thiserror::Error;
use uuid::Uuid;

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
}

#[derive(Debug)]
struct State {
    code: String,
    expires_at_unix: i64,
    consumed: bool,
    attempts: VecDeque<i64>,
    devices: HashMap<Uuid, DeviceRecord>,
    conversations: HashMap<String, HashSet<Uuid>>,
}

#[derive(Debug, Clone)]
struct DeviceRecord {
    identity: DeviceIdentity,
    token_digest: [u8; 32],
    revoked: bool,
}

#[derive(Clone, Debug)]
pub struct DeviceRegistry {
    state: Arc<Mutex<State>>,
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
            state: Arc::new(Mutex::new(State {
                code: new_pairing_code(),
                expires_at_unix: now + PAIRING_LIFETIME.as_secs() as i64,
                consumed: false,
                attempts: VecDeque::new(),
                devices: HashMap::new(),
                conversations: HashMap::new(),
            })),
        }
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
        Ok(PairedDevice {
            identity: DeviceIdentity {
                id,
                name: name.to_owned(),
                expires_at_unix,
            },
            token,
        })
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
        Ok(())
    }

    pub fn revoke_conversation(&self, conversation_id: &str, device_id: Uuid) -> bool {
        self.state
            .lock()
            .expect("registry lock should not be poisoned")
            .conversations
            .get_mut(conversation_id)
            .is_some_and(|devices| devices.remove(&device_id))
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
}
