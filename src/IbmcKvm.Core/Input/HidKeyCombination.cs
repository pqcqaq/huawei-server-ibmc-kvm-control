namespace IbmcKvm.Core.Input;

public sealed class HidKeyCombination
{
    private readonly byte[] usages;

    private HidKeyCombination(HidModifiers modifiers, byte[] usages)
    {
        Modifiers = modifiers;
        this.usages = usages;
    }

    public HidModifiers Modifiers { get; }

    public ReadOnlyMemory<byte> Usages => usages;

    public static HidKeyCombination CtrlShift { get; } = Create(0xE0, 0xE1);

    public static HidKeyCombination CtrlEscape { get; } = Create(0xE0, 0x29);

    public static HidKeyCombination CtrlAltDelete { get; } = Create(0xE0, 0xE2, 0x4C);

    public static HidKeyCombination AltTab { get; } = Create(0xE2, 0x2B);

    public static HidKeyCombination CtrlSpace { get; } = Create(0xE0, 0x2C);

    public static HidKeyCombination KeyboardReset { get; } = Create();

    public static HidKeyCombination Create(params byte[] hidUsages)
    {
        ArgumentNullException.ThrowIfNull(hidUsages);
        var requested = hidUsages.Where(static usage => usage != 0).ToArray();
        if (requested.Length > 6)
        {
            throw new ArgumentException("A custom key combination supports at most six HID usages.", nameof(hidUsages));
        }

        if (requested.Distinct().Count() != requested.Length)
        {
            throw new ArgumentException("A custom key combination cannot contain duplicate HID usages.", nameof(hidUsages));
        }

        var modifiers = HidModifiers.None;
        var keys = new List<byte>(requested.Length);
        foreach (var usage in requested)
        {
            if (usage is >= 0xE0 and <= 0xE7)
            {
                modifiers |= (HidModifiers)(1 << (usage - 0xE0));
                continue;
            }

            if (usage == 1)
            {
                throw new ArgumentOutOfRangeException(nameof(hidUsages), "HID ErrorRollOver is not a key usage.");
            }

            keys.Add(usage);
        }

        if (keys.Count > 6)
        {
            throw new ArgumentException("A boot-protocol report supports at most six non-modifier keys.", nameof(hidUsages));
        }

        return new HidKeyCombination(modifiers, [.. keys.Order()]);
    }

    public byte[] CreateReport()
    {
        var report = new byte[8];
        report[0] = (byte)Modifiers;
        usages.CopyTo(report, 2);
        return report;
    }
}
