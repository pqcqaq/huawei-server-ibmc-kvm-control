use std::net::SocketAddr;
use std::path::PathBuf;
use std::time::Duration;

use clap::{Parser, Subcommand};
use ibmc_kvm_agent::auth::PairingToken;
use ibmc_kvm_agent::config::{default_config_directory, initialize};
use ibmc_kvm_agent::server::{SessionSettings, authenticate_stream, serve_authenticated_stream};
use ibmc_kvm_agent::tls::load_acceptor;
use ibmc_kvm_agent::x11::capture::X11CaptureSource;
use ibmc_kvm_agent::x11::input::X11InputSink;
use tokio::net::TcpListener;
use tokio::time::timeout;

#[derive(Parser)]
#[command(version, about = "Secure X11 remote-desktop agent for iBMC KVM")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Create a self-signed certificate and high-entropy pairing token.
    Init {
        #[arg(long)]
        directory: Option<PathBuf>,
        #[arg(long, default_value = "localhost")]
        certificate_name: String,
    },
    /// Run the TLS Agent Protocol v1 server for the active X11 session.
    Serve {
        #[arg(long, default_value = "0.0.0.0:7443")]
        bind: SocketAddr,
        #[arg(long)]
        directory: Option<PathBuf>,
        #[arg(long)]
        display: Option<String>,
        #[arg(long, default_value_t = 10, value_parser = clap::value_parser!(u8).range(1..=30))]
        fps: u8,
        #[arg(long, default_value_t = 70, value_parser = clap::value_parser!(u8).range(40..=95))]
        jpeg_quality: u8,
    },
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    match Cli::parse().command {
        Command::Init {
            directory,
            certificate_name,
        } => {
            let directory = directory.map(Ok).unwrap_or_else(default_config_directory)?;
            let config = initialize(&directory, &certificate_name)?;
            println!("Configuration: {}", config.directory.display());
            println!("Certificate SHA-256: {}", config.certificate_fingerprint);
            println!("Pairing token (shown once): {}", config.pairing_token);
        }
        Command::Serve {
            bind,
            directory,
            display,
            fps,
            jpeg_quality,
        } => {
            let directory = directory.map(Ok).unwrap_or_else(default_config_directory)?;
            let acceptor = load_acceptor(
                &directory.join("agent-cert.pem"),
                &directory.join("agent-key.pem"),
            )?;
            let token = PairingToken::load(&directory.join("pairing-token"))?;
            let listener = TcpListener::bind(bind).await?;
            eprintln!("ibmc-kvm-agent listening on {bind}");
            loop {
                let (socket, peer) = listener.accept().await?;
                socket.set_nodelay(true)?;
                let tls = match timeout(Duration::from_secs(10), acceptor.accept(socket)).await {
                    Ok(Ok(stream)) => stream,
                    Ok(Err(error)) => {
                        eprintln!("TLS handshake from {peer} failed: {error}");
                        continue;
                    }
                    Err(_) => {
                        eprintln!("TLS handshake from {peer} timed out");
                        continue;
                    }
                };
                let settings = SessionSettings {
                    frames_per_second: fps,
                    jpeg_quality,
                    ..SessionSettings::default()
                };
                let mut tls = tls;
                if let Err(error) =
                    authenticate_stream(&mut tls, &token, settings.authentication_timeout).await
                {
                    eprintln!("controller authentication from {peer} failed: {error}");
                    continue;
                }
                let mut capture = match X11CaptureSource::connect(display.as_deref()) {
                    Ok(value) => value,
                    Err(error) => {
                        eprintln!("X11 capture initialization failed: {error}");
                        continue;
                    }
                };
                let mut input = match X11InputSink::connect(display.as_deref()) {
                    Ok(value) => value,
                    Err(error) => {
                        eprintln!("X11 input initialization failed: {error}");
                        continue;
                    }
                };
                eprintln!("controller connected from {peer}");
                if let Err(error) =
                    serve_authenticated_stream(tls, &mut capture, &mut input, settings).await
                {
                    eprintln!("controller session from {peer} ended: {error}");
                }
            }
        }
    }
    Ok(())
}
