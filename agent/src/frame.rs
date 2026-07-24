use jpeg_encoder::{ColorType, Encoder};

use crate::messages::EncodedTile;

pub const DEFAULT_TILE_SIZE: u16 = 64;
pub const MAX_FRAME_PIXELS: usize = 32 * 1024 * 1024;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RawFrame {
    pub width: u16,
    pub height: u16,
    pub rgb: Vec<u8>,
}

impl RawFrame {
    pub fn validate(&self) -> Result<(), FrameError> {
        if self.width == 0 || self.height == 0 {
            return Err(FrameError::InvalidDimensions);
        }
        let expected = usize::from(self.width)
            .checked_mul(usize::from(self.height))
            .and_then(|pixels| pixels.checked_mul(3))
            .ok_or(FrameError::InvalidDimensions)?;
        if usize::from(self.width) * usize::from(self.height) > MAX_FRAME_PIXELS {
            return Err(FrameError::InvalidDimensions);
        }
        if self.rgb.len() != expected {
            return Err(FrameError::InvalidPixelLength {
                expected,
                actual: self.rgb.len(),
            });
        }
        Ok(())
    }
}

pub struct TileEncoder {
    previous: Option<RawFrame>,
    quality: u8,
    tile_size: u16,
}

impl TileEncoder {
    pub fn new(quality: u8, tile_size: u16) -> Result<Self, FrameError> {
        if !(40..=95).contains(&quality) || tile_size == 0 || tile_size > u16::from(u8::MAX) {
            return Err(FrameError::InvalidSettings);
        }
        Ok(Self {
            previous: None,
            quality,
            tile_size,
        })
    }

    pub fn tile_size(&self) -> u8 {
        self.tile_size as u8
    }

    pub fn encode(
        &mut self,
        frame: RawFrame,
        force_keyframe: bool,
    ) -> Result<(bool, Vec<EncodedTile>), FrameError> {
        frame.validate()?;
        let keyframe = force_keyframe
            || self.previous.as_ref().is_none_or(|previous| {
                previous.width != frame.width || previous.height != frame.height
            });
        let mut tiles = Vec::new();
        for y in (0..frame.height).step_by(usize::from(self.tile_size)) {
            for x in (0..frame.width).step_by(usize::from(self.tile_size)) {
                let width = self.tile_size.min(frame.width - x);
                let height = self.tile_size.min(frame.height - y);
                let rgb = copy_tile(&frame, x, y, width, height);
                let changed = keyframe
                    || self
                        .previous
                        .as_ref()
                        .is_none_or(|previous| copy_tile(previous, x, y, width, height) != rgb);
                if !changed {
                    continue;
                }
                let mut jpeg = Vec::new();
                Encoder::new(&mut jpeg, self.quality).encode(
                    &rgb,
                    width,
                    height,
                    ColorType::Rgb,
                )?;
                tiles.push(EncodedTile {
                    x,
                    y,
                    width,
                    height,
                    jpeg,
                });
            }
        }
        self.previous = Some(frame);
        Ok((keyframe, tiles))
    }
}

fn copy_tile(frame: &RawFrame, x: u16, y: u16, width: u16, height: u16) -> Vec<u8> {
    let mut output = Vec::with_capacity(usize::from(width) * usize::from(height) * 3);
    let stride = usize::from(frame.width) * 3;
    let row_length = usize::from(width) * 3;
    for row in y..y + height {
        let start = usize::from(row) * stride + usize::from(x) * 3;
        output.extend_from_slice(&frame.rgb[start..start + row_length]);
    }
    output
}

#[derive(Debug, thiserror::Error)]
pub enum FrameError {
    #[error("frame dimensions are invalid")]
    InvalidDimensions,
    #[error("frame pixel length is invalid: expected {expected}, got {actual}")]
    InvalidPixelLength { expected: usize, actual: usize },
    #[error("tile encoder settings are invalid")]
    InvalidSettings,
    #[error("JPEG encoding failed: {0}")]
    Jpeg(#[from] jpeg_encoder::EncodingError),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn emits_only_changed_tiles_after_keyframe() {
        let mut encoder = TileEncoder::new(70, 2).unwrap();
        let first = RawFrame {
            width: 4,
            height: 2,
            rgb: vec![0; 4 * 2 * 3],
        };
        let (keyframe, tiles) = encoder.encode(first.clone(), false).unwrap();
        assert!(keyframe);
        assert_eq!(2, tiles.len());

        let (keyframe, tiles) = encoder.encode(first.clone(), false).unwrap();
        assert!(!keyframe);
        assert!(tiles.is_empty());

        let mut changed = first;
        changed.rgb[0] = 255;
        let (keyframe, tiles) = encoder.encode(changed, false).unwrap();
        assert!(!keyframe);
        assert_eq!(1, tiles.len());
        assert_eq!(
            (0, 0, 2, 2),
            (tiles[0].x, tiles[0].y, tiles[0].width, tiles[0].height)
        );
    }

    #[test]
    fn resolution_change_forces_keyframe() {
        let mut encoder = TileEncoder::new(70, 64).unwrap();
        encoder
            .encode(
                RawFrame {
                    width: 1,
                    height: 1,
                    rgb: vec![0; 3],
                },
                false,
            )
            .unwrap();

        let (keyframe, tiles) = encoder
            .encode(
                RawFrame {
                    width: 2,
                    height: 1,
                    rgb: vec![0; 6],
                },
                false,
            )
            .unwrap();

        assert!(keyframe);
        assert_eq!(1, tiles.len());
    }

    #[test]
    fn rejects_frame_above_pixel_limit_before_reading_pixels() {
        let frame = RawFrame {
            width: 8193,
            height: 4096,
            rgb: Vec::new(),
        };

        assert!(matches!(
            frame.validate(),
            Err(FrameError::InvalidDimensions)
        ));
    }
}
