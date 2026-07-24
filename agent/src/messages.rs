use crate::protocol::{Envelope, MessageKind, ProtocolError};

pub const CAPABILITY_KEYBOARD: u16 = 1 << 0;
pub const CAPABILITY_MOUSE: u16 = 1 << 1;
pub const CAPABILITY_ABSOLUTE_MOUSE: u16 = 1 << 2;
pub const TILE_CODEC_JPEG: u8 = 1;

#[derive(Debug, thiserror::Error)]
pub enum MessageError {
    #[error("agent message payload is malformed: {0}")]
    Malformed(&'static str),
    #[error("agent protocol error: {0}")]
    Protocol(#[from] ProtocolError),
}

pub fn client_hello(token: &[u8]) -> Result<Envelope, MessageError> {
    if token.len() < 32 || token.len() > 512 {
        return Err(MessageError::Malformed("pairing token length is invalid"));
    }
    let mut payload = Vec::with_capacity(2 + token.len());
    payload.extend_from_slice(&(token.len() as u16).to_be_bytes());
    payload.extend_from_slice(token);
    Ok(Envelope::new(MessageKind::ClientHello, payload)?)
}

pub fn parse_client_hello(envelope: &Envelope) -> Result<&[u8], MessageError> {
    if envelope.kind != MessageKind::ClientHello || envelope.payload.len() < 2 {
        return Err(MessageError::Malformed("client hello is missing"));
    }
    let length = u16::from_be_bytes([envelope.payload[0], envelope.payload[1]]) as usize;
    if !(32..=512).contains(&length) || envelope.payload.len() != length + 2 {
        return Err(MessageError::Malformed("pairing token length is invalid"));
    }
    Ok(&envelope.payload[2..])
}

pub fn server_hello(
    width: u16,
    height: u16,
    fps: u8,
    tile_size: u8,
) -> Result<Envelope, MessageError> {
    if width == 0 || height == 0 || fps == 0 || tile_size == 0 {
        return Err(MessageError::Malformed("server dimensions are invalid"));
    }
    let mut payload = Vec::with_capacity(8);
    payload.extend_from_slice(&width.to_be_bytes());
    payload.extend_from_slice(&height.to_be_bytes());
    payload.push(fps);
    payload.push(tile_size);
    payload.extend_from_slice(
        &(CAPABILITY_KEYBOARD | CAPABILITY_MOUSE | CAPABILITY_ABSOLUTE_MOUSE).to_be_bytes(),
    );
    Ok(Envelope::new(MessageKind::ServerHello, payload)?)
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EncodedTile {
    pub x: u16,
    pub y: u16,
    pub width: u16,
    pub height: u16,
    pub jpeg: Vec<u8>,
}

pub fn frame(
    sequence: u32,
    width: u16,
    height: u16,
    keyframe: bool,
    tile_size: u8,
    tiles: &[EncodedTile],
) -> Result<Envelope, MessageError> {
    if width == 0 || height == 0 || tile_size == 0 || tiles.len() > u16::MAX as usize {
        return Err(MessageError::Malformed("frame metadata is invalid"));
    }
    let capacity = 12usize.saturating_add(
        tiles
            .iter()
            .map(|tile| 14usize.saturating_add(tile.jpeg.len()))
            .sum(),
    );
    let mut payload = Vec::with_capacity(capacity);
    payload.extend_from_slice(&sequence.to_be_bytes());
    payload.extend_from_slice(&width.to_be_bytes());
    payload.extend_from_slice(&height.to_be_bytes());
    payload.push(u8::from(keyframe));
    payload.push(tile_size);
    payload.extend_from_slice(&(tiles.len() as u16).to_be_bytes());
    for tile in tiles {
        validate_tile(width, height, tile)?;
        payload.extend_from_slice(&tile.x.to_be_bytes());
        payload.extend_from_slice(&tile.y.to_be_bytes());
        payload.extend_from_slice(&tile.width.to_be_bytes());
        payload.extend_from_slice(&tile.height.to_be_bytes());
        payload.push(TILE_CODEC_JPEG);
        payload.push(0);
        payload.extend_from_slice(&(tile.jpeg.len() as u32).to_be_bytes());
        payload.extend_from_slice(&tile.jpeg);
    }
    Ok(Envelope::new(MessageKind::Frame, payload)?)
}

pub fn keyboard_report(envelope: &Envelope) -> Result<[u8; 8], MessageError> {
    if envelope.kind != MessageKind::Keyboard || envelope.payload.len() != 8 {
        return Err(MessageError::Malformed(
            "keyboard report must contain eight bytes",
        ));
    }
    Ok(envelope
        .payload
        .as_slice()
        .try_into()
        .expect("validated length"))
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MouseReport {
    pub buttons: u8,
    pub x: u16,
    pub y: u16,
    pub wheel: i8,
}

pub fn mouse_report(envelope: &Envelope) -> Result<MouseReport, MessageError> {
    if envelope.kind != MessageKind::Mouse || envelope.payload.len() != 6 {
        return Err(MessageError::Malformed(
            "mouse report must contain six bytes",
        ));
    }
    Ok(MouseReport {
        buttons: envelope.payload[0] & 0x07,
        x: u16::from_be_bytes([envelope.payload[1], envelope.payload[2]]),
        y: u16::from_be_bytes([envelope.payload[3], envelope.payload[4]]),
        wheel: envelope.payload[5] as i8,
    })
}

pub fn error(code: u16, message: &str) -> Result<Envelope, MessageError> {
    if message.is_empty() || message.len() > 1024 {
        return Err(MessageError::Malformed("error message length is invalid"));
    }
    let mut payload = Vec::with_capacity(2 + message.len());
    payload.extend_from_slice(&code.to_be_bytes());
    payload.extend_from_slice(message.as_bytes());
    Ok(Envelope::new(MessageKind::Error, payload)?)
}

fn validate_tile(width: u16, height: u16, tile: &EncodedTile) -> Result<(), MessageError> {
    if tile.width == 0
        || tile.height == 0
        || tile.jpeg.is_empty()
        || u32::from(tile.x) + u32::from(tile.width) > u32::from(width)
        || u32::from(tile.y) + u32::from(tile.height) > u32::from(height)
    {
        return Err(MessageError::Malformed("tile range is invalid"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_tile_outside_frame() {
        let tile = EncodedTile {
            x: 60,
            y: 0,
            width: 8,
            height: 8,
            jpeg: vec![1],
        };

        let error = frame(1, 64, 64, true, 64, &[tile]).unwrap_err();

        assert!(matches!(
            error,
            MessageError::Malformed("tile range is invalid")
        ));
    }

    #[test]
    fn round_trips_mouse_report() {
        let envelope =
            Envelope::new(MessageKind::Mouse, vec![3, 0x12, 0x34, 0x56, 0x78, 0xff]).unwrap();

        assert_eq!(
            mouse_report(&envelope).unwrap(),
            MouseReport {
                buttons: 3,
                x: 0x1234,
                y: 0x5678,
                wheel: -1,
            }
        );
    }
}
