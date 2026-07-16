namespace IbmcKvm.Core.Input;

public enum RemoteKeyboardLayout
{
    UnitedStates,
    Japanese,
    French,
}

public static class WindowsVirtualKeyMap
{
    public static bool IsLockKey(int virtualKey) => virtualKey is 0x14 or 0x90 or 0x91;

    public static bool TryGetModifier(int virtualKey, out HidModifiers modifier)
    {
        modifier = virtualKey switch
        {
            0xA2 => HidModifiers.LeftControl,
            0xA0 => HidModifiers.LeftShift,
            0xA4 => HidModifiers.LeftAlt,
            0x5B => HidModifiers.LeftGui,
            0xA3 => HidModifiers.RightControl,
            0xA1 => HidModifiers.RightShift,
            0xA5 => HidModifiers.RightAlt,
            0x5C => HidModifiers.RightGui,
            _ => HidModifiers.None,
        };
        return modifier != HidModifiers.None;
    }

    public static bool TryGetUsage(int virtualKey, out byte usage) =>
        TryGetUsage(virtualKey, RemoteKeyboardLayout.UnitedStates, shifted: false, out usage);

    public static bool TryGetUsage(
        int virtualKey,
        RemoteKeyboardLayout layout,
        bool shifted,
        out byte usage)
    {
        if (!Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }

        if (TryGetLayoutUsage(virtualKey, layout, shifted, out usage))
        {
            return true;
        }

        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            usage = checked((byte)(0x04 + virtualKey - 0x41));
            return true;
        }

        if (virtualKey is >= 0x31 and <= 0x39)
        {
            usage = checked((byte)(0x1E + virtualKey - 0x31));
            return true;
        }

        if (virtualKey is >= 0x70 and <= 0x7B)
        {
            usage = checked((byte)(0x3A + virtualKey - 0x70));
            return true;
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            usage = virtualKey == 0x60 ? (byte)0x62 : checked((byte)(0x59 + virtualKey - 0x61));
            return true;
        }

        usage = virtualKey switch
        {
            0x30 => 0x27,
            0x0D => 0x28,
            0x1B => 0x29,
            0x08 => 0x2A,
            0x09 => 0x2B,
            0x20 => 0x2C,
            0xBD => 0x2D,
            0xBB => 0x2E,
            0xDB => 0x2F,
            0xDD => 0x30,
            0xDC => 0x31,
            0xBA => 0x33,
            0xDE => 0x34,
            0xC0 => 0x35,
            0xBC => 0x36,
            0xBE => 0x37,
            0xBF => 0x38,
            0x14 => 0x39,
            0x2C => 0x46,
            0x91 => 0x47,
            0x13 => 0x48,
            0x2D => 0x49,
            0x24 => 0x4A,
            0x21 => 0x4B,
            0x2E => 0x4C,
            0x23 => 0x4D,
            0x22 => 0x4E,
            0x27 => 0x4F,
            0x25 => 0x50,
            0x28 => 0x51,
            0x26 => 0x52,
            0x90 => 0x53,
            0x6F => 0x54,
            0x6A => 0x55,
            0x6D => 0x56,
            0x6B => 0x57,
            0x6E => 0x63,
            0x5D => 0x65,
            _ => 0,
        };
        return usage != 0;
    }

    private static bool TryGetLayoutUsage(
        int virtualKey,
        RemoteKeyboardLayout layout,
        bool shifted,
        out byte usage)
    {
        usage = layout switch
        {
            RemoteKeyboardLayout.Japanese => virtualKey switch
            {
                0xDB => 0x30,
                0xDD => 0x31,
                0xDC => shifted ? (byte)0x87 : (byte)0x89,
                0xF2 or 0xF3 => 0x35,
                0x1C => 0x8A,
                0x1D => 0x8B,
                _ => 0,
            },
            RemoteKeyboardLayout.French => virtualKey switch
            {
                0x41 => 0x14,
                0x51 => 0x04,
                0x5A => 0x1A,
                0x57 => 0x1D,
                0x4D => 0x33,
                0xBC => 0x10,
                0xBA => 0x36,
                0xBE => 0x37,
                0xBF => 0x38,
                _ => 0,
            },
            _ => 0,
        };
        return usage != 0;
    }
}
