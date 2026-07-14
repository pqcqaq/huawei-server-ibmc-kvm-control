using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace IbmcKvm.Protocol.Wire;

public sealed record LegacyPacket(ushort Length, byte[] Body)
{
    public ushort ReceivedCrc => BinaryPrimitives.ReadUInt16BigEndian(Body.AsSpan(0, 2));
    public byte Command => Body[2];
    public ReadOnlyMemory<byte> Payload => Body.AsMemory(2);
    public bool IsCrcValid => Crc16High.Compute(Body.AsSpan(2)) == ReceivedCrc;
}

/// <summary>
/// Incremental parser for the stream framing used by KVMUtil.diviStreamNew.
/// It tolerates arbitrary fragmentation/coalescing and returns CRC+command+payload.
/// </summary>
public sealed class PacketReader
{
    private readonly List<byte> buffer = new();
    private readonly ushort maximumLength;

    public PacketReader(ushort maximumLength = 250)
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

            var length = BinaryPrimitives.ReadUInt16BigEndian(CollectionsMarshal.AsSpan(buffer).Slice(2, 2));
            if (length < 3 || length > maximumLength)
            {
                // A false sync can occur inside image data. Drop one byte and rescan.
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
            if (buffer[index] == LegacyPacketEncoder.HeaderFirst && buffer[index + 1] == LegacyPacketEncoder.HeaderSecond)
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
