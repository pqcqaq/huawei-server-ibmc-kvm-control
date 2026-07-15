namespace IbmcKvm.App.Ui;

using IbmcKvm.App.Localization;

internal readonly record struct ConsoleVideoSetting(byte Value, string Label);

internal static class ConsoleVideoSettings
{
    public static IReadOnlyList<ConsoleVideoSetting> QualityOptions =>
    [
        new(40, LocalizationManager.Translate("低 40")),
        new(50, LocalizationManager.Translate("低 50")),
        new(60, LocalizationManager.Translate("中 60")),
        new(70, LocalizationManager.Translate("中 70")),
        new(80, LocalizationManager.Translate("高 80")),
        new(90, LocalizationManager.Translate("高 90")),
    ];

    public static IReadOnlyList<ConsoleVideoSetting> ColorDepthOptions { get; } =
    [
        new(2, "8-bit"),
        new(1, "7-bit"),
        new(0, "6-bit"),
        new(3, "4-bit"),
    ];

    public static int FindIndex(IReadOnlyList<ConsoleVideoSetting> options, byte value)
    {
        for (var index = 0; index < options.Count; index++)
        {
            if (options[index].Value == value)
            {
                return index;
            }
        }

        return -1;
    }
}
