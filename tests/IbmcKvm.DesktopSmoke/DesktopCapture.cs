using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IbmcKvm.DesktopSmoke;

internal sealed record WindowCaptureEvidence(
    string FileName,
    double DpiScaleX,
    double DpiScaleY,
    int PixelWidth,
    int PixelHeight);

internal static class DesktopCapture
{
    private const int SourceCopy = 0x00CC0020;

    public static WindowCaptureEvidence Save150Percent(Window window, string outputDirectory, string fileName)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.UpdateLayout();
        var handle = GetHandle(window);
        if (!GetWindowRect(handle, out var bounds))
        {
            throw new InvalidOperationException("Windows did not return the smoke window bounds.");
        }

        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var screenDc = GetDC(nint.Zero);
        if (screenDc == nint.Zero)
        {
            throw new InvalidOperationException("Windows did not provide the desktop device context.");
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (memoryDc == nint.Zero || bitmap == nint.Zero)
        {
            _ = ReleaseDC(nint.Zero, screenDc);
            if (memoryDc != nint.Zero)
            {
                _ = DeleteDC(memoryDc);
            }

            if (bitmap != nint.Zero)
            {
                _ = DeleteObject(bitmap);
            }

            throw new InvalidOperationException("Windows could not allocate the smoke screenshot surface.");
        }

        BitmapSource target;
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            if (!BitBlt(
                    memoryDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    bounds.Left,
                    bounds.Top,
                    SourceCopy))
            {
                throw new InvalidOperationException("Windows could not copy the smoke window pixels.");
            }

            target = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            target.Freeze();
        }
        finally
        {
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(nint.Zero, screenDc);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        var path = Path.Combine(outputDirectory, fileName);
        using (var output = File.Create(path))
        {
            encoder.Save(output);
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        return new WindowCaptureEvidence(fileName, dpi.DpiScaleX, dpi.DpiScaleY, width, height);
    }

    public static nint GetHandle(Window window) => new WindowInteropHelper(window).Handle;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint windowHandle, nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        int rasterOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
