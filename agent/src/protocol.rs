use std::io;

use thiserror::Error;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

pub const MAGIC: [u8; 4] = *b"IKAG";
pub const VERSION: u8 = 1;
pub const HEADER_LENGTH: usize = 12;
pub const MAX_PAYLOAD_LENGTH: usize = 16 * 1024 * 1024;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum MessageKind {
    ClientHello = 1,
    ServerHello = 2,
    Frame = 3,
    Keyboard = 4,
    Mouse = 5,
    KeyframeRequest = 6,
    Ping = 7,
    Pong = 8,
    Error = 9,
}

impl TryFrom<u8> for MessageKind {
    type Error = ProtocolError;

    fn try_from(value: u8) -> Result<Self, ProtocolError> {
        match value {
            1 => Ok(Self::ClientHello),
            2 => Ok(Self::ServerHello),
            3 => Ok(Self::Frame),
            4 => Ok(Self::Keyboard),
            5 => Ok(Self::Mouse),
            6 => Ok(Self::KeyframeRequest),
            7 => Ok(Self::Ping),
            8 => Ok(Self::Pong),
            9 => Ok(Self::Error),
            _ => Err(ProtocolError::UnknownMessageKind(value)),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Envelope {
    pub kind: MessageKind,
    pub payload: Vec<u8>,
}

impl Envelope {
    pub fn new(kind: MessageKind, payload: Vec<u8>) -> Result<Self, ProtocolError> {
        validate_payload_length(payload.len())?;
        Ok(Self { kind, payload })
    }

    pub fn encode(&self) -> Result<Vec<u8>, ProtocolError> {
        validate_payload_length(self.payload.len())?;
        let mut bytes = Vec::with_capacity(HEADER_LENGTH + self.payload.len());
        bytes.extend_from_slice(&MAGIC);
        bytes.push(VERSION);
        bytes.push(self.kind as u8);
        bytes.extend_from_slice(&0u16.to_be_bytes());
        bytes.extend_from_slice(&(self.payload.len() as u32).to_be_bytes());
        bytes.extend_from_slice(&self.payload);
        Ok(bytes)
    }

    pub async fn read_from<R>(reader: &mut R) -> Result<Self, ProtocolError>
    where
        R: AsyncRead + Unpin,
    {
        Self::read_from_with_limit(reader, MAX_PAYLOAD_LENGTH).await
    }

    pub async fn read_from_with_limit<R>(
        reader: &mut R,
        maximum_payload_length: usize,
    ) -> Result<Self, ProtocolError>
    where
        R: AsyncRead + Unpin,
    {
        let mut header = [0u8; HEADER_LENGTH];
        reader.read_exact(&mut header).await?;
        if header[..4] != MAGIC {
            return Err(ProtocolError::InvalidMagic);
        }
        if header[4] != VERSION {
            return Err(ProtocolError::UnsupportedVersion(header[4]));
        }
        if u16::from_be_bytes([header[6], header[7]]) != 0 {
            return Err(ProtocolError::ReservedFlags);
        }

        let kind = MessageKind::try_from(header[5])?;
        let length = u32::from_be_bytes(header[8..12].try_into().expect("fixed header")) as usize;
        validate_payload_length(length)?;
        if length > maximum_payload_length {
            return Err(ProtocolError::PayloadTooLarge(length));
        }
        let mut payload = vec![0u8; length];
        reader.read_exact(&mut payload).await?;
        Ok(Self { kind, payload })
    }

    pub async fn write_to<W>(&self, writer: &mut W) -> Result<(), ProtocolError>
    where
        W: AsyncWrite + Unpin,
    {
        let bytes = self.encode()?;
        writer.write_all(&bytes).await?;
        writer.flush().await?;
        Ok(())
    }
}

fn validate_payload_length(length: usize) -> Result<(), ProtocolError> {
    if length > MAX_PAYLOAD_LENGTH {
        return Err(ProtocolError::PayloadTooLarge(length));
    }
    Ok(())
}

#[derive(Debug, Error)]
pub enum ProtocolError {
    #[error("agent protocol magic is invalid")]
    InvalidMagic,
    #[error("agent protocol version {0} is unsupported")]
    UnsupportedVersion(u8),
    #[error("agent protocol reserved flags must be zero")]
    ReservedFlags,
    #[error("agent protocol message kind {0} is unknown")]
    UnknownMessageKind(u8),
    #[error("agent protocol payload length {0} exceeds the configured maximum")]
    PayloadTooLarge(usize),
    #[error("agent protocol I/O failed: {0}")]
    Io(#[from] io::Error),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encodes_shared_ping_vector() {
        let envelope = Envelope::new(
            MessageKind::Ping,
            0x0102_0304_0506_0708u64.to_be_bytes().to_vec(),
        )
        .unwrap();

        assert_eq!(
            envelope.encode().unwrap(),
            hex("494b414701070000000000080102030405060708")
        );
    }

    #[tokio::test]
    async fn rejects_oversized_payload_before_allocation() {
        let bytes = hex("494b41470103000001000001");
        let error = Envelope::read_from(&mut bytes.as_slice())
            .await
            .unwrap_err();

        assert!(matches!(error, ProtocolError::PayloadTooLarge(16_777_217)));
    }

    #[tokio::test]
    async fn rejects_payload_above_message_limit_before_allocation() {
        let bytes = hex("494b41470101000000000203");
        let error = Envelope::read_from_with_limit(&mut bytes.as_slice(), 514)
            .await
            .unwrap_err();

        assert!(matches!(error, ProtocolError::PayloadTooLarge(515)));
    }

    fn hex(value: &str) -> Vec<u8> {
        value
            .as_bytes()
            .chunks_exact(2)
            .map(|pair| {
                let text = std::str::from_utf8(pair).unwrap();
                u8::from_str_radix(text, 16).unwrap()
            })
            .collect()
    }
}
