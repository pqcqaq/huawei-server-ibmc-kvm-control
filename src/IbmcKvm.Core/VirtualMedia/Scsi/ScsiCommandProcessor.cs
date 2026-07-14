using System.Buffers.Binary;

namespace IbmcKvm.Core.VirtualMedia.Scsi;

public abstract class ScsiCommandProcessor(IRandomAccessMedia media) : IScsiCommandProcessor
{
    protected const int CommandLength = 12;
    protected const int MaximumTransferLength = 16 * 1024 * 1024;

    private ScsiSense sense = new(6, 0x29, 0);
    private bool mediaChanged = true;
    private bool ejected;

    protected IRandomAccessMedia? Media => ejected ? null : media;

    protected ScsiSense CurrentSense => sense;

    public abstract MediaDeviceKind DeviceKind { get; }

    public abstract int GetExpectedDataOutLength(ReadOnlySpan<byte> command);

    public abstract ValueTask<ScsiResponse> ProcessAsync(
        ReadOnlyMemory<byte> command,
        ReadOnlyMemory<byte> dataOut = default,
        CancellationToken cancellationToken = default);

    public void NotifyMediaChanged()
    {
        ejected = false;
        mediaChanged = true;
    }

    protected static void ValidateCommand(ReadOnlySpan<byte> command)
    {
        if (command.Length != CommandLength)
        {
            throw new InvalidDataException("A VMM USB command contains exactly 12 bytes.");
        }
    }

    protected bool TryReady(out ScsiResponse failure, bool reportUnitAttention = true)
    {
        if (Media is null)
        {
            failure = Fail(2, 0x3A, 0);
            return false;
        }

        if (mediaChanged && reportUnitAttention)
        {
            mediaChanged = false;
            failure = Fail(6, 0x28, 0);
            return false;
        }

        failure = null!;
        return true;
    }

    protected ScsiResponse Succeed(byte[]? data = null, ScsiMediaAction action = ScsiMediaAction.None)
    {
        sense = ScsiSense.None;
        return new ScsiResponse(true, data ?? [], sense, action);
    }

    protected ScsiResponse Fail(byte key, byte asc, byte ascq, uint information = 0)
    {
        sense = new ScsiSense(key, asc, ascq, information);
        return new ScsiResponse(false, [], sense);
    }

    protected ScsiResponse RequestSense() => new(true, sense.ToFixedFormat(), sense);

    protected ScsiResponse Eject()
    {
        ejected = true;
        return Succeed(action: ScsiMediaAction.Eject);
    }

    protected ScsiResponse Load()
    {
        ejected = false;
        mediaChanged = true;
        return Succeed(action: ScsiMediaAction.Load);
    }

    protected async ValueTask<ScsiResponse> ReadBlocksAsync(
        uint lba,
        uint blocks,
        CancellationToken cancellationToken)
    {
        if (!TryReady(out var failure))
        {
            return failure;
        }

        if (!TryGetRange(lba, blocks, out var offset, out var length))
        {
            return Fail(5, 0x21, 0);
        }

        if (length > MaximumTransferLength)
        {
            return Fail(5, 0x24, 0);
        }

        var result = new byte[length];
        try
        {
            await Media!.ReadAsync(offset, result, cancellationToken).ConfigureAwait(false);
            return Succeed(result);
        }
        catch (IOException)
        {
            return Fail(3, 0x10, 0, lba);
        }
    }

    protected async ValueTask<ScsiResponse> WriteBlocksAsync(
        uint lba,
        uint blocks,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        if (!TryReady(out var failure))
        {
            return failure;
        }

        if (Media!.IsReadOnly)
        {
            return Fail(7, 0x27, 0);
        }

        if (!TryGetRange(lba, blocks, out var offset, out var length))
        {
            return Fail(5, 0x21, 0);
        }

        if (length > MaximumTransferLength || data.Length != length)
        {
            return Fail(5, 0x24, 0);
        }

        try
        {
            await Media.WriteAsync(offset, data, cancellationToken).ConfigureAwait(false);
            await Media.FlushAsync(cancellationToken).ConfigureAwait(false);
            return Succeed();
        }
        catch (IOException)
        {
            return Fail(3, 0x10, 0, lba);
        }
    }

    protected bool TryGetRange(uint lba, uint blocks, out long offset, out int length)
    {
        var byteOffset = (ulong)lba * (uint)media.BlockSize;
        var byteLength = (ulong)blocks * (uint)media.BlockSize;
        if (byteOffset > long.MaxValue || byteLength > int.MaxValue ||
            byteOffset > (ulong)media.Length || byteLength > (ulong)media.Length - byteOffset)
        {
            offset = 0;
            length = 0;
            return false;
        }

        offset = (long)byteOffset;
        length = (int)byteLength;
        return true;
    }

    protected static uint ReadLba(ReadOnlySpan<byte> command) =>
        BinaryPrimitives.ReadUInt32BigEndian(command.Slice(2, 4));

    protected static uint ReadBlockCount(ReadOnlySpan<byte> command) =>
        command[0] is 0xA8 or 0xAA
            ? BinaryPrimitives.ReadUInt32BigEndian(command.Slice(6, 4))
            : BinaryPrimitives.ReadUInt16BigEndian(command.Slice(7, 2));

    protected int ExpectedBlockData(ReadOnlySpan<byte> command)
    {
        ValidateCommand(command);
        var bytes = (ulong)ReadBlockCount(command) * (uint)media.BlockSize;
        return bytes > MaximumTransferLength ? 0 : checked((int)bytes);
    }
}
