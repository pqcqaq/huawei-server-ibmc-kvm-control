namespace IbmcKvm.Core.Input;

[Flags]
public enum HidModifiers : byte
{
    None = 0,
    LeftControl = 1 << 0,
    LeftShift = 1 << 1,
    LeftAlt = 1 << 2,
    LeftGui = 1 << 3,
    RightControl = 1 << 4,
    RightShift = 1 << 5,
    RightAlt = 1 << 6,
    RightGui = 1 << 7,
}

public sealed class HidKeyboardState
{
    private readonly SortedSet<byte> pressed = [];

    public HidModifiers Modifiers { get; private set; }

    public bool Press(byte usage)
    {
        ValidateUsage(usage);
        return pressed.Add(usage);
    }

    public bool Release(byte usage)
    {
        ValidateUsage(usage);
        return pressed.Remove(usage);
    }

    public bool SetModifier(HidModifiers modifier, bool pressed)
    {
        if (modifier == HidModifiers.None || !IsSingleBit((byte)modifier))
        {
            throw new ArgumentOutOfRangeException(nameof(modifier));
        }

        var previous = Modifiers;
        Modifiers = pressed ? Modifiers | modifier : Modifiers & ~modifier;
        return previous != Modifiers;
    }

    public byte[] CreateReport()
    {
        var report = new byte[8];
        report[0] = (byte)Modifiers;
        if (pressed.Count > 6)
        {
            report.AsSpan(2).Fill(0x01); // HID ErrorRollOver
            return report;
        }

        var index = 2;
        foreach (var usage in pressed)
        {
            report[index++] = usage;
        }

        return report;
    }

    public byte[] Clear()
    {
        pressed.Clear();
        Modifiers = HidModifiers.None;
        return CreateReport();
    }

    private static void ValidateUsage(byte usage)
    {
        if (usage is 0 or 1)
        {
            throw new ArgumentOutOfRangeException(nameof(usage));
        }
    }

    private static bool IsSingleBit(byte value) => (value & (value - 1)) == 0;
}
