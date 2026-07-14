using IbmcKvm.App.Input;

namespace IbmcKvm.App.Tests.Input;

public sealed class RemoteInputAvailabilityTests
{
    [Theory]
    [InlineData((int)RemoteInputState.Disconnected, 119, 130, 125)]
    [InlineData((int)RemoteInputState.ConnectionFailed, 198, 74, 64)]
    [InlineData((int)RemoteInputState.ConnectedInactive, 57, 132, 216)]
    [InlineData((int)RemoteInputState.Ready, 21, 155, 101)]
    public void MapsEveryStateToTheSpecifiedIndicatorColor(
        int stateValue,
        byte red,
        byte green,
        byte blue)
    {
        var state = (RemoteInputState)stateValue;
        var color = RemoteInputAvailability.GetIndicatorColor(state);

        Assert.Equal(red, color.R);
        Assert.Equal(green, color.G);
        Assert.Equal(blue, color.B);
    }

    [Fact]
    public void FailureTakesPrecedenceOverOtherSignals()
    {
        var state = RemoteInputAvailability.Resolve(
            isConnected: true,
            connectionFailed: true,
            hasVideoFrame: true,
            isPointerInsideViewer: true,
            isViewerFocused: true,
            isWindowActive: true);

        Assert.Equal(RemoteInputState.ConnectionFailed, state);
    }

    [Fact]
    public void DisconnectedSessionIsNotReady()
    {
        var state = RemoteInputAvailability.Resolve(
            isConnected: false,
            connectionFailed: false,
            hasVideoFrame: true,
            isPointerInsideViewer: true,
            isViewerFocused: true,
            isWindowActive: true);

        Assert.Equal(RemoteInputState.Disconnected, state);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void ConnectedSessionStaysInactiveUntilEveryInputConditionIsMet(
        bool hasVideoFrame,
        bool isPointerInsideViewer,
        bool isViewerFocused,
        bool isWindowActive)
    {
        var state = RemoteInputAvailability.Resolve(
            isConnected: true,
            connectionFailed: false,
            hasVideoFrame,
            isPointerInsideViewer,
            isViewerFocused,
            isWindowActive);

        Assert.Equal(RemoteInputState.ConnectedInactive, state);
    }

    [Fact]
    public void ConnectedFocusedViewerUnderThePointerIsReady()
    {
        var state = RemoteInputAvailability.Resolve(
            isConnected: true,
            connectionFailed: false,
            hasVideoFrame: true,
            isPointerInsideViewer: true,
            isViewerFocused: true,
            isWindowActive: true);

        Assert.Equal(RemoteInputState.Ready, state);
    }
}
