using System.Windows.Media;

namespace IbmcKvm.App.Input;

internal enum RemoteInputState
{
    Disconnected,
    ConnectionFailed,
    ConnectedInactive,
    Ready,
}

internal static class RemoteInputAvailability
{
    public static Color GetIndicatorColor(RemoteInputState state) => state switch
    {
        RemoteInputState.Disconnected => Color.FromRgb(119, 130, 125),
        RemoteInputState.ConnectionFailed => Color.FromRgb(198, 74, 64),
        RemoteInputState.ConnectedInactive => Color.FromRgb(57, 132, 216),
        RemoteInputState.Ready => Color.FromRgb(21, 155, 101),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static RemoteInputState Resolve(
        bool isConnected,
        bool connectionFailed,
        bool hasVideoFrame,
        bool isPointerInsideViewer,
        bool isViewerFocused,
        bool isWindowActive)
    {
        if (connectionFailed)
        {
            return RemoteInputState.ConnectionFailed;
        }

        if (!isConnected)
        {
            return RemoteInputState.Disconnected;
        }

        return hasVideoFrame && isPointerInsideViewer && isViewerFocused && isWindowActive
            ? RemoteInputState.Ready
            : RemoteInputState.ConnectedInactive;
    }
}
