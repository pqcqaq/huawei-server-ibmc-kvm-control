use std::collections::BTreeSet;

use x11rb::connection::Connection as _;
use x11rb::protocol::xproto::ConnectionExt as _;
use x11rb::protocol::xtest::ConnectionExt as _;
use x11rb::rust_connection::RustConnection;

use crate::messages::MouseReport;
use crate::server::{BoxError, InputSink};

const KEY_PRESS: u8 = 2;
const KEY_RELEASE: u8 = 3;
const BUTTON_PRESS: u8 = 4;
const BUTTON_RELEASE: u8 = 5;
const MOTION_NOTIFY: u8 = 6;

pub struct X11InputSink {
    connection: RustConnection,
    root: u32,
    keymap: Keymap,
    pressed_keys: BTreeSet<u8>,
    pressed_buttons: u8,
}

impl X11InputSink {
    pub fn connect(display: Option<&str>) -> Result<Self, X11InputError> {
        let (connection, screen_number) = x11rb::connect(display)?;
        let root = connection
            .setup()
            .roots
            .get(screen_number)
            .ok_or(X11InputError::Unsupported("X11 screen number is invalid"))?
            .root;
        let keymap = Keymap::load(&connection)?;
        Ok(Self {
            connection,
            root,
            keymap,
            pressed_keys: BTreeSet::new(),
            pressed_buttons: 0,
        })
    }

    fn update_keyboard(&mut self, report: [u8; 8]) -> Result<(), X11InputError> {
        let desired = self.keymap.resolve_report(report);
        for keycode in self
            .pressed_keys
            .difference(&desired)
            .copied()
            .collect::<Vec<_>>()
        {
            self.fake_input(KEY_RELEASE, keycode, 0, 0)?;
        }
        for keycode in desired
            .difference(&self.pressed_keys)
            .copied()
            .collect::<Vec<_>>()
        {
            self.fake_input(KEY_PRESS, keycode, 0, 0)?;
        }
        self.pressed_keys = desired;
        self.connection.flush()?;
        Ok(())
    }

    fn update_mouse(&mut self, report: MouseReport) -> Result<(), X11InputError> {
        let geometry = self.connection.get_geometry(self.root)?.reply()?;
        let x = scale_coordinate(report.x, geometry.width);
        let y = scale_coordinate(report.y, geometry.height);
        self.fake_input(MOTION_NOTIFY, 0, x, y)?;
        for (mask, button) in [(1u8, 1u8), (2, 3), (4, 2)] {
            let was_pressed = self.pressed_buttons & mask != 0;
            let is_pressed = report.buttons & mask != 0;
            if was_pressed != is_pressed {
                self.fake_input(
                    if is_pressed {
                        BUTTON_PRESS
                    } else {
                        BUTTON_RELEASE
                    },
                    button,
                    0,
                    0,
                )?;
            }
        }
        let wheel_button = if report.wheel > 0 { 4 } else { 5 };
        for _ in 0..report.wheel.unsigned_abs().min(10) {
            self.fake_input(BUTTON_PRESS, wheel_button, 0, 0)?;
            self.fake_input(BUTTON_RELEASE, wheel_button, 0, 0)?;
        }
        self.pressed_buttons = report.buttons;
        self.connection.flush()?;
        Ok(())
    }

    fn release(&mut self) -> Result<(), X11InputError> {
        for keycode in std::mem::take(&mut self.pressed_keys) {
            self.fake_input(KEY_RELEASE, keycode, 0, 0)?;
        }
        for (mask, button) in [(1u8, 1u8), (2, 3), (4, 2)] {
            if self.pressed_buttons & mask != 0 {
                self.fake_input(BUTTON_RELEASE, button, 0, 0)?;
            }
        }
        self.pressed_buttons = 0;
        self.connection.flush()?;
        Ok(())
    }

    fn fake_input(&self, event_type: u8, detail: u8, x: i16, y: i16) -> Result<(), X11InputError> {
        self.connection
            .xtest_fake_input(event_type, detail, 0, self.root, x, y, 0)?
            .check()?;
        Ok(())
    }
}

impl InputSink for X11InputSink {
    fn keyboard(&mut self, report: [u8; 8]) -> Result<(), BoxError> {
        Ok(self.update_keyboard(report)?)
    }

    fn mouse(&mut self, report: MouseReport) -> Result<(), BoxError> {
        Ok(self.update_mouse(report)?)
    }

    fn release_all(&mut self) -> Result<(), BoxError> {
        Ok(self.release()?)
    }
}

struct Keymap {
    minimum_keycode: u8,
    keysyms_per_keycode: usize,
    keysyms: Vec<u32>,
}

impl Keymap {
    fn load(connection: &RustConnection) -> Result<Self, X11InputError> {
        let setup = connection.setup();
        let count = setup.max_keycode - setup.min_keycode + 1;
        let reply = connection
            .get_keyboard_mapping(setup.min_keycode, count)?
            .reply()?;
        Ok(Self {
            minimum_keycode: setup.min_keycode,
            keysyms_per_keycode: usize::from(reply.keysyms_per_keycode),
            keysyms: reply.keysyms,
        })
    }

    fn resolve_report(&self, report: [u8; 8]) -> BTreeSet<u8> {
        let mut keycodes = BTreeSet::new();
        for bit in 0..8 {
            if report[0] & (1 << bit) != 0
                && let Some(keycode) = self.find_keysym(modifier_keysym(bit))
            {
                keycodes.insert(keycode);
            }
        }
        for usage in report[2..].iter().copied().filter(|usage| *usage != 0) {
            if let Some(keysym) = hid_keysym(usage)
                && let Some(keycode) = self.find_keysym(keysym)
            {
                keycodes.insert(keycode);
            }
        }
        keycodes
    }

    fn find_keysym(&self, keysym: u32) -> Option<u8> {
        self.keysyms
            .chunks(self.keysyms_per_keycode)
            .position(|keysyms| keysyms.contains(&keysym))
            .and_then(|index| self.minimum_keycode.checked_add(index as u8))
    }
}

fn modifier_keysym(bit: u8) -> u32 {
    match bit {
        0 => 0xffe3,
        1 => 0xffe1,
        2 => 0xffe9,
        3 => 0xffeb,
        4 => 0xffe4,
        5 => 0xffe2,
        6 => 0xffea,
        _ => 0xffec,
    }
}

fn hid_keysym(usage: u8) -> Option<u32> {
    match usage {
        0x04..=0x1d => Some(u32::from(b'a' + usage - 0x04)),
        0x1e..=0x26 => Some(u32::from(b'1' + usage - 0x1e)),
        0x27 => Some(u32::from(b'0')),
        0x28 => Some(0xff0d),
        0x29 => Some(0xff1b),
        0x2a => Some(0xff08),
        0x2b => Some(0xff09),
        0x2c => Some(0x20),
        0x2d => Some(b'-'.into()),
        0x2e => Some(b'='.into()),
        0x2f => Some(b'['.into()),
        0x30 => Some(b']'.into()),
        0x31 => Some(b'\\'.into()),
        0x33 => Some(b';'.into()),
        0x34 => Some(b'\''.into()),
        0x35 => Some(b'`'.into()),
        0x36 => Some(b','.into()),
        0x37 => Some(b'.'.into()),
        0x38 => Some(b'/'.into()),
        0x39 => Some(0xffe5),
        0x3a..=0x45 => Some(0xffbe + u32::from(usage - 0x3a)),
        0x49 => Some(0xff63),
        0x4a => Some(0xff50),
        0x4b => Some(0xff55),
        0x4c => Some(0xffff),
        0x4d => Some(0xff57),
        0x4e => Some(0xff56),
        0x4f => Some(0xff53),
        0x50 => Some(0xff51),
        0x51 => Some(0xff54),
        0x52 => Some(0xff52),
        0x53 => Some(0xff7f),
        _ => None,
    }
}

fn scale_coordinate(value: u16, extent: u16) -> i16 {
    if extent <= 1 {
        return 0;
    }
    (u32::from(value) * u32::from(extent - 1) / u32::from(u16::MAX)) as i16
}

#[derive(Debug, thiserror::Error)]
pub enum X11InputError {
    #[error("X11 connection failed: {0}")]
    Connect(#[from] x11rb::errors::ConnectError),
    #[error("X11 request failed: {0}")]
    Connection(#[from] x11rb::errors::ConnectionError),
    #[error("X11 reply failed: {0}")]
    Reply(#[from] x11rb::errors::ReplyError),
    #[error("X11 server rejected an input request: {0}")]
    ReplyOrId(#[from] x11rb::errors::ReplyOrIdError),
    #[error("X11 display is unsupported: {0}")]
    Unsupported(&'static str),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_hid_letters_and_navigation() {
        assert_eq!(Some(u32::from(b'a')), hid_keysym(0x04));
        assert_eq!(Some(0xff53), hid_keysym(0x4f));
        assert_eq!(None, hid_keysym(0));
    }

    #[test]
    fn scales_absolute_coordinates() {
        assert_eq!(0, scale_coordinate(0, 1920));
        assert_eq!(1919, scale_coordinate(u16::MAX, 1920));
    }
}
