using System.Buffers.Binary;

namespace IbmcKvm.Protocol.Wire;

public static class ImanaPacketEncoder
{
    public const int UnencryptedSessionIdLength = 4;
    public const int EncryptedSessionIdLength = 24;

    public static byte[] Encode(
        ReadOnlySpan<byte> sessionId,
        bool encrypted,
        ReadOnlySpan<byte> payload)
    {
        var expectedSessionIdLength = encrypted
            ? EncryptedSessionIdLength
            : UnencryptedSessionIdLength;
        if (sessionId.Length != expectedSessionIdLength)
        {
            throw new ArgumentException(
                $"An {(encrypted ? "encrypted" : "plain")} iMana frame requires {expectedSessionIdLength} session-id bytes.",
                nameof(sessionId));
        }

        if (payload.Length > 248)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "The iMana one-byte length field is limited to 250 bytes including CRC.");
        }

        var bodyLength = checked(payload.Length + 2);
        var packet = new byte[4 + sessionId.Length + bodyLength];
        packet[0] = LegacyPacketEncoder.HeaderFirst;
        packet[1] = LegacyPacketEncoder.HeaderSecond;
        packet[2] = encrypted ? (byte)0x80 : (byte)0;
        packet[3] = checked((byte)bodyLength);
        sessionId.CopyTo(packet.AsSpan(4));
        var crcOffset = 4 + sessionId.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(crcOffset, 2), Crc16High.Compute(payload));
        payload.CopyTo(packet.AsSpan(crcOffset + 2));
        return packet;
    }
}

public sealed class ImanaPacketReader
{
    private readonly List<byte> buffer = [];
    private readonly byte maximumLength;

    public ImanaPacketReader(byte maximumLength = 250)
    {
        if (maximumLength < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        this.maximumLength = maximumLength;
    }

    public IReadOnlyList<LegacyPacket> Append(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            buffer.Add(bytes[index]);
        }

        var packets = new List<LegacyPacket>();
        while (TryReadPacket(out var packet))
        {
            packets.Add(packet);
        }

        return packets;
    }

    public void Reset() => buffer.Clear();

    private bool TryReadPacket(out LegacyPacket packet)
    {
        packet = null!;
        while (true)
        {
            var headerIndex = FindHeader();
            if (headerIndex < 0)
            {
                KeepPossibleHeaderPrefix();
                return false;
            }

            if (headerIndex > 0)
            {
                buffer.RemoveRange(0, headerIndex);
            }

            if (buffer.Count < LegacyPacketEncoder.HeaderSize)
            {
                return false;
            }

            var length = buffer[3];
            if (length < 3 || length > maximumLength)
            {
                buffer.RemoveAt(0);
                continue;
            }

            var totalSize = LegacyPacketEncoder.HeaderSize + length;
            if (buffer.Count < totalSize)
            {
                return false;
            }

            var body = buffer.GetRange(LegacyPacketEncoder.HeaderSize, length).ToArray();
            buffer.RemoveRange(0, totalSize);
            packet = new LegacyPacket(length, body);
            return true;
        }
    }

    private int FindHeader()
    {
        for (var index = 0; index + 1 < buffer.Count; index++)
        {
            if (buffer[index] == LegacyPacketEncoder.HeaderFirst &&
                buffer[index + 1] == LegacyPacketEncoder.HeaderSecond)
            {
                return index;
            }
        }

        return -1;
    }

    private void KeepPossibleHeaderPrefix()
    {
        if (buffer.Count > 0 && buffer[^1] == LegacyPacketEncoder.HeaderFirst)
        {
            buffer.RemoveRange(0, buffer.Count - 1);
        }
        else
        {
            buffer.Clear();
        }
    }
}
