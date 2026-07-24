use x11rb::connection::Connection as _;
use x11rb::protocol::xproto::{ConnectionExt as _, ImageFormat, ImageOrder};
use x11rb::rust_connection::RustConnection;

use crate::frame::RawFrame;
use crate::server::{BoxError, CaptureSource};

pub struct X11CaptureSource {
    connection: RustConnection,
    root: u32,
    bits_per_pixel: u8,
    scanline_pad: u8,
    image_order: ImageOrder,
    red_mask: u32,
    green_mask: u32,
    blue_mask: u32,
}

impl X11CaptureSource {
    pub fn connect(display: Option<&str>) -> Result<Self, X11Error> {
        let (connection, screen_number) = x11rb::connect(display)?;
        let setup = connection.setup();
        let screen = setup
            .roots
            .get(screen_number)
            .ok_or(X11Error::Unsupported("X11 screen number is invalid"))?;
        let visual = screen
            .allowed_depths
            .iter()
            .flat_map(|depth| depth.visuals.iter())
            .find(|visual| visual.visual_id == screen.root_visual)
            .ok_or(X11Error::Unsupported("X11 root visual is unavailable"))?;
        let format = setup
            .pixmap_formats
            .iter()
            .find(|format| format.depth == screen.root_depth)
            .ok_or(X11Error::Unsupported(
                "X11 root pixmap format is unavailable",
            ))?;
        if format.bits_per_pixel != 24 && format.bits_per_pixel != 32 {
            return Err(X11Error::Unsupported(
                "only 24-bit and 32-bit X11 root pixmaps are supported",
            ));
        }

        let result = Self {
            root: screen.root,
            bits_per_pixel: format.bits_per_pixel,
            scanline_pad: format.scanline_pad,
            image_order: setup.image_byte_order,
            red_mask: visual.red_mask,
            green_mask: visual.green_mask,
            blue_mask: visual.blue_mask,
            connection,
        };
        Ok(result)
    }

    fn capture_frame(&self) -> Result<RawFrame, X11Error> {
        let geometry = self.connection.get_geometry(self.root)?.reply()?;
        if geometry.width == 0 || geometry.height == 0 {
            return Err(X11Error::Unsupported(
                "X11 root window has no visible pixels",
            ));
        }
        let image = self
            .connection
            .get_image(
                ImageFormat::Z_PIXMAP,
                self.root,
                0,
                0,
                geometry.width,
                geometry.height,
                u32::MAX,
            )?
            .reply()?;
        let stride = scanline_stride(
            usize::from(geometry.width),
            self.bits_per_pixel,
            self.scanline_pad,
        )?;
        let required = stride
            .checked_mul(usize::from(geometry.height))
            .ok_or(X11Error::Unsupported("X11 image dimensions overflow"))?;
        if image.data.len() < required {
            return Err(X11Error::Unsupported("X11 image payload is truncated"));
        }

        let bytes_per_pixel = usize::from(self.bits_per_pixel / 8);
        let mut rgb =
            Vec::with_capacity(usize::from(geometry.width) * usize::from(geometry.height) * 3);
        for y in 0..usize::from(geometry.height) {
            for x in 0..usize::from(geometry.width) {
                let offset = y * stride + x * bytes_per_pixel;
                let pixel = read_pixel(
                    &image.data[offset..offset + bytes_per_pixel],
                    self.image_order,
                );
                rgb.push(scale_channel(pixel, self.red_mask));
                rgb.push(scale_channel(pixel, self.green_mask));
                rgb.push(scale_channel(pixel, self.blue_mask));
            }
        }
        Ok(RawFrame {
            width: geometry.width,
            height: geometry.height,
            rgb,
        })
    }
}

impl CaptureSource for X11CaptureSource {
    fn capture(&mut self) -> Result<RawFrame, BoxError> {
        Ok(self.capture_frame()?)
    }
}

fn scanline_stride(width: usize, bits_per_pixel: u8, scanline_pad: u8) -> Result<usize, X11Error> {
    if scanline_pad == 0 || !scanline_pad.is_multiple_of(8) {
        return Err(X11Error::Unsupported("X11 scanline padding is invalid"));
    }
    let bits = width
        .checked_mul(usize::from(bits_per_pixel))
        .ok_or(X11Error::Unsupported("X11 scanline width overflows"))?;
    let pad = usize::from(scanline_pad);
    Ok(bits.div_ceil(pad) * (pad / 8))
}

fn read_pixel(bytes: &[u8], order: ImageOrder) -> u32 {
    match order {
        ImageOrder::LSB_FIRST => bytes.iter().enumerate().fold(0u32, |value, (index, byte)| {
            value | (u32::from(*byte) << (index * 8))
        }),
        _ => bytes
            .iter()
            .fold(0u32, |value, byte| (value << 8) | u32::from(*byte)),
    }
}

fn scale_channel(pixel: u32, mask: u32) -> u8 {
    if mask == 0 {
        return 0;
    }
    let shift = mask.trailing_zeros();
    let maximum = mask >> shift;
    let value = (pixel & mask) >> shift;
    ((value * 255 + maximum / 2) / maximum) as u8
}

#[derive(Debug, thiserror::Error)]
pub enum X11Error {
    #[error("X11 connection failed: {0}")]
    Connect(#[from] x11rb::errors::ConnectError),
    #[error("X11 request failed: {0}")]
    Connection(#[from] x11rb::errors::ConnectionError),
    #[error("X11 reply failed: {0}")]
    Reply(#[from] x11rb::errors::ReplyError),
    #[error("X11 display is unsupported: {0}")]
    Unsupported(&'static str),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scales_masked_channels() {
        let pixel = 0x00_80_40_20;

        assert_eq!(128, scale_channel(pixel, 0x00_ff_00_00));
        assert_eq!(64, scale_channel(pixel, 0x00_00_ff_00));
        assert_eq!(32, scale_channel(pixel, 0x00_00_00_ff));
    }

    #[test]
    fn reads_little_endian_pixel() {
        assert_eq!(
            0x44332211,
            read_pixel(&[0x11, 0x22, 0x33, 0x44], ImageOrder::LSB_FIRST)
        );
    }
}
