//! Versioned wire types shared by PocketAgent desktop, relay, and mobile clients.
//!
//! Relay payloads are intentionally opaque here. The relay routes these envelopes but
//! must not need to deserialize conversation data or provider credentials.

use serde::{Deserialize, Serialize};
use uuid::Uuid;

pub const RELAY_PROTOCOL_VERSION: u16 = 1;
pub const APPLICATION_PROTOCOL_VERSION: u16 = 1;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ApplicationRequest {
    pub version: u16,
    pub method: String,
    pub path: String,
    pub body: Vec<u8>,
    #[serde(default)]
    pub headers: Vec<(String, String)>,
}

impl ApplicationRequest {
    pub fn new(method: impl Into<String>, path: impl Into<String>, body: Vec<u8>) -> Self {
        Self {
            version: APPLICATION_PROTOCOL_VERSION,
            method: method.into(),
            path: path.into(),
            body,
            headers: Vec::new(),
        }
    }

    pub fn with_header(mut self, name: impl Into<String>, value: impl Into<String>) -> Self {
        self.headers.push((name.into(), value.into()));
        self
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ApplicationResponse {
    pub version: u16,
    pub status: u16,
    pub content_type: String,
    pub body: Vec<u8>,
}

impl ApplicationResponse {
    pub fn new(status: u16, content_type: impl Into<String>, body: Vec<u8>) -> Self {
        Self {
            version: APPLICATION_PROTOCOL_VERSION,
            status,
            content_type: content_type.into(),
            body,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct OpaqueEnvelope {
    /// Identifies the application-level key/session used by the sender.
    pub key_id: String,
    /// Nonce/IV bytes for the application-level authenticated encryption layer.
    pub nonce: Vec<u8>,
    /// Ciphertext plus authentication tag. The relay never interprets this field.
    pub ciphertext: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HostRegistration {
    pub host_id: Uuid,
    pub host_public_key: Vec<u8>,
    pub connection_nonce: Uuid,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RelayRequest {
    pub request_id: Uuid,
    pub target_host: Uuid,
    pub device_id: Uuid,
    pub envelope: OpaqueEnvelope,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RelayResponse {
    pub request_id: Uuid,
    pub target_host: Uuid,
    pub envelope: OpaqueEnvelope,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum RelayFrame {
    HostHello(HostRegistration),
    HostAccepted { session_id: Uuid },
    ClientRequest(RelayRequest),
    HostResponse(RelayResponse),
    Close { code: u16, reason: String },
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RelayFrameEnvelope {
    pub version: u16,
    pub frame: RelayFrame,
}

impl RelayFrameEnvelope {
    pub fn new(frame: RelayFrame) -> Self {
        Self {
            version: RELAY_PROTOCOL_VERSION,
            frame,
        }
    }

    pub fn is_supported(&self) -> bool {
        self.version == RELAY_PROTOCOL_VERSION
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum RelayWireMessage {
    AuthenticateHost { host_id: Uuid, token: Vec<u8> },
    AuthenticateClient { client_id: Uuid, token: Vec<u8> },
    Authenticated { session_id: Uuid },
    Frame(RelayFrameEnvelope),
    Rejected { reason: String },
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_round_trip_preserves_opaque_payload() {
        let frame = RelayFrameEnvelope::new(RelayFrame::ClientRequest(RelayRequest {
            request_id: Uuid::new_v4(),
            target_host: Uuid::new_v4(),
            device_id: Uuid::new_v4(),
            envelope: OpaqueEnvelope {
                key_id: "session-key".into(),
                nonce: vec![1, 2, 3],
                ciphertext: vec![9, 8, 7, 6],
            },
        }));

        let encoded = serde_json::to_vec(&frame).expect("frame should serialize");
        let decoded: RelayFrameEnvelope =
            serde_json::from_slice(&encoded).expect("frame should deserialize");
        assert_eq!(decoded, frame);
        assert_eq!(decoded.version, RELAY_PROTOCOL_VERSION);
        assert!(decoded.is_supported());
    }
}
