//! WebSocket transport for relay sessions.
//!
//! The remote host and client both connect outward to this server. The relay
//! authenticates the first wire message, then routes only versioned frames. TLS
//! is expected for non-loopback deployments (`wss://`); the integration tests
//! use loopback `ws://` so they remain self-contained.

use crate::{ClientSession, HostSession, RelayError, RelayNode};
use futures_util::{SinkExt, StreamExt};
use pocketagent_protocol::{RelayFrame, RelayFrameEnvelope, RelayWireMessage};
use std::{collections::HashMap, io, sync::Arc};
use subtle::ConstantTimeEq;
use thiserror::Error;
use tokio::{
    io::{AsyncRead, AsyncWrite},
    net::{TcpListener, TcpStream},
    sync::{RwLock, mpsc, oneshot},
};
use tokio_rustls::TlsAcceptor;
use tokio_tungstenite::{
    Connector, MaybeTlsStream, WebSocketStream, accept_async, connect_async,
    connect_async_tls_with_config, tungstenite::Message,
};
use url::Url;
use uuid::Uuid;

#[derive(Debug, Error)]
pub enum TransportError {
    #[error("I/O failure: {0}")]
    Io(#[from] io::Error),
    #[error("WebSocket failure: {0}")]
    WebSocket(#[source] Box<tokio_tungstenite::tungstenite::Error>),
    #[error("wire message serialization failure: {0}")]
    Json(#[from] serde_json::Error),
    #[error("invalid relay URL: {0}")]
    Url(#[from] url::ParseError),
    #[error("relay authentication rejected: {0}")]
    Rejected(String),
    #[error("the relay client sent an unsupported protocol version")]
    UnsupportedVersion,
    #[error("the relay client sent an unexpected wire message")]
    UnexpectedMessage,
    #[error("the relay client closed before authentication")]
    MissingAuthentication,
    #[error("relay routing failed: {0}")]
    Routing(#[from] RelayError),
}

impl From<tokio_tungstenite::tungstenite::Error> for TransportError {
    fn from(error: tokio_tungstenite::tungstenite::Error) -> Self {
        Self::WebSocket(Box::new(error))
    }
}

#[derive(Clone, Default)]
pub struct RelayAuthRegistry {
    hosts: Arc<RwLock<HashMap<Uuid, Vec<u8>>>>,
    clients: Arc<RwLock<HashMap<Uuid, Vec<u8>>>>,
}

impl RelayAuthRegistry {
    pub async fn register_host(&self, host_id: Uuid, token: Vec<u8>) {
        self.hosts.write().await.insert(host_id, token);
    }

    pub async fn register_client(&self, client_id: Uuid, token: Vec<u8>) {
        self.clients.write().await.insert(client_id, token);
    }

    async fn verify_host(&self, host_id: Uuid, token: &[u8]) -> bool {
        verify_token(self.hosts.read().await.get(&host_id), token)
    }

    async fn verify_client(&self, client_id: Uuid, token: &[u8]) -> bool {
        verify_token(self.clients.read().await.get(&client_id), token)
    }
}

fn verify_token(expected: Option<&Vec<u8>>, actual: &[u8]) -> bool {
    expected.is_some_and(|expected| {
        expected.len() == actual.len() && expected.as_slice().ct_eq(actual).into()
    })
}

#[derive(Clone)]
pub struct RelayTransportServer {
    node: RelayNode,
    auth: RelayAuthRegistry,
}

impl RelayTransportServer {
    pub fn new(node: RelayNode, auth: RelayAuthRegistry) -> Self {
        Self { node, auth }
    }

    pub async fn serve_until(
        &self,
        listener: TcpListener,
        mut shutdown: oneshot::Receiver<()>,
    ) -> Result<(), TransportError> {
        loop {
            tokio::select! {
                accepted = listener.accept() => {
                    let (stream, _) = accepted?;
                    let server = self.clone();
                    tokio::spawn(async move {
                        let _ = server.handle(stream).await;
                    });
                }
                _ = &mut shutdown => return Ok(()),
            }
        }
    }

    pub async fn serve_tls_until(
        &self,
        listener: TcpListener,
        acceptor: TlsAcceptor,
        mut shutdown: oneshot::Receiver<()>,
    ) -> Result<(), TransportError> {
        loop {
            tokio::select! {
                accepted = listener.accept() => {
                    let (stream, _) = accepted?;
                    let acceptor = acceptor.clone();
                    let server = self.clone();
                    tokio::spawn(async move {
                        if let Ok(stream) = acceptor.accept(stream).await {
                            let _ = server.handle(stream).await;
                        }
                    });
                }
                _ = &mut shutdown => return Ok(()),
            }
        }
    }

    async fn handle<S>(&self, stream: S) -> Result<(), TransportError>
    where
        S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
    {
        let mut socket = accept_async(stream).await?;
        let first = socket
            .next()
            .await
            .ok_or(TransportError::MissingAuthentication)??;
        let authentication = decode_wire(first)?;
        let (role, session_id, mut outbound) = match authentication {
            RelayWireMessage::AuthenticateHost { host_id, token } => {
                if !self.auth.verify_host(host_id, &token).await {
                    send_wire(
                        &mut socket,
                        RelayWireMessage::Rejected {
                            reason: "host authentication failed".into(),
                        },
                    )
                    .await?;
                    return Err(TransportError::Rejected(
                        "host authentication failed".into(),
                    ));
                }
                let (tx, rx) = mpsc::channel(32);
                let session = self.node.attach_host(host_id, tx).await;
                (SessionRole::Host(session), session.session_id, rx)
            }
            RelayWireMessage::AuthenticateClient { client_id, token } => {
                if !self.auth.verify_client(client_id, &token).await {
                    send_wire(
                        &mut socket,
                        RelayWireMessage::Rejected {
                            reason: "client authentication failed".into(),
                        },
                    )
                    .await?;
                    return Err(TransportError::Rejected(
                        "client authentication failed".into(),
                    ));
                }
                let (tx, rx) = mpsc::channel(32);
                let session = self.node.attach_client(client_id, tx).await;
                (SessionRole::Client(session), session.session_id, rx)
            }
            _ => return Err(TransportError::MissingAuthentication),
        };

        send_wire(&mut socket, RelayWireMessage::Authenticated { session_id }).await?;
        let result = loop {
            tokio::select! {
                outbound_message = outbound.recv() => {
                    match outbound_message {
                        Some(frame) => send_wire(&mut socket, RelayWireMessage::Frame(frame)).await?,
                        None => break Ok(()),
                    }
                }
                inbound = socket.next() => {
                    match inbound {
                        Some(Ok(Message::Text(text))) => self.handle_wire(&mut socket, &role, decode_text(&text)?).await?,
                        Some(Ok(Message::Binary(bytes))) => self.handle_wire(&mut socket, &role, serde_json::from_slice(&bytes)?).await?,
                        Some(Ok(Message::Ping(payload))) => socket.send(Message::Pong(payload)).await?,
                        Some(Ok(Message::Close(_))) | None => break Ok(()),
                        Some(Ok(Message::Pong(_))) => {},
                        Some(Err(error)) => break Err(error.into()),
                        Some(Ok(_)) => return Err(TransportError::UnexpectedMessage),
                    }
                }
            }
        };

        match role {
            SessionRole::Host(session) => self.node.detach_host(session).await,
            SessionRole::Client(session) => self.node.detach_client(session).await,
        }
        result
    }

    async fn handle_wire<S>(
        &self,
        socket: &mut WebSocketStream<S>,
        role: &SessionRole,
        message: RelayWireMessage,
    ) -> Result<(), TransportError>
    where
        S: AsyncRead + AsyncWrite + Unpin,
    {
        let RelayWireMessage::Frame(frame) = message else {
            return Err(TransportError::UnexpectedMessage);
        };
        if !frame.is_supported() {
            return Err(TransportError::UnsupportedVersion);
        }
        match (role, frame.frame) {
            (SessionRole::Host(session), RelayFrame::HostResponse(response)) => {
                self.node.route_response(*session, response).await?;
            }
            (SessionRole::Client(session), RelayFrame::ClientRequest(request)) => {
                self.node.route_request(*session, request).await?;
            }
            _ => return Err(TransportError::UnexpectedMessage),
        }
        let _ = socket;
        Ok(())
    }
}

#[derive(Clone, Copy)]
enum SessionRole {
    Host(HostSession),
    Client(ClientSession),
}

async fn send_wire<S>(socket: &mut S, message: RelayWireMessage) -> Result<(), TransportError>
where
    S: SinkExt<Message> + Unpin,
    S::Error: Into<tokio_tungstenite::tungstenite::Error>,
{
    let text = serde_json::to_string(&message)?;
    socket
        .send(Message::Text(text.into()))
        .await
        .map_err(Into::into)?;
    Ok(())
}

fn decode_wire(message: Message) -> Result<RelayWireMessage, TransportError> {
    match message {
        Message::Text(text) => decode_text(&text),
        Message::Binary(bytes) => Ok(serde_json::from_slice(&bytes)?),
        _ => Err(TransportError::MissingAuthentication),
    }
}

fn decode_text(text: &str) -> Result<RelayWireMessage, TransportError> {
    Ok(serde_json::from_str(text)?)
}

pub struct RelayTransportClient {
    socket: WebSocketStream<MaybeTlsStream<TcpStream>>,
    session_id: Uuid,
}

impl RelayTransportClient {
    pub async fn connect_host(
        url: &str,
        host_id: Uuid,
        token: Vec<u8>,
    ) -> Result<Self, TransportError> {
        Self::connect(
            url,
            RelayWireMessage::AuthenticateHost { host_id, token },
            None,
        )
        .await
    }

    pub async fn connect_host_with_connector(
        url: &str,
        host_id: Uuid,
        token: Vec<u8>,
        connector: Connector,
    ) -> Result<Self, TransportError> {
        Self::connect(
            url,
            RelayWireMessage::AuthenticateHost { host_id, token },
            Some(connector),
        )
        .await
    }

    pub async fn connect_client(
        url: &str,
        client_id: Uuid,
        token: Vec<u8>,
    ) -> Result<Self, TransportError> {
        Self::connect(
            url,
            RelayWireMessage::AuthenticateClient { client_id, token },
            None,
        )
        .await
    }

    pub async fn connect_client_with_connector(
        url: &str,
        client_id: Uuid,
        token: Vec<u8>,
        connector: Connector,
    ) -> Result<Self, TransportError> {
        Self::connect(
            url,
            RelayWireMessage::AuthenticateClient { client_id, token },
            Some(connector),
        )
        .await
    }

    async fn connect(
        url: &str,
        authentication: RelayWireMessage,
        connector: Option<Connector>,
    ) -> Result<Self, TransportError> {
        let parsed = Url::parse(url)?;
        let (mut socket, _) = match connector {
            Some(connector) => {
                connect_async_tls_with_config(parsed.as_str(), None, false, Some(connector)).await?
            }
            None => connect_async(parsed.as_str()).await?,
        };
        send_wire(&mut socket, authentication).await?;
        let response = socket
            .next()
            .await
            .ok_or(TransportError::MissingAuthentication)??;
        match decode_wire(response)? {
            RelayWireMessage::Authenticated { session_id } => Ok(Self { socket, session_id }),
            RelayWireMessage::Rejected { reason } => Err(TransportError::Rejected(reason)),
            _ => Err(TransportError::MissingAuthentication),
        }
    }

    pub fn session_id(&self) -> Uuid {
        self.session_id
    }

    pub async fn send_frame(&mut self, frame: RelayFrameEnvelope) -> Result<(), TransportError> {
        if !frame.is_supported() {
            return Err(TransportError::UnsupportedVersion);
        }
        send_wire(&mut self.socket, RelayWireMessage::Frame(frame)).await
    }

    pub async fn recv_frame(&mut self) -> Result<RelayFrameEnvelope, TransportError> {
        loop {
            let message = self
                .socket
                .next()
                .await
                .ok_or(TransportError::MissingAuthentication)??;
            match message {
                Message::Text(text) => return extract_frame(decode_text(&text)?),
                Message::Binary(bytes) => return extract_frame(serde_json::from_slice(&bytes)?),
                Message::Ping(payload) => self.socket.send(Message::Pong(payload)).await?,
                Message::Pong(_) => {}
                Message::Close(_) => return Err(TransportError::MissingAuthentication),
                _ => return Err(TransportError::UnexpectedMessage),
            }
        }
    }
}

fn extract_frame(message: RelayWireMessage) -> Result<RelayFrameEnvelope, TransportError> {
    match message {
        RelayWireMessage::Frame(frame) if frame.is_supported() => Ok(frame),
        RelayWireMessage::Frame(_) => Err(TransportError::UnsupportedVersion),
        RelayWireMessage::Rejected { reason } => Err(TransportError::Rejected(reason)),
        _ => Err(TransportError::UnexpectedMessage),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use pocketagent_crypto::{SessionKey, open, seal};
    use pocketagent_protocol::{RelayRequest, RelayResponse};

    #[tokio::test]
    async fn authenticates_and_routes_encrypted_frames_over_websocket() {
        let node = RelayNode::new();
        let auth = RelayAuthRegistry::default();
        let host_id = Uuid::new_v4();
        let client_id = Uuid::new_v4();
        auth.register_host(host_id, b"host-secret".to_vec()).await;
        auth.register_client(client_id, b"client-secret".to_vec())
            .await;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("bind should work");
        let url = format!(
            "ws://{}",
            listener.local_addr().expect("address should exist")
        );
        let (shutdown_tx, shutdown_rx) = oneshot::channel();
        let server = RelayTransportServer::new(node, auth);
        let server_task =
            tokio::spawn(async move { server.serve_until(listener, shutdown_rx).await });

        let mut host = RelayTransportClient::connect_host(&url, host_id, b"host-secret".to_vec())
            .await
            .expect("host should authenticate");
        let mut client =
            RelayTransportClient::connect_client(&url, client_id, b"client-secret".to_vec())
                .await
                .expect("client should authenticate");
        assert_ne!(host.session_id(), client.session_id());

        let key = SessionKey::random();
        let request = RelayRequest {
            request_id: Uuid::new_v4(),
            target_host: host_id,
            device_id: client_id,
            envelope: seal(&key, b"host/device/request", b"private request"),
        };
        client
            .send_frame(RelayFrameEnvelope::new(RelayFrame::ClientRequest(
                request.clone(),
            )))
            .await
            .expect("client request should send");
        let received = host
            .recv_frame()
            .await
            .expect("host should receive request");
        let RelayFrame::ClientRequest(received_request) = received.frame else {
            panic!("expected client request");
        };
        assert_eq!(received_request.request_id, request.request_id);
        assert_eq!(
            open(&key, b"host/device/request", &received_request.envelope)
                .expect("request should decrypt"),
            b"private request"
        );

        let response = RelayResponse {
            request_id: request.request_id,
            target_host: host_id,
            envelope: seal(&key, b"host/device/response", b"private response"),
        };
        host.send_frame(RelayFrameEnvelope::new(RelayFrame::HostResponse(response)))
            .await
            .expect("host response should send");
        let received = client
            .recv_frame()
            .await
            .expect("client should receive response");
        let RelayFrame::HostResponse(received_response) = received.frame else {
            panic!("expected host response");
        };
        assert_eq!(
            open(&key, b"host/device/response", &received_response.envelope)
                .expect("response should decrypt"),
            b"private response"
        );

        let _ = shutdown_tx.send(());
        server_task
            .await
            .expect("server task should join")
            .expect("server should stop cleanly");
    }

    #[tokio::test]
    async fn rejects_invalid_authentication_before_session_creation() {
        let node = RelayNode::new();
        let auth = RelayAuthRegistry::default();
        let host_id = Uuid::new_v4();
        auth.register_host(host_id, b"host-secret".to_vec()).await;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("bind should work");
        let url = format!(
            "ws://{}",
            listener.local_addr().expect("address should exist")
        );
        let (shutdown_tx, shutdown_rx) = oneshot::channel();
        let server = RelayTransportServer::new(node, auth);
        let server_task =
            tokio::spawn(async move { server.serve_until(listener, shutdown_rx).await });

        let result =
            RelayTransportClient::connect_host(&url, host_id, b"wrong-secret".to_vec()).await;
        assert!(matches!(result, Err(TransportError::Rejected(_))));

        let _ = shutdown_tx.send(());
        server_task
            .await
            .expect("server task should join")
            .expect("server should stop cleanly");
    }

    #[tokio::test]
    async fn authenticates_and_routes_over_native_tls_websocket() {
        // Several workspace dependencies expose both rustls crypto providers;
        // select the provider used by this test explicitly instead of relying
        // on rustls' ambiguous process-wide auto-detection.
        let _ = rustls::crypto::ring::default_provider().install_default();
        let node = RelayNode::new();
        let auth = RelayAuthRegistry::default();
        let host_id = Uuid::new_v4();
        let client_id = Uuid::new_v4();
        auth.register_host(host_id, b"host-secret".to_vec()).await;
        auth.register_client(client_id, b"client-secret".to_vec())
            .await;

        let certificate = rcgen::generate_simple_self_signed(vec!["localhost".to_owned()])
            .expect("certificate should generate");
        let certificate_der =
            rustls::pki_types::CertificateDer::from(certificate.cert.der().to_vec());
        let private_key = rustls::pki_types::PrivateKeyDer::Pkcs8(
            rustls::pki_types::PrivatePkcs8KeyDer::from(certificate.key_pair.serialize_der()),
        );
        let server_config = rustls::ServerConfig::builder()
            .with_no_client_auth()
            .with_single_cert(vec![certificate_der.clone()], private_key)
            .expect("server certificate should be accepted");
        let acceptor = TlsAcceptor::from(Arc::new(server_config));

        let mut roots = rustls::RootCertStore::empty();
        roots
            .add(certificate_der)
            .expect("test certificate should be trusted");
        let client_config = rustls::ClientConfig::builder()
            .with_root_certificates(roots)
            .with_no_client_auth();
        let connector = Connector::Rustls(Arc::new(client_config));

        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("bind should work");
        let url = format!(
            "wss://localhost:{}",
            listener.local_addr().expect("address should exist").port()
        );
        let (shutdown_tx, shutdown_rx) = oneshot::channel();
        let server = RelayTransportServer::new(node, auth);
        let server_task = tokio::spawn(async move {
            server
                .serve_tls_until(listener, acceptor, shutdown_rx)
                .await
        });

        let mut host = RelayTransportClient::connect_host_with_connector(
            &url,
            host_id,
            b"host-secret".to_vec(),
            connector.clone(),
        )
        .await
        .expect("host should authenticate over TLS");
        let mut client = RelayTransportClient::connect_client_with_connector(
            &url,
            client_id,
            b"client-secret".to_vec(),
            connector,
        )
        .await
        .expect("client should authenticate over TLS");

        let key = SessionKey::random();
        let request = RelayRequest {
            request_id: Uuid::new_v4(),
            target_host: host_id,
            device_id: client_id,
            envelope: seal(&key, b"host/device/request", b"tls request"),
        };
        client
            .send_frame(RelayFrameEnvelope::new(RelayFrame::ClientRequest(
                request.clone(),
            )))
            .await
            .expect("client request should send");
        let received = host
            .recv_frame()
            .await
            .expect("host should receive request");
        let RelayFrame::ClientRequest(received_request) = received.frame else {
            panic!("expected client request");
        };
        assert_eq!(received_request.request_id, request.request_id);
        assert_eq!(
            open(&key, b"host/device/request", &received_request.envelope)
                .expect("request should decrypt"),
            b"tls request"
        );

        let response = RelayResponse {
            request_id: request.request_id,
            target_host: host_id,
            envelope: seal(&key, b"host/device/response", b"tls response"),
        };
        host.send_frame(RelayFrameEnvelope::new(RelayFrame::HostResponse(response)))
            .await
            .expect("host response should send");
        let received = client
            .recv_frame()
            .await
            .expect("client should receive response");
        let RelayFrame::HostResponse(received_response) = received.frame else {
            panic!("expected host response");
        };
        assert_eq!(
            open(&key, b"host/device/response", &received_response.envelope)
                .expect("response should decrypt"),
            b"tls response"
        );

        let _ = shutdown_tx.send(());
        server_task
            .await
            .expect("server task should join")
            .expect("server should stop cleanly");
    }
}
