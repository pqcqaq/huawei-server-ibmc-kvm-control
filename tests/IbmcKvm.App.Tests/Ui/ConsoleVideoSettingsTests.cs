using IbmcKvm.App.Ui;

namespace IbmcKvm.App.Tests.Ui;

public sealed class ConsoleVideoSettingsTests
{
    [Fact]
    public void ExposesSourceVerifiedQualitySteps()
    {
        Assert.Equal(
            new byte[] { 40, 50, 60, 70, 80, 90 },
            ConsoleVideoSettings.QualityOptions.Select(static option => option.Value));
        Assert.Equal(
            3,
            ConsoleVideoSettings.FindIndex(ConsoleVideoSettings.QualityOptions, 70));
        Assert.Equal(
            -1,
            ConsoleVideoSettings.FindIndex(ConsoleVideoSettings.QualityOptions, 65));
    }

    [Fact]
    public void MapsColorDepthLabelsToProtocolWireValues()
    {
        Assert.Equal(
            new (byte Value, string Label)[]
            {
                (2, "8-bit"),
                (1, "7-bit"),
                (0, "6-bit"),
                (3, "4-bit"),
            },
            ConsoleVideoSettings.ColorDepthOptions.Select(static option => (option.Value, option.Label)));
    }
}
