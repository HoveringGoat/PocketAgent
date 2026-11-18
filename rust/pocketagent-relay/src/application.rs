//! Encrypted application messages carried by the opaque relay transport.
//!
//! The relay only sees routing identifiers and ciphertext. Request/response
//! methods, paths, headers and bodies are authenticated end-to-end with the
//! session key shared by the device and target host.

use pocketagent_crypto::{CryptoError, SessionKey, open, seal};
use pocketagent_protocol::{ApplicationRequest, ApplicationResponse, RelayRequest, RelayResponse};
use serde::de::DeserializeOwned;
use thiserror::Error;
use uuid::Uuid;

#[derive(Debug, Error)]
pub enum ApplicationError {
    #[error("application payload serialization failed: {0}")]
    Json(#[from] serde_json::Error),
    #[error("application payload authentication failed: {0}")]
    Crypto(#[from] CryptoError),
    #[error("application protocol version is unsupported")]
    UnsupportedVersion,
    #[error("application route does not match the envelope route")]
    RouteMismatch,
}

fn associated_data(host_id: Uuid, device_id: Uuid, request_id: Uuid) -> Vec<u8> {
    format!("pocketagent/application/{host_id}/{device_id}/{request_id}").into_bytes()
}

pub fn encode_request(
    key: &SessionKey,
    host_id: Uuid,
    device_id: Uuid,
    request_id: Uuid,
    request: &ApplicationRequest,
) -> Result<RelayRequest, ApplicationError> {
    if request.version != pocketagent_protocol::APPLICATION_PROTOCOL_VERSION {
        return Err(ApplicationError::UnsupportedVersion);
    }
    let bytes = serde_json::to_vec(request)?;
    Ok(RelayRequest {
        request_id,
        target_host: host_id,
        device_id,
        envelope: seal(
            key,
            &associated_data(host_id, device_id, request_id),
            &bytes,
        ),
    })
}

pub fn decode_request(
    key: &SessionKey,
    host_id: Uuid,
    request: &RelayRequest,
) -> Result<ApplicationRequest, ApplicationError> {
    if request.target_host != host_id {
        return Err(ApplicationError::RouteMismatch);
    }
    let bytes = open(
        key,
        &associated_data(host_id, request.device_id, request.request_id),
        &request.envelope,
    )?;
    let request = serde_json::from_slice::<ApplicationRequest>(&bytes)?;
    if request.version != pocketagent_protocol::APPLICATION_PROTOCOL_VERSION {
        return Err(ApplicationError::UnsupportedVersion);
    }
    Ok(request)
}

pub fn encode_response(
    key: &SessionKey,
    host_id: Uuid,
    device_id: Uuid,
    request_id: Uuid,
    response: &ApplicationResponse,
) -> Result<RelayResponse, ApplicationError> {
    if response.version != pocketagent_protocol::APPLICATION_PROTOCOL_VERSION {
        return Err(ApplicationError::UnsupportedVersion);
    }
    let bytes = serde_json::to_vec(response)?;
    Ok(RelayResponse {
        request_id,
        target_host: host_id,
        envelope: seal(
            key,
            &associated_data(host_id, device_id, request_id),
            &bytes,
        ),
    })
}

pub fn decode_response(
    key: &SessionKey,
    host_id: Uuid,
    device_id: Uuid,
    response: &RelayResponse,
) -> Result<ApplicationResponse, ApplicationError> {
    if response.target_host != host_id {
        return Err(ApplicationError::RouteMismatch);
    }
    let bytes = open(
        key,
        &associated_data(host_id, device_id, response.request_id),
        &response.envelope,
    )?;
    let response = serde_json::from_slice::<ApplicationResponse>(&bytes)?;
    if response.version != pocketagent_protocol::APPLICATION_PROTOCOL_VERSION {
        return Err(ApplicationError::UnsupportedVersion);
    }
    Ok(response)
}

pub fn decode_json<T: DeserializeOwned>(
    key: &SessionKey,
    host_id: Uuid,
    device_id: Uuid,
    response: &RelayResponse,
) -> Result<T, ApplicationError> {
    let response = decode_response(key, host_id, device_id, response)?;
    Ok(serde_json::from_slice(&response.body)?)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn application_payload_round_trips_end_to_end() {
        let key = SessionKey::random();
        let host_id = Uuid::new_v4();
        let device_id = Uuid::new_v4();
        let request_id = Uuid::new_v4();
        let request = ApplicationRequest::new("GET", "/api/session", Vec::new());
        let wire = encode_request(&key, host_id, device_id, request_id, &request).unwrap();
        assert_eq!(decode_request(&key, host_id, &wire).unwrap(), request);
        let response =
            ApplicationResponse::new(200, "application/json", br#"{"ok":true}"#.to_vec());
        let wire = encode_response(&key, host_id, device_id, request_id, &response).unwrap();
        assert_eq!(
            decode_response(&key, host_id, device_id, &wire).unwrap(),
            response
        );
    }

    #[test]
    fn application_route_binding_rejects_wrong_device_or_request() {
        let key = SessionKey::random();
        let host_id = Uuid::new_v4();
        let device_id = Uuid::new_v4();
        let request_id = Uuid::new_v4();
        let request = ApplicationRequest::new("POST", "/api/conversations", b"{}".to_vec());
        let wire = encode_request(&key, host_id, device_id, request_id, &request).unwrap();
        assert!(matches!(
            decode_request(&key, Uuid::new_v4(), &wire),
            Err(ApplicationError::RouteMismatch)
        ));
        let response = ApplicationResponse::new(202, "application/json", Vec::new());
        let wire = encode_response(&key, host_id, device_id, request_id, &response).unwrap();
        assert!(matches!(
            decode_response(&key, host_id, Uuid::new_v4(), &wire),
            Err(ApplicationError::Crypto(_))
        ));
    }
}
