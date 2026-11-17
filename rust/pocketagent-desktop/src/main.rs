use pocketagent_relay::RelayNode;
use pocketagent_relay::config::RelayConfig;
use pocketagent_relay::transport::{RelayAuthRegistry, RelayTransportServer};
use std::sync::{Arc, Mutex};
use tokio::net::TcpListener;
use tokio::sync::oneshot;

slint::include_modules!();

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let window = AppWindow::new()?;
    let relay_shutdown: Arc<Mutex<Option<oneshot::Sender<()>>>> = Arc::new(Mutex::new(None));

    {
        let weak = window.as_weak();
        let relay_shutdown = Arc::clone(&relay_shutdown);
        window.on_start_relay(move || {
            if relay_shutdown
                .lock()
                .expect("relay state lock should work")
                .is_some()
            {
                return;
            }
            let weak = weak.clone();
            let relay_shutdown = Arc::clone(&relay_shutdown);
            std::thread::spawn(move || {
                let runtime = match tokio::runtime::Builder::new_current_thread()
                    .enable_all()
                    .build()
                {
                    Ok(runtime) => runtime,
                    Err(error) => {
                        set_ui_error(&weak, format!("Runtime failed: {error}"));
                        return;
                    }
                };
                runtime.block_on(async move {
                    let listener = match TcpListener::bind("127.0.0.1:0").await {
                        Ok(listener) => listener,
                        Err(error) => {
                            set_ui_error(&weak, format!("Could not bind relay: {error}"));
                            return;
                        }
                    };
                    let address = match listener.local_addr() {
                        Ok(address) => address,
                        Err(error) => {
                            set_ui_error(&weak, format!("Could not read relay address: {error}"));
                            return;
                        }
                    };
                    let config = RelayConfig::new();
                    let auth = RelayAuthRegistry::default();
                    auth.register_host(config.host_id, config.host_token.into_bytes())
                        .await;
                    auth.register_client(config.client_id, config.client_token.into_bytes())
                        .await;
                    let (shutdown_tx, shutdown_rx) = oneshot::channel();
                    *relay_shutdown.lock().expect("relay state lock should work") =
                        Some(shutdown_tx);
                    set_ui_running(&weak, address.to_string(), config.host_id.to_string());
                    let server = RelayTransportServer::new(RelayNode::new(), auth);
                    let result = server.serve_until(listener, shutdown_rx).await;
                    *relay_shutdown.lock().expect("relay state lock should work") = None;
                    if let Err(error) = result {
                        set_ui_error(&weak, format!("Relay stopped with an error: {error}"));
                    } else {
                        set_ui_stopped(&weak);
                    }
                });
            });
        });
    }

    {
        let weak = window.as_weak();
        let relay_shutdown = Arc::clone(&relay_shutdown);
        window.on_stop_relay(move || {
            if let Some(shutdown) = relay_shutdown
                .lock()
                .expect("relay state lock should work")
                .take()
            {
                let _ = shutdown.send(());
            } else {
                set_ui_stopped(&weak);
            }
        });
    }

    window.run()?;
    Ok(())
}

fn set_ui_running(weak: &slint::Weak<AppWindow>, endpoint: String, host_id: String) {
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_relay_running(true);
        window.set_status("Relay is running".into());
        window.set_endpoint(format!("ws://{endpoint}  •  host {host_id}").into());
    });
}

fn set_ui_stopped(weak: &slint::Weak<AppWindow>) {
    let _ = weak.upgrade_in_event_loop(|window| {
        window.set_relay_running(false);
        window.set_status("Relay is stopped".into());
        window.set_endpoint("No endpoint".into());
    });
}

fn set_ui_error(weak: &slint::Weak<AppWindow>, message: String) {
    let _ = weak.upgrade_in_event_loop(move |window| {
        window.set_relay_running(false);
        window.set_status(message.into());
        window.set_endpoint("No endpoint".into());
    });
}
