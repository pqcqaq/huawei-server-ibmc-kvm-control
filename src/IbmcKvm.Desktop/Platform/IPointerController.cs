namespace IbmcKvm.Desktop.Platform;

internal interface IPointerController : IDisposable
{
    bool IsSupported { get; }

    string? UnsupportedReason { get; }

    bool TryCapture(nint windowHandle);

    void Release();

    void Center(nint windowHandle, int x, int y);
}
