//! Encrypted application messages carried by the opaque relay transport.
//!
//! The relay only sees routing identifiers and ciphertext. Request/response
//! methods, paths, headers and bodies are authenticated end-to-end with the
//! session key shared by the device and target host.

use crate::transport::{RelayTransportClient, TransportError};
use pocketagent_crypto::{CryptoError, SessionKey, open, seal};
use pocketagent_protocol::{
    ApplicationRequest, ApplicationResponse, RelayFrame, RelayFrameEnvelope, RelayRequest,
    RelayResponse,
};
use serde::de::DeserializeOwned;
use std::{future::Future, pin::Pin, sync::Arc};
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
    #[error("relay transport failed: {0}")]
    Transport(#[from] TransportError),
}

pub struct RelayApplicationClient {
    transport: RelayTransportClient,
    key: SessionKey,
    host_id: Uuid,
    device_id: Uuid,
}

type ApplicationHandler = Arc<
    dyn Fn(ApplicationRequest) -> Pin<Box<dyn Future<Output = ApplicationResponse> + Send>>
        + Send
        + Sync,
>;

/// Authenticated relay-side host loop. The host connects outward to the relay,
/// decrypts requests, invokes the local HTTP/application adapter, and encrypts
/// the response back to the requesting device. The relay never sees the body.
pub struct RelayApplicationHost {
    transport: RelayTransportClient,
    key: SessionKey,
    host_id: Uuid,
    handler: ApplicationHandler,
}

impl RelayApplicationHost {
    pub async fn connect<F, Fut>(
        url: &str,
        host_id: Uuid,
        token: Vec<u8>,
        key: SessionKey,
        handler: F,
    ) -> Result<Self, ApplicationError>
    where
        F: Fn(ApplicationRequest) -> Fut + Send + Sync + 'static,
        Fut: Future<Output = ApplicationResponse> + Send + 'static,
    {
        let transport = RelayTransportClient::connect_host(url, host_id, token).await?;
        Ok(Self {
            transport,
            key,
            host_id,
            handler: Arc::new(move |request| Box::pin(handler(request))),
        })
    }

    pub async fn serve(mut self) -> Result<(), ApplicationError> {
        loop {
            let frame = self.transport.recv_frame().await?;
            let RelayFrame::ClientRequest(request) = frame.frame else {
                continue;
            };
            let device_id = request.device_id;
            let request_id = request.request_id;
            let request = decode_request(&self.key, self.host_id, &request)?;
            let response = (self.handler)(request).await;
            let response =
                encode_response(&self.key, self.host_id, device_id, request_id, &response)?;
            self.transport
                .send_frame(RelayFrameEnvelope::new(RelayFrame::HostResponse(response)))
                .await?;
        }
    }

    pub fn session_id(&self) -> Uuid {
        self.transport.session_id()
    }
}

impl RelayApplicationClient {
    pub async fn connect(
        url: &str,
        host_id: Uuid,
        device_id: Uuid,
        token: Vec<u8>,
        key: SessionKey,
    ) -> Result<Self, ApplicationError> {
        let transport = RelayTransportClient::connect_client(url, device_id, token).await?;
        Ok(Self {
            transport,
            key,
            host_id,
            device_id,
        })
    }

    pub async fn request(
        &mut self,
        request: ApplicationRequest,
    ) -> Result<ApplicationResponse, ApplicationError> {
        let request_id = Uuid::new_v4();
        let wire = encode_request(
            &self.key,
            self.host_id,
            self.device_id,
            request_id,
            &request,
        )?;
        self.transport
            .send_frame(RelayFrameEnvelope::new(RelayFrame::ClientRequest(wire)))
            .await?;
        loop {
            let frame = self.transport.recv_frame().await?;
            if let RelayFrame::HostResponse(response) = frame.frame
                && response.request_id == request_id
            {
                return decode_response(&self.key, self.host_id, self.device_id, &response);
            }
        }
    }

    pub fn session_id(&self) -> Uuid {
        self.transport.session_id()
    }
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
    use crate::{RelayNode, transport::RelayAuthRegistry, transport::RelayTransportServer};
    use tokio::{net::TcpListener, sync::oneshot};

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

    #[tokio::test]
    async fn host_loop_dispatches_encrypted_application_requests() {
        let node = RelayNode::new();
        let auth = RelayAuthRegistry::default();
        let host_id = Uuid::new_v4();
        let device_id = Uuid::new_v4();
        auth.register_host(host_id, b"host-token".to_vec()).await;
        auth.register_client(device_id, b"device-token".to_vec())
            .await;
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("ws://{}", listener.local_addr().unwrap());
        let (shutdown_tx, shutdown_rx) = oneshot::channel();
        let server = RelayTransportServer::new(node, auth);
        let server_task =
            tokio::spawn(async move { server.serve_until(listener, shutdown_rx).await });

        let key = SessionKey::random();
        let host_key = key.clone();
        let host_url = url.clone();
        let host_task = tokio::spawn(async move {
            let host =
                RelayApplicationHost::connect(
                    &host_url,
                    host_id,
                    b"host-token".to_vec(),
                    host_key,
                    |request| async move {
                        ApplicationResponse::new(200, "application/json", request.body)
                    },
                )
                .await
                .unwrap();
            host.serve().await
        });

        let mut client = RelayApplicationClient::connect(
            &url,
            host_id,
            device_id,
            b"device-token".to_vec(),
            key,
        )
        .await
        .unwrap();
        let response = client
            .request(ApplicationRequest::new(
                "POST",
                "/api/echo",
                b"hello".to_vec(),
            ))
            .await
            .unwrap();
        assert_eq!(response.status, 200);
        assert_eq!(response.body, b"hello");

        let _ = shutdown_tx.send(());
        host_task.abort();
        server_task.abort();
    }
}
