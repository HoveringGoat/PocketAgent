use pocketagent_crypto::{SessionKey, open, seal};
use pocketagent_protocol::{
    OpaqueEnvelope, RelayFrame, RelayFrameEnvelope, RelayRequest, RelayResponse,
};
use pocketagent_relay::RelayNode;
use pocketagent_relay::config::RelayConfig;
use pocketagent_relay::transport::{RelayAuthRegistry, RelayTransportClient, RelayTransportServer};
use std::{env, error::Error, fs, io, path::Path};
use tokio::{net::TcpListener, sync::oneshot};
use uuid::Uuid;

#[tokio::main(flavor = "current_thread")]
async fn main() -> Result<(), Box<dyn Error>> {
    match env::args().nth(1).as_deref() {
        Some("demo") => run_demo().await,
        Some("serve") => run_serve().await,
        Some("init-config") => run_init_config(),
        Some("serve-config") => run_serve_config().await,
        Some("host") => run_host().await,
        Some("client") => run_client().await,
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
    println!(
        "  cargo run -p pocketagent-relay --bin pocketagent-relay -- serve <bind> <host-id> <host-token> <client-id> <client-token>"
    );
    println!("  cargo run -p pocketagent-relay --bin pocketagent-relay -- init-config <path>");
    println!(
        "  cargo run -p pocketagent-relay --bin pocketagent-relay -- serve-config <bind> <path>"
    );
    println!(
        "  cargo run -p pocketagent-relay --bin pocketagent-relay -- host <url> <host-id> <host-token>"
    );
    println!(
        "  cargo run -p pocketagent-relay --bin pocketagent-relay -- client <url> <host-id> <client-id> <client-token>"
    );
    println!();
    println!("Runs a local authenticated relay round trip over ws:// with encrypted payloads.");
    println!(
        "serve/host/client are process-level opaque routing probes; they are not production commands."
    );
}

async fn run_serve() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = env::args().skip(2).collect();
    if args.len() != 5 {
        print_usage();
        return Err(
            io::Error::new(io::ErrorKind::InvalidInput, "serve expects five arguments").into(),
        );
    }
    let bind = &args[0];
    let host_id = args[1].parse::<Uuid>()?;
    let client_id = args[3].parse::<Uuid>()?;
    let auth = RelayAuthRegistry::default();
    auth.register_host(host_id, args[2].as_bytes().to_vec())
        .await;
    auth.register_client(client_id, args[4].as_bytes().to_vec())
        .await;
    let listener = TcpListener::bind(bind).await?;
    println!("relay listening at ws://{}", listener.local_addr()?);
    println!("press Ctrl+C to stop");
    let server = RelayTransportServer::new(RelayNode::new(), auth);
    let (_shutdown_tx, shutdown_rx) = oneshot::channel();
    server.serve_until(listener, shutdown_rx).await?;
    Ok(())
}

fn run_init_config() -> Result<(), Box<dyn Error>> {
    let path = env::args()
        .nth(2)
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "init-config expects a path"))?;
    let path = Path::new(&path);
    if path.exists() {
        return Err(
            io::Error::new(io::ErrorKind::AlreadyExists, "config file already exists").into(),
        );
    }
    let config = RelayConfig::new();
    let encoded = serde_json::to_string_pretty(&config)?;
    fs::write(path, format!("{encoded}\n"))?;
    println!("created relay config at {}", path.display());
    println!("host id: {}", config.host_id);
    println!("client id: {}", config.client_id);
    println!("tokens are stored only in the config file");
    Ok(())
}

async fn run_serve_config() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = env::args().skip(2).collect();
    if args.len() != 2 {
        print_usage();
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "serve-config expects bind and path",
        )
        .into());
    }
    let config: RelayConfig = serde_json::from_str(&fs::read_to_string(&args[1])?)?;
    let auth = RelayAuthRegistry::default();
    auth.register_host(config.host_id, config.host_token.into_bytes())
        .await;
    auth.register_client(config.client_id, config.client_token.into_bytes())
        .await;
    let listener = TcpListener::bind(&args[0]).await?;
    println!("relay listening at ws://{}", listener.local_addr()?);
    println!("loaded identities from {}", args[1]);
    println!("press Ctrl+C to stop");
    let server = RelayTransportServer::new(RelayNode::new(), auth);
    let (_shutdown_tx, shutdown_rx) = oneshot::channel();
    server.serve_until(listener, shutdown_rx).await?;
    Ok(())
}

async fn run_host() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = env::args().skip(2).collect();
    if args.len() != 3 {
        print_usage();
        return Err(
            io::Error::new(io::ErrorKind::InvalidInput, "host expects three arguments").into(),
        );
    }
    let host_id = args[1].parse::<Uuid>()?;
    let mut host =
        RelayTransportClient::connect_host(&args[0], host_id, args[2].as_bytes().to_vec()).await?;
    let received = host.recv_frame().await?;
    let RelayFrame::ClientRequest(request) = received.frame else {
        return Err(io::Error::other("host received an unexpected frame").into());
    };
    let response = RelayResponse {
        request_id: request.request_id,
        target_host: request.target_host,
        envelope: request.envelope,
    };
    host.send_frame(RelayFrameEnvelope::new(RelayFrame::HostResponse(response)))
        .await?;
    println!("host echoed request {}", request.request_id);
    Ok(())
}

async fn run_client() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = env::args().skip(2).collect();
    if args.len() != 4 {
        print_usage();
        return Err(
            io::Error::new(io::ErrorKind::InvalidInput, "client expects four arguments").into(),
        );
    }
    let host_id = args[1].parse::<Uuid>()?;
    let client_id = args[2].parse::<Uuid>()?;
    let mut client =
        RelayTransportClient::connect_client(&args[0], client_id, args[3].as_bytes().to_vec())
            .await?;
    let request = RelayRequest {
        request_id: Uuid::new_v4(),
        target_host: host_id,
        device_id: client_id,
        envelope: OpaqueEnvelope {
            key_id: "cli-probe".into(),
            nonce: vec![0; 12],
            ciphertext: b"opaque probe".to_vec(),
        },
    };
    client
        .send_frame(RelayFrameEnvelope::new(RelayFrame::ClientRequest(
            request.clone(),
        )))
        .await?;
    let received = client.recv_frame().await?;
    let RelayFrame::HostResponse(response) = received.frame else {
        return Err(io::Error::other("client received an unexpected frame").into());
    };
    if response.request_id != request.request_id || response.envelope != request.envelope {
        return Err(io::Error::other("host response did not preserve the opaque envelope").into());
    }
    println!("client received echoed response {}", response.request_id);
    Ok(())
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
