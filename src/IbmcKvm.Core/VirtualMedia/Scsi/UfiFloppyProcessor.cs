using System.Buffers.Binary;

namespace IbmcKvm.Core.VirtualMedia.Scsi;

public sealed class UfiFloppyProcessor(IRandomAccessMedia media) : ScsiCommandProcessor(media)
{
    private static readonly byte[] InquiryData =
    [
        0, 0x80, 0, 1, 31, 0, 0, 0,
        (byte)'V', (byte)'i', (byte)'r', (byte)'t', (byte)'u', (byte)'a', (byte)'l', (byte)' ',
        (byte)'F', (byte)'L', (byte)'O', (byte)'P', (byte)'P', (byte)'Y', (byte)' ', (byte)'V',
        (byte)'M', (byte)' ', (byte)'1', (byte)'.', (byte)'1', (byte)'.', (byte)'0', (byte)' ',
        (byte)' ', (byte)' ', (byte)' ', (byte)' ',
    ];

    public override MediaDeviceKind DeviceKind => MediaDeviceKind.Floppy;

    public override int GetExpectedDataOutLength(ReadOnlySpan<byte> command)
    {
        ValidateCommand(command);
        return command[0] switch
        {
            0x04 or 0x55 => BinaryPrimitives.ReadUInt16BigEndian(command.Slice(7, 2)),
            0x2A or 0x2E or 0xAA => ExpectedBlockData(command),
            _ => 0,
        };
    }

    public override async ValueTask<ScsiResponse> ProcessAsync(
        ReadOnlyMemory<byte> command,
        ReadOnlyMemory<byte> dataOut = default,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command.Span);
        var bytes = command.ToArray();
        switch (bytes[0])
        {
            case 0x00:
                return TryReady(out var readyFailure) ? Succeed() : readyFailure;
            case 0x01:
            case 0x2B:
            case 0x2F:
                return Succeed();
            case 0x03:
                return RequestSense();
            case 0x04:
                return await FormatAsync(
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(7, 2)),
                    dataOut,
                    cancellationToken).ConfigureAwait(false);
            case 0x12:
                return Inquiry(bytes);
            case 0x1B:
                return (bytes[4] & 2) != 0 ? Fail(5, 0x24, 0) : Succeed();
            case 0x1D:
                return Succeed();
            case 0x1E:
                return (bytes[4] & 1) != 0 ? Fail(5, 0x24, 0) : Succeed();
            case 0x23:
                return ReadFormatCapacities();
            case 0x25:
                return ReadCapacity();
            case 0x28:
            case 0xA8:
                return await ReadBlocksAsync(ReadLba(bytes), ReadBlockCount(bytes), cancellationToken)
                    .ConfigureAwait(false);
            case 0x2A:
            case 0x2E:
            case 0xAA:
                return await WriteBlocksAsync(
                    ReadLba(bytes),
                    ReadBlockCount(bytes),
                    dataOut,
                    cancellationToken).ConfigureAwait(false);
            case 0x55:
                return dataOut.Length == GetExpectedDataOutLength(bytes) ? Succeed() : Fail(5, 0x1A, 0);
            case 0x5A:
                return ModeSense(bytes);
            default:
                return Fail(5, 0x24, 0);
        }
    }

    private ScsiResponse Inquiry(ReadOnlySpan<byte> command)
    {
        if ((command[1] & 0xE0) != 0)
        {
            return Fail(5, 0x25, 0);
        }

        return (command[1] & 1) != 0 ? Fail(5, 0x24, 0) : Succeed(InquiryData.ToArray());
    }

    private ScsiResponse ReadCapacity()
    {
        if (!TryReady(out var failure))
        {
            return failure;
        }

        var blocks = Media!.Length / Media.BlockSize;
        if (blocks is < 1 or > uint.MaxValue)
        {
            return Fail(5, 0x21, 0);
        }

        var result = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(result, checked((uint)(blocks - 1)));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), checked((uint)Media.BlockSize));
        return Succeed(result);
    }

    private ScsiResponse ReadFormatCapacities()
    {
        if (!TryReady(out var failure))
        {
            return failure;
        }

        var result = new byte[20]
        {
            0, 0, 0, 16,
            0, 0, 0, 0,
            2, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
        };
        var blocks = checked((uint)(Media!.Length / Media.BlockSize));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), blocks);
        result[9] = (byte)(Media.BlockSize >> 16);
        result[10] = (byte)(Media.BlockSize >> 8);
        result[11] = (byte)Media.BlockSize;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), blocks);
        result[17] = (byte)(Media.BlockSize >> 16);
        result[18] = (byte)(Media.BlockSize >> 8);
        result[19] = (byte)Media.BlockSize;
        return Succeed(result);
    }

    private ScsiResponse ModeSense(ReadOnlySpan<byte> command)
    {
        if (!TryReady(out var failure, reportUnitAttention: false))
        {
            return failure;
        }

        var pageCode = command[2] & 0x3F;
        var result = new List<byte>
        {
            0, 0, 0x94, Media!.IsReadOnly ? (byte)0x80 : (byte)0, 0, 0, 0, 0,
        };
        if (pageCode is 1 or 0x3F)
        {
            result.AddRange([1, 10, 0, 3, 0, 0, 0, 0, 3, 0, 0, 0]);
        }

        if (pageCode is 5 or 0x3F)
        {
            result.AddRange([5, 30, 3, 0xE8, 2, 18, 2, 0, 0, 80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 8, 30, 0, 0, 0, 0, 0, 0, 0, 2, 88, 0, 0]);
        }

        if (pageCode is 0x1B or 0x3F)
        {
            result.AddRange([0x1B, 10, 0x80, 1, 0, 0, 0, 0, 0, 0, 0, 0]);
        }

        if (pageCode is 0x1C or 0x3F)
        {
            result.AddRange([0x1C, 6, 0, 5, 0, 0, 0, 0]);
        }

        if (result.Count == 8 && pageCode != 0)
        {
            return Fail(5, 0x24, 0);
        }

        result[1] = (byte)(result.Count - 2);
        var allocationLength = BinaryPrimitives.ReadUInt16BigEndian(command.Slice(7, 2));
        return Succeed(result.Take(Math.Min(result.Count, allocationLength)).ToArray());
    }

    private async ValueTask<ScsiResponse> FormatAsync(
        int expected,
        ReadOnlyMemory<byte> dataOut,
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

        if (expected == 0 || dataOut.Length != expected)
        {
            return Fail(5, 0x1A, 0);
        }

        var zeroes = new byte[Math.Min(32 * 1024, checked((int)Media.Length))];
        for (long offset = 0; offset < Media.Length; offset += zeroes.Length)
        {
            var count = checked((int)Math.Min(zeroes.Length, Media.Length - offset));
            await Media.WriteAsync(offset, zeroes.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        await Media.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Succeed();
    }
}
