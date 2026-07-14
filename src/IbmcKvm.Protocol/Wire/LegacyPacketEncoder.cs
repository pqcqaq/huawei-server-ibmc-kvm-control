using System.Buffers.Binary;

namespace IbmcKvm.Protocol.Wire;

public static class LegacyPacketEncoder
{
    public const byte HeaderFirst = 0xFE;
    public const byte HeaderSecond = 0xF6;
    public const int HeaderSize = 4;
    public const int TrailerSize = 2;

    public static byte[] Encode(int codeKey, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue - TrailerSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload exceeds the wire length field");
        }

        var length = payload.Length + TrailerSize;
        // The length field excludes the 4-byte code key and 2-byte CRC.
        var packet = new byte[HeaderSize + 4 + length];
        packet[0] = HeaderFirst;
        packet[1] = HeaderSecond;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), checked((ushort)length));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), codeKey);
        var crc = Crc16High.Compute(payload);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), crc);
        payload.CopyTo(packet.AsSpan(10));
        return packet;
    }
}
