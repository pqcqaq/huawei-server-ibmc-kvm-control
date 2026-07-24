using Avalonia.Input;
using IbmcKvm.Core.Input;

namespace IbmcKvm.Desktop.Input;

internal static class AvaloniaHidKeyMap
{
    public static bool IsLockKey(PhysicalKey key) => key is
        PhysicalKey.CapsLock or PhysicalKey.NumLock or PhysicalKey.ScrollLock;

    public static bool IsLockKey(Key key) => key is Key.CapsLock or Key.NumLock or Key.Scroll;

    public static bool TryGetModifier(PhysicalKey key, out HidModifiers modifier)
    {
        modifier = key switch
        {
            PhysicalKey.ControlLeft => HidModifiers.LeftControl,
            PhysicalKey.ShiftLeft => HidModifiers.LeftShift,
            PhysicalKey.AltLeft => HidModifiers.LeftAlt,
            PhysicalKey.MetaLeft => HidModifiers.LeftGui,
            PhysicalKey.ControlRight => HidModifiers.RightControl,
            PhysicalKey.ShiftRight => HidModifiers.RightShift,
            PhysicalKey.AltRight => HidModifiers.RightAlt,
            PhysicalKey.MetaRight => HidModifiers.RightGui,
            _ => HidModifiers.None,
        };
        return modifier != HidModifiers.None;
    }

    public static bool TryGetModifier(Key key, out HidModifiers modifier)
    {
        modifier = key switch
        {
            Key.LeftCtrl => HidModifiers.LeftControl,
            Key.LeftShift => HidModifiers.LeftShift,
            Key.LeftAlt => HidModifiers.LeftAlt,
            Key.LWin => HidModifiers.LeftGui,
            Key.RightCtrl => HidModifiers.RightControl,
            Key.RightShift => HidModifiers.RightShift,
            Key.RightAlt => HidModifiers.RightAlt,
            Key.RWin => HidModifiers.RightGui,
            _ => HidModifiers.None,
        };
        return modifier != HidModifiers.None;
    }

    public static bool TryGetUsage(Key key, out byte usage)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            usage = checked((byte)(0x04 + key - Key.A));
            return true;
        }

        if (key is >= Key.D1 and <= Key.D9)
        {
            usage = checked((byte)(0x1E + key - Key.D1));
            return true;
        }

        if (key is >= Key.F1 and <= Key.F12)
        {
            usage = checked((byte)(0x3A + key - Key.F1));
            return true;
        }

        if (key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            usage = checked((byte)(0x59 + key - Key.NumPad1));
            return true;
        }

        usage = key switch
        {
            Key.D0 => 0x27,
            Key.Enter => 0x28,
            Key.Escape => 0x29,
            Key.Back => 0x2A,
            Key.Tab => 0x2B,
            Key.Space => 0x2C,
            Key.OemMinus => 0x2D,
            Key.OemPlus => 0x2E,
            Key.OemOpenBrackets => 0x2F,
            Key.OemCloseBrackets => 0x30,
            Key.OemPipe => 0x31,
            Key.OemSemicolon => 0x33,
            Key.OemQuotes => 0x34,
            Key.OemTilde => 0x35,
            Key.OemComma => 0x36,
            Key.OemPeriod => 0x37,
            Key.OemQuestion => 0x38,
            Key.CapsLock => 0x39,
            Key.PrintScreen => 0x46,
            Key.Scroll => 0x47,
            Key.Pause => 0x48,
            Key.Insert => 0x49,
            Key.Home => 0x4A,
            Key.PageUp => 0x4B,
            Key.Delete => 0x4C,
            Key.End => 0x4D,
            Key.PageDown => 0x4E,
            Key.Right => 0x4F,
            Key.Left => 0x50,
            Key.Down => 0x51,
            Key.Up => 0x52,
            Key.NumLock => 0x53,
            Key.Divide => 0x54,
            Key.Multiply => 0x55,
            Key.Subtract => 0x56,
            Key.Add => 0x57,
            Key.NumPad0 => 0x62,
            Key.Decimal => 0x63,
            Key.Apps => 0x65,
            _ => 0,
        };
        return usage != 0;
    }

    public static bool TryGetUsage(PhysicalKey key, out byte usage)
    {
        usage = key switch
        {
            PhysicalKey.A => 0x04,
            PhysicalKey.B => 0x05,
            PhysicalKey.C => 0x06,
            PhysicalKey.D => 0x07,
            PhysicalKey.E => 0x08,
            PhysicalKey.F => 0x09,
            PhysicalKey.G => 0x0A,
            PhysicalKey.H => 0x0B,
            PhysicalKey.I => 0x0C,
            PhysicalKey.J => 0x0D,
            PhysicalKey.K => 0x0E,
            PhysicalKey.L => 0x0F,
            PhysicalKey.M => 0x10,
            PhysicalKey.N => 0x11,
            PhysicalKey.O => 0x12,
            PhysicalKey.P => 0x13,
            PhysicalKey.Q => 0x14,
            PhysicalKey.R => 0x15,
            PhysicalKey.S => 0x16,
            PhysicalKey.T => 0x17,
            PhysicalKey.U => 0x18,
            PhysicalKey.V => 0x19,
            PhysicalKey.W => 0x1A,
            PhysicalKey.X => 0x1B,
            PhysicalKey.Y => 0x1C,
            PhysicalKey.Z => 0x1D,
            PhysicalKey.Digit1 => 0x1E,
            PhysicalKey.Digit2 => 0x1F,
            PhysicalKey.Digit3 => 0x20,
            PhysicalKey.Digit4 => 0x21,
            PhysicalKey.Digit5 => 0x22,
            PhysicalKey.Digit6 => 0x23,
            PhysicalKey.Digit7 => 0x24,
            PhysicalKey.Digit8 => 0x25,
            PhysicalKey.Digit9 => 0x26,
            PhysicalKey.Digit0 => 0x27,
            PhysicalKey.Enter or PhysicalKey.NumPadEnter => 0x28,
            PhysicalKey.Escape => 0x29,
            PhysicalKey.Backspace => 0x2A,
            PhysicalKey.Tab => 0x2B,
            PhysicalKey.Space => 0x2C,
            PhysicalKey.Minus => 0x2D,
            PhysicalKey.Equal => 0x2E,
            PhysicalKey.BracketLeft => 0x2F,
            PhysicalKey.BracketRight => 0x30,
            PhysicalKey.Backslash => 0x31,
            PhysicalKey.Semicolon => 0x33,
            PhysicalKey.Quote => 0x34,
            PhysicalKey.Backquote => 0x35,
            PhysicalKey.Comma => 0x36,
            PhysicalKey.Period => 0x37,
            PhysicalKey.Slash => 0x38,
            PhysicalKey.CapsLock => 0x39,
            PhysicalKey.F1 => 0x3A,
            PhysicalKey.F2 => 0x3B,
            PhysicalKey.F3 => 0x3C,
            PhysicalKey.F4 => 0x3D,
            PhysicalKey.F5 => 0x3E,
            PhysicalKey.F6 => 0x3F,
            PhysicalKey.F7 => 0x40,
            PhysicalKey.F8 => 0x41,
            PhysicalKey.F9 => 0x42,
            PhysicalKey.F10 => 0x43,
            PhysicalKey.F11 => 0x44,
            PhysicalKey.F12 => 0x45,
            PhysicalKey.PrintScreen => 0x46,
            PhysicalKey.ScrollLock => 0x47,
            PhysicalKey.Pause => 0x48,
            PhysicalKey.Insert => 0x49,
            PhysicalKey.Home => 0x4A,
            PhysicalKey.PageUp => 0x4B,
            PhysicalKey.Delete => 0x4C,
            PhysicalKey.End => 0x4D,
            PhysicalKey.PageDown => 0x4E,
            PhysicalKey.ArrowRight => 0x4F,
            PhysicalKey.ArrowLeft => 0x50,
            PhysicalKey.ArrowDown => 0x51,
            PhysicalKey.ArrowUp => 0x52,
            PhysicalKey.NumLock => 0x53,
            PhysicalKey.NumPadDivide => 0x54,
            PhysicalKey.NumPadMultiply => 0x55,
            PhysicalKey.NumPadSubtract => 0x56,
            PhysicalKey.NumPadAdd => 0x57,
            PhysicalKey.NumPad1 => 0x59,
            PhysicalKey.NumPad2 => 0x5A,
            PhysicalKey.NumPad3 => 0x5B,
            PhysicalKey.NumPad4 => 0x5C,
            PhysicalKey.NumPad5 => 0x5D,
            PhysicalKey.NumPad6 => 0x5E,
            PhysicalKey.NumPad7 => 0x5F,
            PhysicalKey.NumPad8 => 0x60,
            PhysicalKey.NumPad9 => 0x61,
            PhysicalKey.NumPad0 => 0x62,
            PhysicalKey.NumPadDecimal => 0x63,
            PhysicalKey.ContextMenu => 0x65,
            PhysicalKey.IntlBackslash => 0x64,
            PhysicalKey.IntlRo => 0x87,
            PhysicalKey.KanaMode => 0x88,
            PhysicalKey.IntlYen => 0x89,
            PhysicalKey.Convert => 0x8A,
            PhysicalKey.NonConvert => 0x8B,
            _ => 0,
        };
        return usage != 0;
    }
}
