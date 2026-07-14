using System.Buffers.Binary;

namespace IbmcKvm.Protocol.VirtualMedia;

public sealed class VmmPacketCodec
{
    public const int HeaderLength = 12;
    public const int DefaultMaximumPayloadLength = 1024 * 1024;

    private readonly int maximumPayloadLength;

    public VmmPacketCodec(int maximumPayloadLength = DefaultMaximumPayloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPayloadLength, 0);
        this.maximumPayloadLength = maximumPayloadLength;
    }

    public byte[] Encode(VmmPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(packet.Payload);
        ValidatePayloadLength(packet.Payload.Length);

        var result = new byte[HeaderLength + packet.Payload.Length];
        result[0] = (byte)packet.Type;
        result[1] = packet.Field1;
        result[2] = packet.Field2;
        result[3] = packet.CommandId;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), checked((uint)packet.Payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8, 4), packet.Metadata);
        packet.Payload.CopyTo(result.AsSpan(HeaderLength));
        return result;
    }

    public async ValueTask<VmmPacket> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        if (payloadLength > int.MaxValue)
        {
            throw new InvalidDataException("The VMM payload length exceeds the supported range.");
        }

        ValidatePayloadLength((int)payloadLength);
        var payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return new VmmPacket(
            (VmmPacketType)header[0],
            header[1],
            header[2],
            header[3],
            payload,
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)));
    }

    private void ValidatePayloadLength(int payloadLength)
    {
        if (payloadLength > maximumPayloadLength)
        {
            throw new InvalidDataException($"The VMM payload length {payloadLength} exceeds the configured limit.");
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The VMM stream ended in the middle of a frame.");
            }

            offset += count;
        }
    }
}
