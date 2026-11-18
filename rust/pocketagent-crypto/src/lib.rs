//! Application-level encryption for relay payloads.
//!
//! Relay transport may use TLS for hop protection, but the message envelope is
//! sealed separately so a relay node cannot read conversation data or credentials.
//! The AES-256-GCM nonce and tag layout is compatible with Android's
//! `AES/GCM/NoPadding` implementation.

use aes_gcm::{Aes256Gcm, KeyInit, Nonce, aead::Aead};
use pocketagent_protocol::OpaqueEnvelope;
use thiserror::Error;
use uuid::Uuid;
use zeroize::Zeroize;

const NONCE_LENGTH: usize = 12;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum CryptoError {
    #[error("the envelope belongs to a different session key")]
    WrongKey,
    #[error("the encrypted envelope failed authentication")]
    AuthenticationFailed,
}

#[derive(Clone)]
pub struct SessionKey {
    id: Uuid,
    bytes: [u8; 32],
}

impl Drop for SessionKey {
    fn drop(&mut self) {
        self.bytes.zeroize();
    }
}

impl SessionKey {
    pub fn random() -> Self {
        Self {
            id: Uuid::new_v4(),
            bytes: rand::random(),
        }
    }

    pub fn from_bytes(id: Uuid, bytes: [u8; 32]) -> Self {
        Self { id, bytes }
    }

    pub fn id(&self) -> Uuid {
        self.id
    }

    /// Returns the out-of-band enrollment form used by relay configuration.
    /// The value is intentionally opaque to the relay; it is shared only with
    /// the host and device that need to open application envelopes.
    pub fn to_wire(&self) -> String {
        let mut bytes = String::with_capacity(64);
        for byte in self.bytes {
            use std::fmt::Write;
            write!(&mut bytes, "{byte:02x}").expect("writing to a string cannot fail");
        }
        format!("{}:{bytes}", self.id)
    }

    pub fn from_wire(value: &str) -> Option<Self> {
        let (id, encoded) = value.trim().split_once(':')?;
        if encoded.len() != 64 {
            return None;
        }
        let mut bytes = [0_u8; 32];
        let encoded = encoded.as_bytes();
        for (index, slot) in bytes.iter_mut().enumerate() {
            let offset = index * 2;
            *slot = (hex_digit(encoded[offset])? << 4) | hex_digit(encoded[offset + 1])?;
        }
        Some(Self::from_bytes(id.parse().ok()?, bytes))
    }
}

fn hex_digit(value: u8) -> Option<u8> {
    match value {
        b'0'..=b'9' => Some(value - b'0'),
        b'a'..=b'f' => Some(value - b'a' + 10),
        b'A'..=b'F' => Some(value - b'A' + 10),
        _ => None,
    }
}

pub fn seal(key: &SessionKey, associated_data: &[u8], plaintext: &[u8]) -> OpaqueEnvelope {
    let cipher = Aes256Gcm::new_from_slice(&key.bytes).expect("AES-256 keys are always 32 bytes");
    let nonce_bytes: [u8; NONCE_LENGTH] = rand::random();
    let nonce = Nonce::from_slice(&nonce_bytes);
    let mut ciphertext = cipher
        .encrypt(
            nonce,
            aes_gcm::aead::Payload {
                msg: plaintext,
                aad: associated_data,
            },
        )
        .expect("AES-GCM encryption cannot fail with a valid key and nonce");
    OpaqueEnvelope {
        key_id: key.id.to_string(),
        nonce: nonce_bytes.to_vec(),
        ciphertext: std::mem::take(&mut ciphertext),
    }
}

pub fn open(
    key: &SessionKey,
    associated_data: &[u8],
    envelope: &OpaqueEnvelope,
) -> Result<Vec<u8>, CryptoError> {
    if envelope.key_id != key.id.to_string() || envelope.nonce.len() != NONCE_LENGTH {
        return Err(CryptoError::WrongKey);
    }
    let cipher = Aes256Gcm::new_from_slice(&key.bytes).expect("AES-256 keys are always 32 bytes");
    let nonce = Nonce::from_slice(&envelope.nonce);
    cipher
        .decrypt(
            nonce,
            aes_gcm::aead::Payload {
                msg: envelope.ciphertext.as_ref(),
                aad: associated_data,
            },
        )
        .map_err(|_| CryptoError::AuthenticationFailed)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn seals_and_opens_with_associated_data() {
        let key = SessionKey::random();
        let envelope = seal(
            &key,
            b"host/device/request-1",
            b"private conversation payload",
        );

        assert_ne!(envelope.ciphertext, b"private conversation payload");
        assert_eq!(
            open(&key, b"host/device/request-1", &envelope).expect("matching key should open"),
            b"private conversation payload"
        );
    }

    #[test]
    fn rejects_wrong_key_and_associated_data() {
        let key = SessionKey::random();
        let other = SessionKey::random();
        let envelope = seal(&key, b"route-a", b"payload");

        assert_eq!(
            open(&other, b"route-a", &envelope),
            Err(CryptoError::WrongKey)
        );
        assert_eq!(
            open(&key, b"route-b", &envelope),
            Err(CryptoError::AuthenticationFailed)
        );
    }

    #[test]
    fn rejects_tampered_ciphertext() {
        let key = SessionKey::random();
        let mut envelope = seal(&key, b"route", b"payload");
        envelope.ciphertext[0] ^= 0x80;

        assert_eq!(
            open(&key, b"route", &envelope),
            Err(CryptoError::AuthenticationFailed)
        );
    }

    #[test]
    fn session_key_wire_form_round_trips_and_rejects_bad_values() {
        let key = SessionKey::random();
        let wire = key.to_wire();
        let restored = SessionKey::from_wire(&wire).expect("wire key should parse");
        assert_eq!(restored.id(), key.id());
        assert!(SessionKey::from_wire("not-a-key").is_none());
        assert!(SessionKey::from_wire(&format!("{}:00", key.id())).is_none());
        assert!(SessionKey::from_wire(&format!("{}:{}z", key.id(), "00".repeat(32))).is_none());
    }
}
