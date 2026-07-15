using System.Runtime.InteropServices;
using System.Windows;

namespace IbmcKvm.App.Input;

internal static class CursorPositionService
{
    public static bool TryMoveTo(Point screenPoint) =>
        SetCursorPos(checked((int)Math.Round(screenPoint.X)), checked((int)Math.Round(screenPoint.Y)));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
