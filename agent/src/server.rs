use std::time::Duration;

use tokio::io::{AsyncRead, AsyncWrite};
use tokio::time::{Instant, interval, timeout};

use crate::auth::PairingToken;
use crate::frame::{FrameError, RawFrame, TileEncoder};
use crate::messages::{
    MessageError, MouseReport, error, frame, keyboard_report, mouse_report, parse_client_hello,
    server_hello,
};
use crate::protocol::{Envelope, MessageKind, ProtocolError};

pub type BoxError = Box<dyn std::error::Error + Send + Sync + 'static>;

pub trait CaptureSource {
    fn capture(&mut self) -> Result<RawFrame, BoxError>;
}

pub trait InputSink {
    fn keyboard(&mut self, report: [u8; 8]) -> Result<(), BoxError>;
    fn mouse(&mut self, report: MouseReport) -> Result<(), BoxError>;
    fn release_all(&mut self) -> Result<(), BoxError>;
}

#[derive(Clone, Copy, Debug)]
pub struct SessionSettings {
    pub frames_per_second: u8,
    pub jpeg_quality: u8,
    pub authentication_timeout: Duration,
    pub idle_timeout: Duration,
}

impl Default for SessionSettings {
    fn default() -> Self {
        Self {
            frames_per_second: 10,
            jpeg_quality: 70,
            authentication_timeout: Duration::from_secs(10),
            idle_timeout: Duration::from_secs(30),
        }
    }
}

pub async fn serve_stream<S, C, I>(
    mut stream: S,
    token: &PairingToken,
    capture: &mut C,
    input: &mut I,
    settings: SessionSettings,
) -> Result<(), ServerError>
where
    S: AsyncRead + AsyncWrite + Unpin,
    C: CaptureSource,
    I: InputSink,
{
    validate_settings(settings)?;
    authenticate_stream(&mut stream, token, settings.authentication_timeout).await?;
    serve_authenticated_stream(stream, capture, input, settings).await
}

pub async fn authenticate_stream<S>(
    stream: &mut S,
    token: &PairingToken,
    authentication_timeout: Duration,
) -> Result<(), ServerError>
where
    S: AsyncRead + AsyncWrite + Unpin,
{
    const MAXIMUM_CLIENT_HELLO_PAYLOAD: usize = 2 + 512;
    let hello = timeout(
        authentication_timeout,
        Envelope::read_from_with_limit(stream, MAXIMUM_CLIENT_HELLO_PAYLOAD),
    )
    .await
    .map_err(|_| ServerError::AuthenticationTimeout)??;
    let authenticated = parse_client_hello(&hello)
        .map(|candidate| token.verify(candidate))
        .unwrap_or(false);
    if authenticated {
        return Ok(());
    }
    error(1, "authentication failed")?.write_to(stream).await?;
    Err(ServerError::AuthenticationFailed)
}

pub async fn serve_authenticated_stream<S, C, I>(
    stream: S,
    capture: &mut C,
    input: &mut I,
    settings: SessionSettings,
) -> Result<(), ServerError>
where
    S: AsyncRead + AsyncWrite + Unpin,
    C: CaptureSource,
    I: InputSink,
{
    validate_settings(settings)?;
    let (mut reader, mut writer) = tokio::io::split(stream);
    let initial = capture.capture().map_err(ServerError::Capture)?;
    initial.validate()?;
    let mut encoder = TileEncoder::new(settings.jpeg_quality, 64)?;
    server_hello(
        initial.width,
        initial.height,
        settings.frames_per_second,
        encoder.tile_size(),
    )?
    .write_to(&mut writer)
    .await?;

    let initial_width = initial.width;
    let initial_height = initial.height;
    let mut sequence = 1u32;
    let (keyframe, tiles) = encoder.encode(initial, true)?;
    frame(
        sequence,
        initial_width,
        initial_height,
        keyframe,
        encoder.tile_size(),
        &tiles,
    )?
    .write_to(&mut writer)
    .await?;

    let period = Duration::from_secs_f64(1.0 / f64::from(settings.frames_per_second));
    let mut frame_timer = interval(period);
    frame_timer.reset();
    let mut last_received = Instant::now();
    let mut force_keyframe = false;
    let result = loop {
        tokio::select! {
            incoming = Envelope::read_from(&mut reader) => {
                let incoming = match incoming {
                    Ok(value) => value,
                    Err(ProtocolError::Io(error)) if error.kind() == std::io::ErrorKind::UnexpectedEof => break Ok(()),
                    Err(error) => break Err(ServerError::Protocol(error)),
                };
                last_received = Instant::now();
                match incoming.kind {
                    MessageKind::Keyboard => input.keyboard(keyboard_report(&incoming)?).map_err(ServerError::Input)?,
                    MessageKind::Mouse => input.mouse(mouse_report(&incoming)?).map_err(ServerError::Input)?,
                    MessageKind::KeyframeRequest if incoming.payload.is_empty() => force_keyframe = true,
                    MessageKind::Ping if incoming.payload.len() == 8 => {
                        Envelope::new(MessageKind::Pong, incoming.payload)?.write_to(&mut writer).await?;
                    }
                    _ => break Err(ServerError::UnexpectedMessage(incoming.kind)),
                }
            }
            _ = frame_timer.tick() => {
                if last_received.elapsed() > settings.idle_timeout {
                    break Err(ServerError::IdleTimeout);
                }
                let raw = capture.capture().map_err(ServerError::Capture)?;
                let width = raw.width;
                let height = raw.height;
                let (keyframe, tiles) = encoder.encode(raw, force_keyframe)?;
                force_keyframe = false;
                if keyframe || !tiles.is_empty() {
                    sequence = sequence.wrapping_add(1);
                    frame(sequence, width, height, keyframe, encoder.tile_size(), &tiles)?
                        .write_to(&mut writer)
                        .await?;
                }
            }
        }
    };
    let release_result = input.release_all().map_err(ServerError::Input);
    result.and(release_result)
}

fn validate_settings(settings: SessionSettings) -> Result<(), ServerError> {
    if settings.frames_per_second == 0 || settings.frames_per_second > 30 {
        return Err(ServerError::InvalidSettings);
    }
    Ok(())
}

#[derive(Debug, thiserror::Error)]
pub enum ServerError {
    #[error("agent session settings are invalid")]
    InvalidSettings,
    #[error("agent authentication timed out")]
    AuthenticationTimeout,
    #[error("agent authentication failed")]
    AuthenticationFailed,
    #[error("agent session was idle for too long")]
    IdleTimeout,
    #[error("agent received an unexpected {0:?} message")]
    UnexpectedMessage(MessageKind),
    #[error("agent capture failed: {0}")]
    Capture(BoxError),
    #[error("agent input failed: {0}")]
    Input(BoxError),
    #[error("agent protocol failed: {0}")]
    Protocol(#[from] ProtocolError),
    #[error("agent message failed: {0}")]
    Message(#[from] MessageError),
    #[error("agent frame encoding failed: {0}")]
    Frame(#[from] FrameError),
}

#[cfg(test)]
mod tests {
    use std::collections::VecDeque;

    use base64::Engine as _;
    use tokio::io::duplex;

    use super::*;
    use crate::messages::client_hello;

    #[tokio::test]
    async fn authenticates_sends_frame_and_routes_input() {
        let directory = crate::test_support::TestDirectory::new();
        let token_path = directory.path().join("token");
        let encoded = PairingToken::generate(&token_path).unwrap();
        let token = PairingToken::load(&token_path).unwrap();
        let decoded = base64::engine::general_purpose::STANDARD
            .decode(encoded)
            .unwrap();
        let (client, server) = duplex(1024 * 1024);
        let mut capture = MockCapture::new();
        let mut input = MockInput::default();
        let settings = SessionSettings {
            frames_per_second: 1,
            idle_timeout: Duration::from_secs(5),
            ..SessionSettings::default()
        };

        let server_task = tokio::spawn(async move {
            let result = serve_stream(server, &token, &mut capture, &mut input, settings).await;
            (result, input)
        });
        let (mut reader, mut writer) = tokio::io::split(client);
        client_hello(&decoded)
            .unwrap()
            .write_to(&mut writer)
            .await
            .unwrap();
        assert_eq!(
            MessageKind::ServerHello,
            Envelope::read_from(&mut reader).await.unwrap().kind
        );
        assert_eq!(
            MessageKind::Frame,
            Envelope::read_from(&mut reader).await.unwrap().kind
        );
        Envelope::new(MessageKind::Keyboard, vec![0, 0, 4, 0, 0, 0, 0, 0])
            .unwrap()
            .write_to(&mut writer)
            .await
            .unwrap();
        drop(writer);
        drop(reader);

        let (result, input) = server_task.await.unwrap();
        result.unwrap();
        assert_eq!(vec![[0, 0, 4, 0, 0, 0, 0, 0]], input.keyboards);
        assert!(input.released);
    }

    #[tokio::test]
    async fn rejects_oversized_authentication_message_without_capture() {
        let directory = crate::test_support::TestDirectory::new();
        let token_path = directory.path().join("token");
        PairingToken::generate(&token_path).unwrap();
        let token = PairingToken::load(&token_path).unwrap();
        let (mut client, server) = duplex(4096);
        let mut capture = PanicCapture;
        let mut input = MockInput::default();

        let server_task = tokio::spawn(async move {
            serve_stream(
                server,
                &token,
                &mut capture,
                &mut input,
                SessionSettings::default(),
            )
            .await
        });
        let mut header = [0u8; crate::protocol::HEADER_LENGTH];
        header[..4].copy_from_slice(&crate::protocol::MAGIC);
        header[4] = crate::protocol::VERSION;
        header[5] = MessageKind::ClientHello as u8;
        header[8..12].copy_from_slice(&515u32.to_be_bytes());
        use tokio::io::AsyncWriteExt as _;
        client.write_all(&header).await.unwrap();

        assert!(matches!(
            server_task.await.unwrap(),
            Err(ServerError::Protocol(ProtocolError::PayloadTooLarge(515)))
        ));
    }

    struct MockCapture {
        frames: VecDeque<RawFrame>,
    }

    struct PanicCapture;

    impl CaptureSource for PanicCapture {
        fn capture(&mut self) -> Result<RawFrame, BoxError> {
            panic!("capture must not run before authentication")
        }
    }

    impl MockCapture {
        fn new() -> Self {
            Self {
                frames: VecDeque::from([RawFrame {
                    width: 2,
                    height: 1,
                    rgb: vec![0; 6],
                }]),
            }
        }
    }

    impl CaptureSource for MockCapture {
        fn capture(&mut self) -> Result<RawFrame, BoxError> {
            Ok(self.frames.front().unwrap().clone())
        }
    }

    #[derive(Default)]
    struct MockInput {
        keyboards: Vec<[u8; 8]>,
        released: bool,
    }

    impl InputSink for MockInput {
        fn keyboard(&mut self, report: [u8; 8]) -> Result<(), BoxError> {
            self.keyboards.push(report);
            Ok(())
        }

        fn mouse(&mut self, _report: MouseReport) -> Result<(), BoxError> {
            Ok(())
        }

        fn release_all(&mut self) -> Result<(), BoxError> {
            self.released = true;
            Ok(())
        }
    }
}
