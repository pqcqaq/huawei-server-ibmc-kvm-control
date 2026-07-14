using System.Buffers.Binary;

namespace IbmcKvm.Core.VirtualMedia.Scsi;

public sealed class SffOpticalProcessor(IRandomAccessMedia media) : ScsiCommandProcessor(media)
{
    private static readonly byte[] InquiryData =
    [
        5, 0x80, 0, 0x21, 31, 0, 0, 0,
        (byte)'V', (byte)'i', (byte)'r', (byte)'t', (byte)'u', (byte)'a', (byte)'l', (byte)' ',
        (byte)'D', (byte)'V', (byte)'D', (byte)'-', (byte)'R', (byte)'O', (byte)'M', (byte)' ',
        (byte)'V', (byte)'M', (byte)' ', (byte)'1', (byte)'.', (byte)'1', (byte)'.', (byte)'0',
        (byte)' ', (byte)'2', (byte)'2', (byte)'5',
    ];

    public override MediaDeviceKind DeviceKind => MediaDeviceKind.Optical;

    public override int GetExpectedDataOutLength(ReadOnlySpan<byte> command)
    {
        ValidateCommand(command);
        return command[0] == 0x55 ? BinaryPrimitives.ReadUInt16BigEndian(command.Slice(7, 2)) : 0;
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
                return TestUnitReady();
            case 0x03:
                return RequestSense();
            case 0x12:
                return Succeed(InquiryData.ToArray());
            case 0x1B:
                return StartStop(bytes);
            case 0x1E:
                return Succeed();
            case 0x28:
            case 0xA8:
                return await ReadBlocksAsync(ReadLba(bytes), ReadBlockCount(bytes), cancellationToken)
                    .ConfigureAwait(false);
            case 0x2B:
                return Succeed();
            case 0x25:
                return ReadCapacity();
            case 0x43:
                return ReadToc(bytes);
            case 0x4A:
                return TestUnitReady() with { Success = false };
            case 0x42:
            case 0x44:
            case 0xB9:
            case 0xBE:
                return Fail(5, 0x24, 0);
            case 0x55:
                return dataOut.Length == GetExpectedDataOutLength(bytes) ? Succeed() : Fail(5, 0x1A, 0);
            case 0x5A:
                return ModeSense(bytes);
            default:
                return Fail(5, 0x24, 0);
        }
    }

    private ScsiResponse TestUnitReady()
    {
        return TryReady(out var failure) ? Succeed() : failure;
    }

    private ScsiResponse StartStop(ReadOnlySpan<byte> command)
    {
        var eject = (command[4] & 2) != 0;
        var start = (command[4] & 1) != 0;
        if (!eject)
        {
            return start ? Fail(5, 0x24, 0) : Succeed();
        }

        return start ? Load() : Eject();
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

    private ScsiResponse ModeSense(ReadOnlySpan<byte> command)
    {
        if (!TryReady(out var failure, reportUnitAttention: false))
        {
            return failure;
        }

        var result = new byte[8]
        {
            0,
            6,
            Media is null ? (byte)1 : (byte)0x70,
            0,
            0,
            0,
            0,
            0,
        };
        var allocationLength = BinaryPrimitives.ReadUInt16BigEndian(command.Slice(7, 2));
        return Succeed(result[..Math.Min(result.Length, allocationLength)]);
    }

    private ScsiResponse ReadToc(ReadOnlySpan<byte> command)
    {
        if (!TryReady(out var failure))
        {
            return failure;
        }

        var isMsf = (command[1] & 2) != 0;
        var format = command[2] & 7;
        if (format == 0)
        {
            format = (command[9] >> 6) & 3;
        }

        var startTrack = command[6];
        var totalBlocks = checked((int)(Media!.Length / Media.BlockSize));
        var result = new List<byte>(64) { 0, 0, 1, 1 };
        switch (format)
        {
            case 0:
                if (startTrack > 1 && startTrack != 0xAA)
                {
                    return Fail(5, 0x24, 0);
                }

                if (startTrack <= 1)
                {
                    result.AddRange([0, 0x14, 1, 0, 0, 0, isMsf ? (byte)2 : (byte)0, 0]);
                }

                result.AddRange([0, 0x14, 0xAA, 0]);
                AddAddress(result, totalBlocks, isMsf);
                break;
            case 1:
                result.AddRange([0, 0, 0, 0, 0, 0, 0, 0]);
                break;
            case 2:
                for (var index = 0; index < 4; index++)
                {
                    result.Add(1);
                    result.Add(0x14);
                    result.Add(0);
                    result.Add(index < 3 ? (byte)(0xA0 + index) : (byte)1);
                    result.AddRange([0, 0, 0]);
                    if (index < 2)
                    {
                        result.AddRange([0, 1, 0, 0]);
                    }
                    else if (index == 2)
                    {
                        AddAddress(result, totalBlocks, isMsf);
                    }
                    else
                    {
                        result.AddRange([0, 0, 0, 0]);
                    }
                }

                break;
            default:
                return Fail(5, 0x24, 0);
        }

        result[0] = (byte)((result.Count - 2) >> 8);
        result[1] = (byte)(result.Count - 2);
        var allocationLength = BinaryPrimitives.ReadUInt16BigEndian(command.Slice(7, 2));
        return Succeed(result.Take(Math.Min(result.Count, allocationLength)).ToArray());
    }

    private static void AddAddress(List<byte> result, int totalBlocks, bool isMsf)
    {
        if (!isMsf)
        {
            result.AddRange([(byte)(totalBlocks >> 24), (byte)(totalBlocks >> 16), (byte)(totalBlocks >> 8), (byte)totalBlocks]);
            return;
        }

        var seconds = totalBlocks / 75 + 2;
        result.Add(0);
        result.Add((byte)(seconds / 60));
        result.Add((byte)(seconds % 60));
        result.Add((byte)(totalBlocks % 75));
    }
}
