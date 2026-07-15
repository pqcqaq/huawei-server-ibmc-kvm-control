using System.Globalization;
using IbmcKvm.Core.Input;

namespace IbmcKvm.App.Ui;

internal readonly record struct HidUsageOption(byte Usage, string Label);

internal readonly record struct KeyboardLayoutOption(RemoteKeyboardLayout Layout, string Label);

internal static class KeyboardUiOptions
{
    public static IReadOnlyList<KeyboardLayoutOption> Layouts { get; } =
    [
        new(RemoteKeyboardLayout.UnitedStates, "English (US)"),
        new(RemoteKeyboardLayout.Japanese, "日本語"),
        new(RemoteKeyboardLayout.French, "Français"),
    ];

    public static IReadOnlyList<HidUsageOption> CustomKeys { get; } = CreateCustomKeys();

    private static List<HidUsageOption> CreateCustomKeys()
    {
        var options = new List<HidUsageOption>
        {
            new(0, "未使用"),
            new(0xE0, "Left Ctrl"),
            new(0xE1, "Left Shift"),
            new(0xE2, "Left Alt"),
            new(0xE3, "Left Windows"),
            new(0xE4, "Right Ctrl"),
            new(0xE5, "Right Shift"),
            new(0xE6, "Right Alt"),
            new(0xE7, "Right Windows"),
        };

        for (byte usage = 0x04; usage <= 0x1D; usage++)
        {
            options.Add(new(usage, ((char)('A' + usage - 0x04)).ToString()));
        }

        for (byte usage = 0x1E; usage <= 0x26; usage++)
        {
            options.Add(new(usage, (usage - 0x1D).ToString(CultureInfo.InvariantCulture)));
        }

        options.Add(new(0x27, "0"));
        options.AddRange(
        [
            new(0x28, "Enter"),
            new(0x29, "Esc"),
            new(0x2A, "Backspace"),
            new(0x2B, "Tab"),
            new(0x2C, "Space"),
            new(0x39, "Caps Lock"),
            new(0x46, "Print Screen"),
            new(0x47, "Scroll Lock"),
            new(0x48, "Pause"),
            new(0x49, "Insert"),
            new(0x4A, "Home"),
            new(0x4B, "Page Up"),
            new(0x4C, "Delete"),
            new(0x4D, "End"),
            new(0x4E, "Page Down"),
            new(0x4F, "Right"),
            new(0x50, "Left"),
            new(0x51, "Down"),
            new(0x52, "Up"),
            new(0x53, "Num Lock"),
            new(0x65, "Menu"),
            new(0x87, "JIS Ro"),
            new(0x89, "JIS Yen"),
            new(0x8A, "JIS Convert"),
            new(0x8B, "JIS NonConvert"),
        ]);

        for (byte usage = 0x3A; usage <= 0x45; usage++)
        {
            options.Add(new(usage, $"F{usage - 0x39}"));
        }

        return options;
    }
}
