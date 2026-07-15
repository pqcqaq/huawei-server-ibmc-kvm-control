using System.Buffers.Binary;

namespace IbmcKvm.Protocol.Wire;

public static class LegacyPacketEncoder
{
    public const byte HeaderFirst = 0xFE;
    public const byte HeaderSecond = 0xF6;
    public const int HeaderSize = 4;
    public const int TrailerSize = 2;
    public const int ExtendedAuthenticatorLength = 24;

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

    public static byte[] EncodeExtendedAuthenticator(
        ReadOnlySpan<byte> authenticator,
        ReadOnlySpan<byte> payload)
    {
        if (authenticator.Length != ExtendedAuthenticatorLength)
        {
            throw new ArgumentException(
                "An encrypted KVM connect authenticator contains exactly 24 bytes.",
                nameof(authenticator));
        }

        const int maximumPayloadLength = 0x7FFF - TrailerSize;
        if (payload.Length > maximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload exceeds the extended wire length field");
        }

        var length = checked((ushort)(0x8000 | (payload.Length + TrailerSize)));
        var packet = new byte[HeaderSize + ExtendedAuthenticatorLength + TrailerSize + payload.Length];
        packet[0] = HeaderFirst;
        packet[1] = HeaderSecond;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), length);
        authenticator.CopyTo(packet.AsSpan(HeaderSize, ExtendedAuthenticatorLength));
        var crcOffset = HeaderSize + ExtendedAuthenticatorLength;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(crcOffset, TrailerSize), Crc16High.Compute(payload));
        payload.CopyTo(packet.AsSpan(crcOffset + TrailerSize));
        return packet;
    }
}
