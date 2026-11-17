//! In-process relay routing core and authenticated WebSocket transport.
//!
//! The transport is an MVP foundation: it supports loopback `ws://` and native
//! Rustls `wss://` listeners, while enrollment and persistent identity remain
//! application-level work.

use pocketagent_protocol::{RelayFrame, RelayFrameEnvelope, RelayRequest, RelayResponse};
use std::{collections::HashMap, sync::Arc};
use thiserror::Error;
use tokio::sync::{RwLock, mpsc};
use uuid::Uuid;

pub mod config;
pub mod transport;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum RelayError {
    #[error("the host is not connected")]
    HostUnavailable,
    #[error("the client is not connected")]
    ClientUnavailable,
    #[error("the session does not own this route")]
    SessionMismatch,
    #[error("the request ID is already pending")]
    DuplicateRequest,
    #[error("the request ID is not pending")]
    RequestNotFound,
    #[error("the receiving connection is closed")]
    DeliveryClosed,
}

#[derive(Clone)]
pub struct RelayNode {
    inner: Arc<RwLock<RelayState>>,
}

struct RelayState {
    hosts: HashMap<Uuid, HostConnection>,
    clients: HashMap<Uuid, ClientConnection>,
    pending: HashMap<Uuid, PendingRequest>,
}

struct HostConnection {
    session_id: Uuid,
    outbound: mpsc::Sender<RelayFrameEnvelope>,
}

struct ClientConnection {
    session_id: Uuid,
    outbound: mpsc::Sender<RelayFrameEnvelope>,
}

struct PendingRequest {
    host_id: Uuid,
    client_id: Uuid,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HostSession {
    pub host_id: Uuid,
    pub session_id: Uuid,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ClientSession {
    pub client_id: Uuid,
    pub session_id: Uuid,
}

impl RelayNode {
    pub fn new() -> Self {
        Self {
            inner: Arc::new(RwLock::new(RelayState {
                hosts: HashMap::new(),
                clients: HashMap::new(),
                pending: HashMap::new(),
            })),
        }
    }

    pub async fn attach_host(
        &self,
        host_id: Uuid,
        outbound: mpsc::Sender<RelayFrameEnvelope>,
    ) -> HostSession {
        let session = HostSession {
            host_id,
            session_id: Uuid::new_v4(),
        };
        self.inner.write().await.hosts.insert(
            host_id,
            HostConnection {
                session_id: session.session_id,
                outbound,
            },
        );
        session
    }

    pub async fn attach_client(
        &self,
        client_id: Uuid,
        outbound: mpsc::Sender<RelayFrameEnvelope>,
    ) -> ClientSession {
        let session = ClientSession {
            client_id,
            session_id: Uuid::new_v4(),
        };
        self.inner.write().await.clients.insert(
            client_id,
            ClientConnection {
                session_id: session.session_id,
                outbound,
            },
        );
        session
    }

    pub async fn route_request(
        &self,
        client: ClientSession,
        request: RelayRequest,
    ) -> Result<(), RelayError> {
        if request.device_id != client.client_id {
            return Err(RelayError::SessionMismatch);
        }

        let request_id = request.request_id;
        let (outbound, frame) = {
            let mut state = self.inner.write().await;
            if !state
                .clients
                .get(&client.client_id)
                .is_some_and(|connection| connection.session_id == client.session_id)
            {
                return Err(RelayError::ClientUnavailable);
            }
            let host_outbound = state
                .hosts
                .get(&request.target_host)
                .ok_or(RelayError::HostUnavailable)?
                .outbound
                .clone();
            if state.pending.contains_key(&request.request_id) {
                return Err(RelayError::DuplicateRequest);
            }
            state.pending.insert(
                request.request_id,
                PendingRequest {
                    host_id: request.target_host,
                    client_id: client.client_id,
                },
            );
            (
                host_outbound,
                RelayFrameEnvelope::new(RelayFrame::ClientRequest(request)),
            )
        };

        if outbound.send(frame).await.is_err() {
            let mut state = self.inner.write().await;
            state.pending.remove(&request_id);
            let _ = state.clients.remove(&client.client_id);
            return Err(RelayError::DeliveryClosed);
        }
        Ok(())
    }

    pub async fn route_response(
        &self,
        host: HostSession,
        response: RelayResponse,
    ) -> Result<(), RelayError> {
        let (outbound, frame) = {
            let mut state = self.inner.write().await;
            if !state
                .hosts
                .get(&host.host_id)
                .is_some_and(|connection| connection.session_id == host.session_id)
            {
                return Err(RelayError::HostUnavailable);
            }
            let pending = state
                .pending
                .get(&response.request_id)
                .ok_or(RelayError::RequestNotFound)?;
            if pending.host_id != host.host_id || response.target_host != host.host_id {
                return Err(RelayError::SessionMismatch);
            }
            let client_id = pending.client_id;
            let outbound = state
                .clients
                .get(&client_id)
                .ok_or(RelayError::ClientUnavailable)?
                .outbound
                .clone();
            state.pending.remove(&response.request_id);
            (
                outbound,
                RelayFrameEnvelope::new(RelayFrame::HostResponse(response)),
            )
        };

        if outbound.send(frame).await.is_err() {
            return Err(RelayError::DeliveryClosed);
        }
        Ok(())
    }

    pub async fn connected_hosts(&self) -> Vec<Uuid> {
        self.inner.read().await.hosts.keys().copied().collect()
    }

    pub async fn connected_clients(&self) -> Vec<Uuid> {
        self.inner.read().await.clients.keys().copied().collect()
    }

    pub async fn detach_host(&self, session: HostSession) {
        let mut state = self.inner.write().await;
        if state
            .hosts
            .get(&session.host_id)
            .is_some_and(|connection| connection.session_id == session.session_id)
        {
            state.hosts.remove(&session.host_id);
            state
                .pending
                .retain(|_, pending| pending.host_id != session.host_id);
        }
    }

    pub async fn detach_client(&self, session: ClientSession) {
        let mut state = self.inner.write().await;
        if state
            .clients
            .get(&session.client_id)
            .is_some_and(|connection| connection.session_id == session.session_id)
        {
            state.clients.remove(&session.client_id);
            state
                .pending
                .retain(|_, pending| pending.client_id != session.client_id);
        }
    }
}

impl Default for RelayNode {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use pocketagent_protocol::{OpaqueEnvelope, RelayFrame};

    #[tokio::test]
    async fn routes_opaque_request_from_client_to_outbound_host_and_back() {
        let relay = RelayNode::new();
        let host_id = Uuid::new_v4();
        let client_id = Uuid::new_v4();
        let (host_tx, mut host_rx) = mpsc::channel(1);
        let (client_tx, mut client_rx) = mpsc::channel(1);
        let host = relay.attach_host(host_id, host_tx).await;
        let client = relay.attach_client(client_id, client_tx).await;
        let request = RelayRequest {
            request_id: Uuid::new_v4(),
            target_host: host_id,
            device_id: client_id,
            envelope: OpaqueEnvelope {
                key_id: "device-session".into(),
                nonce: vec![1, 2, 3],
                ciphertext: vec![99, 4, 5, 6],
            },
        };

        relay
            .route_request(client, request.clone())
            .await
            .expect("request should route");
        let routed = host_rx.recv().await.expect("host should receive request");
        assert_eq!(routed.frame, RelayFrame::ClientRequest(request.clone()));

        let response = RelayResponse {
            request_id: request.request_id,
            target_host: host_id,
            envelope: OpaqueEnvelope {
                key_id: "host-session".into(),
                nonce: vec![7, 8],
                ciphertext: vec![42, 43],
            },
        };
        relay
            .route_response(host, response.clone())
            .await
            .expect("response should route");
        let routed = client_rx
            .recv()
            .await
            .expect("client should receive response");
        assert_eq!(routed.frame, RelayFrame::HostResponse(response));
    }

    #[tokio::test]
    async fn rejects_client_request_for_a_different_device() {
        let relay = RelayNode::new();
        let host_id = Uuid::new_v4();
        let client_id = Uuid::new_v4();
        let (host_tx, _host_rx) = mpsc::channel(1);
        let (client_tx, _client_rx) = mpsc::channel(1);
        relay.attach_host(host_id, host_tx).await;
        let client = relay.attach_client(client_id, client_tx).await;
        let request = RelayRequest {
            request_id: Uuid::new_v4(),
            target_host: host_id,
            device_id: Uuid::new_v4(),
            envelope: OpaqueEnvelope {
                key_id: "wrong-device".into(),
                nonce: vec![],
                ciphertext: vec![1],
            },
        };

        assert_eq!(
            relay.route_request(client, request).await,
            Err(RelayError::SessionMismatch)
        );
    }

    #[tokio::test]
    async fn keeps_pending_request_when_host_response_route_is_invalid() {
        let relay = RelayNode::new();
        let host_id = Uuid::new_v4();
        let client_id = Uuid::new_v4();
        let (host_tx, mut host_rx) = mpsc::channel(1);
        let (client_tx, mut client_rx) = mpsc::channel(1);
        let host = relay.attach_host(host_id, host_tx).await;
        let client = relay.attach_client(client_id, client_tx).await;
        let request = RelayRequest {
            request_id: Uuid::new_v4(),
            target_host: host_id,
            device_id: client_id,
            envelope: OpaqueEnvelope {
                key_id: "device-session".into(),
                nonce: vec![1],
                ciphertext: vec![2],
            },
        };
        relay
            .route_request(client, request.clone())
            .await
            .expect("request should route");
        host_rx.recv().await.expect("host should receive request");

        let invalid = RelayResponse {
            request_id: request.request_id,
            target_host: Uuid::new_v4(),
            envelope: OpaqueEnvelope {
                key_id: "host-session".into(),
                nonce: vec![3],
                ciphertext: vec![4],
            },
        };
        assert_eq!(
            relay.route_response(host, invalid).await,
            Err(RelayError::SessionMismatch)
        );

        let valid = RelayResponse {
            request_id: request.request_id,
            target_host: host_id,
            envelope: OpaqueEnvelope {
                key_id: "host-session".into(),
                nonce: vec![5],
                ciphertext: vec![6],
            },
        };
        relay
            .route_response(host, valid)
            .await
            .expect("valid response should still route");
        assert!(client_rx.recv().await.is_some());
    }
}
