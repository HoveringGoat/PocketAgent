use pocketagent_crypto::{SessionKey, open, seal};
use pocketagent_protocol::{RelayFrame, RelayFrameEnvelope, RelayRequest, RelayResponse};
use pocketagent_relay::RelayNode;
use pocketagent_relay::transport::{RelayAuthRegistry, RelayTransportClient, RelayTransportServer};
use std::{env, error::Error, io};
use tokio::{net::TcpListener, sync::oneshot};
use uuid::Uuid;

#[tokio::main(flavor = "current_thread")]
async fn main() -> Result<(), Box<dyn Error>> {
    match env::args().nth(1).as_deref() {
        Some("demo") => run_demo().await,
        _ => {
            print_usage();
            Ok(())
        }
    }
}

fn print_usage() {
    println!("PocketAgent Rust relay MVP");
    println!();
    println!("  cargo run -p pocketagent-relay --bin pocketagent-relay -- demo");
    println!();
    println!("Runs a local authenticated relay round trip over ws:// with encrypted payloads.");
    println!("This demo is for transport validation; it is not a production relay service.");
}

async fn run_demo() -> Result<(), Box<dyn Error>> {
    let node = RelayNode::new();
    let auth = RelayAuthRegistry::default();
    let host_id = Uuid::new_v4();
    let client_id = Uuid::new_v4();
    let host_token = b"demo-host-token".to_vec();
    let client_token = b"demo-client-token".to_vec();
    auth.register_host(host_id, host_token.clone()).await;
    auth.register_client(client_id, client_token.clone()).await;

    let listener = TcpListener::bind("127.0.0.1:0").await?;
    let url = format!("ws://{}", listener.local_addr()?);
    let (shutdown_tx, shutdown_rx) = oneshot::channel();
    let server = RelayTransportServer::new(node, auth);
    let server_task = tokio::spawn(async move { server.serve_until(listener, shutdown_rx).await });

    println!("relay listening at {url}");
    let mut host = RelayTransportClient::connect_host(&url, host_id, host_token).await?;
    let mut client = RelayTransportClient::connect_client(&url, client_id, client_token).await?;
    println!("host and client authenticated");

    let key = SessionKey::random();
    let request = RelayRequest {
        request_id: Uuid::new_v4(),
        target_host: host_id,
        device_id: client_id,
        envelope: seal(&key, b"demo/request", b"hello from client"),
    };
    client
        .send_frame(RelayFrameEnvelope::new(RelayFrame::ClientRequest(
            request.clone(),
        )))
        .await?;
    let received = host.recv_frame().await?;
    let RelayFrame::ClientRequest(received_request) = received.frame else {
        return Err(io::Error::other("relay returned an unexpected request frame").into());
    };
    if open(&key, b"demo/request", &received_request.envelope)? != b"hello from client" {
        return Err(io::Error::other("request payload failed decryption").into());
    }

    let response = RelayResponse {
        request_id: request.request_id,
        target_host: host_id,
        envelope: seal(&key, b"demo/response", b"hello from host"),
    };
    host.send_frame(RelayFrameEnvelope::new(RelayFrame::HostResponse(response)))
        .await?;
    let received = client.recv_frame().await?;
    let RelayFrame::HostResponse(received_response) = received.frame else {
        return Err(io::Error::other("relay returned an unexpected response frame").into());
    };
    if open(&key, b"demo/response", &received_response.envelope)? != b"hello from host" {
        return Err(io::Error::other("response payload failed decryption").into());
    }

    let _ = shutdown_tx.send(());
    server_task.await??;
    println!("relay demo passed: authenticated encrypted request/response round trip");
    Ok(())
}
