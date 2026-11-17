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
}
