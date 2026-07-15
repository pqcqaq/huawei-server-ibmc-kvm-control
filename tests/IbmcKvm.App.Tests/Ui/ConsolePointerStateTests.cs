using IbmcKvm.App.Ui;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.App.Tests.Ui;

public sealed class ConsolePointerStateTests
{
    [Fact]
    public void CapturedModeUsesRelativeProtocolAndHidesPointerOnlyWhileCaptured()
    {
        var state = new ConsolePointerState();
        state.SetMode(ConsolePointerMode.Captured);

        Assert.Equal(KvmMouseMode.Relative, state.ProtocolMode);
        Assert.True(state.BeginCapture());
        Assert.False(state.IsLocalPointerVisible);

        Assert.True(state.ReleaseCapture());
        Assert.True(state.IsLocalPointerVisible);
    }

    [Fact]
    public void ExplicitPointerVisibilityAppliesOutsideCapture()
    {
        var state = new ConsolePointerState();

        state.SetShowLocalPointer(false);

        Assert.False(state.IsLocalPointerVisible);
        state.SetShowLocalPointer(true);
        Assert.True(state.IsLocalPointerVisible);
    }

    [Fact]
    public void LeavingCapturedModeReleasesCaptureState()
    {
        var state = new ConsolePointerState();
        state.SetMode(ConsolePointerMode.Captured);
        state.BeginCapture();

        state.SetMode(ConsolePointerMode.Absolute);

        Assert.False(state.IsCaptureActive);
        Assert.Equal(KvmMouseMode.Absolute, state.ProtocolMode);
    }
}
