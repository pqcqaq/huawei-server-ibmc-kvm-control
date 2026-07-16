using IbmcKvm.Core.Input;

namespace IbmcKvm.Core.Tests.Input;

public sealed class HidKeyboardStateTests
{
    [Fact]
    public void BuildsStableBootProtocolReportAcrossPressAndRelease()
    {
        var state = new HidKeyboardState();
        state.SetModifier(HidModifiers.LeftControl, true);
        state.Press(0x06);
        state.Press(0x04);

        Assert.Equal(new byte[] { 1, 0, 4, 6, 0, 0, 0, 0 }, state.CreateReport());

        state.Release(4);
        state.SetModifier(HidModifiers.LeftControl, false);
        Assert.Equal(new byte[] { 0, 0, 6, 0, 0, 0, 0, 0 }, state.CreateReport());
    }

    [Fact]
    public void ReportsRolloverWhenMoreThanSixKeysAreHeld()
    {
        var state = new HidKeyboardState();
        for (byte usage = 4; usage <= 10; usage++)
        {
            state.Press(usage);
        }

        Assert.Equal(new byte[] { 0, 0, 1, 1, 1, 1, 1, 1 }, state.CreateReport());
    }

    [Fact]
    public void BuildsSourceCompatibleTransientKeyPressWithoutAccumulatingHeldCharacters()
    {
        var state = new HidKeyboardState();
        state.SetModifier(HidModifiers.LeftShift, true);
        state.Press(0x04);

        Assert.Equal(
            new byte[] { 2, 0, 5, 0, 0, 0, 0, 0 },
            state.CreateKeyPressReport(0x05));
        Assert.Equal(
            new byte[] { 2, 0, 4, 0, 0, 0, 0, 0 },
            state.CreateReport());
    }

    [Fact]
    public void ClearReleasesEveryKeyAndModifier()
    {
        var state = new HidKeyboardState();
        state.Press(4);
        state.SetModifier(HidModifiers.RightAlt, true);

        Assert.Equal(new byte[8], state.Clear());
    }
}
