using IbmcKvm.App.Ui;
using IbmcKvm.Core.Input;

namespace IbmcKvm.App.Tests.Ui;

public sealed class KeyboardUiOptionsTests
{
    [Fact]
    public void OffersAllSupportedRemoteLayouts()
    {
        Assert.Equal(
            Enum.GetValues<RemoteKeyboardLayout>(),
            KeyboardUiOptions.Layouts.Select(static option => option.Layout));
    }

    [Fact]
    public void CustomEditorOptionsHaveUniqueHidUsages()
    {
        Assert.Equal(
            KeyboardUiOptions.CustomKeys.Count,
            KeyboardUiOptions.CustomKeys.Select(static option => option.Usage).Distinct().Count());
        Assert.Contains(KeyboardUiOptions.CustomKeys, static option => option.Usage == 0xE0);
        Assert.Contains(KeyboardUiOptions.CustomKeys, static option => option.Usage == 0x4C);
        Assert.Contains(KeyboardUiOptions.CustomKeys, static option => option.Usage == 0x89);
    }
}
