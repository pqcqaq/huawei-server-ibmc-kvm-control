using IbmcKvm.Protocol.Session;

namespace IbmcKvm.App.Ui;

internal enum ConsolePointerMode
{
    Absolute,
    Relative,
    Captured,
}

internal sealed class ConsolePointerState
{
    public ConsolePointerMode Mode { get; private set; } = ConsolePointerMode.Absolute;

    public bool ShowLocalPointer { get; private set; } = true;

    public bool IsCaptureActive { get; private set; }

    public bool IsLocalPointerVisible => ShowLocalPointer && !IsCaptureActive;

    public KvmMouseMode ProtocolMode => Mode == ConsolePointerMode.Absolute
        ? KvmMouseMode.Absolute
        : KvmMouseMode.Relative;

    public void SetMode(ConsolePointerMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Mode = mode;
        if (mode != ConsolePointerMode.Captured)
        {
            IsCaptureActive = false;
        }
    }

    public bool BeginCapture()
    {
        if (Mode != ConsolePointerMode.Captured || IsCaptureActive)
        {
            return false;
        }

        IsCaptureActive = true;
        return true;
    }

    public bool ReleaseCapture()
    {
        var changed = IsCaptureActive;
        IsCaptureActive = false;
        return changed;
    }

    public void SetShowLocalPointer(bool show) => ShowLocalPointer = show;
}
