using System.Runtime.InteropServices;

namespace IbmcKvm.Desktop.Platform;

internal sealed class X11PointerController : IPointerController
{
    private const int GrabModeAsync = 1;
    private const int GrabSuccess = 0;
    private const uint PointerMotionMask = 1u << 6;
    private const uint ButtonPressMask = 1u << 2;
    private const uint ButtonReleaseMask = 1u << 3;

    private nint display;
    private bool captured;

    public X11PointerController()
    {
        if (!OperatingSystem.IsLinux())
        {
            UnsupportedReason = "Pointer capture through X11 is only available on Linux.";
            return;
        }

        if (!string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "x11", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            UnsupportedReason = "This desktop session does not expose X11. Captured relative mouse mode requires Xorg.";
            return;
        }

        try
        {
            display = XOpenDisplay(0);
            if (display == 0)
            {
                UnsupportedReason = "The X11 display could not be opened.";
            }
        }
        catch (DllNotFoundException)
        {
            UnsupportedReason = "libX11.so.6 is not installed.";
        }
    }

    public bool IsSupported => display != 0;

    public string? UnsupportedReason { get; }

    public bool TryCapture(nint windowHandle)
    {
        if (display == 0 || windowHandle == 0)
        {
            return false;
        }

        var result = XGrabPointer(
            display,
            windowHandle,
            ownerEvents: true,
            PointerMotionMask | ButtonPressMask | ButtonReleaseMask,
            GrabModeAsync,
            GrabModeAsync,
            windowHandle,
            0,
            0);
        captured = result == GrabSuccess;
        _ = XFlush(display);
        return captured;
    }

    public void Release()
    {
        if (display == 0 || !captured)
        {
            return;
        }

        _ = XUngrabPointer(display, 0);
        _ = XFlush(display);
        captured = false;
    }

    public void Center(nint windowHandle, int x, int y)
    {
        if (display == 0 || windowHandle == 0 || x < 0 || y < 0)
        {
            return;
        }

        _ = XWarpPointer(display, 0, windowHandle, 0, 0, 0, 0, x, y);
        _ = XFlush(display);
    }

    public void Dispose()
    {
        Release();
        var current = Interlocked.Exchange(ref display, 0);
        if (current != 0)
        {
            _ = XCloseDisplay(current);
        }
    }

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(nint displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    private static extern int XGrabPointer(
        nint display,
        nint grabWindow,
        [MarshalAs(UnmanagedType.Bool)] bool ownerEvents,
        uint eventMask,
        int pointerMode,
        int keyboardMode,
        nint confineTo,
        nint cursor,
        ulong time);

    [DllImport("libX11.so.6")]
    private static extern int XUngrabPointer(nint display, ulong time);

    [DllImport("libX11.so.6")]
    private static extern int XWarpPointer(
        nint display,
        nint sourceWindow,
        nint destinationWindow,
        int sourceX,
        int sourceY,
        uint sourceWidth,
        uint sourceHeight,
        int destinationX,
        int destinationY);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(nint display);
}
