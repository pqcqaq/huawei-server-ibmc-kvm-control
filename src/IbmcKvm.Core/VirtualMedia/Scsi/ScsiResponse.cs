namespace IbmcKvm.Core.VirtualMedia.Scsi;

public enum ScsiMediaAction
{
    None,
    Eject,
    Load,
}

public sealed record ScsiSense(byte Key, byte AdditionalCode, byte Qualifier, uint Information = 0)
{
    public static readonly ScsiSense None = new(0, 0, 0);

    public byte[] ToFixedFormat()
    {
        var result = new byte[18]
        {
            Information == 0 ? (byte)0x70 : (byte)0xF0,
            0,
            Key,
            0,
            0,
            0,
            0,
            10,
            0,
            0,
            0,
            0,
            AdditionalCode,
            Qualifier,
            0,
            0,
            0,
            0,
        };
        if (Information != 0)
        {
            result[3] = (byte)(Information >> 24);
            result[4] = (byte)(Information >> 16);
            result[5] = (byte)(Information >> 8);
            result[6] = (byte)Information;
        }

        return result;
    }
}

public sealed record ScsiResponse(
    bool Success,
    byte[] Data,
    ScsiSense Sense,
    ScsiMediaAction MediaAction = ScsiMediaAction.None);

public interface IScsiCommandProcessor
{
    MediaDeviceKind DeviceKind { get; }

    int GetExpectedDataOutLength(ReadOnlySpan<byte> command);

    ValueTask<ScsiResponse> ProcessAsync(
        ReadOnlyMemory<byte> command,
        ReadOnlyMemory<byte> dataOut = default,
        CancellationToken cancellationToken = default);
}
