using System.Buffers.Binary;
using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Protocol.Tests.Wire;

public sealed class PacketReaderTests
{
    [Fact]
    public void ReassemblesPacketSplitAtEveryByteBoundary()
    {
        var frame = BuildIncoming(0x08, 0x01, 0x00);

        for (var split = 0; split <= frame.Length; split++)
        {
            var reader = new PacketReader();
            var first = reader.Append(frame.AsSpan(0, split));
            if (split < frame.Length)
            {
                Assert.Empty(first);
            }
            var packets = first.Concat(reader.Append(frame.AsSpan(split))).ToArray();
            var packet = Assert.Single(packets);
            Assert.Equal(0x08, packet.Command);
            Assert.Equal(new byte[] { 0x08, 0x01, 0x00 }, packet.Payload.ToArray());
            Assert.True(packet.IsCrcValid);
        }
    }

    [Fact]
    public void ReadsCoalescedPacketsAndSkipsNoise()
    {
        var first = BuildIncoming(0x04, 0x01, 0x02);
        var second = BuildIncoming(0x15, 0x01, 0x00, 0x00);
        var input = new byte[] { 0x00, 0xFE, 0x01 }.Concat(first).Concat(second).ToArray();

        var packets = new PacketReader().Append(input);

        Assert.Equal(new byte[] { 0x04, 0x15 }, packets.Select(static value => value.Command));
    }

    [Fact]
    public void PreservesPartialHeaderAcrossAppends()
    {
        var reader = new PacketReader();
        Assert.Empty(reader.Append(new byte[] { 0x66, 0xFE }));

        var packet = Assert.Single(reader.Append(BuildIncoming(0x01, 0x01, 0x01).AsSpan(1)));

        Assert.Equal(0x01, packet.Command);
    }

    [Fact]
    public void ResynchronizesAfterMalformedLengths()
    {
        var malformedShort = new byte[] { 0xFE, 0xF6, 0x00, 0x02, 0xAA, 0xBB };
        var malformedLarge = new byte[] { 0xFE, 0xF6, 0x00, 0xFB, 0xAA };
        var valid = BuildIncoming(0x08, 0x01, 0x01);

        var packets = new PacketReader().Append(malformedShort.Concat(malformedLarge).Concat(valid).ToArray());

        Assert.Equal(0x08, Assert.Single(packets).Command);
    }

    [Fact]
    public void ReportsCrcMismatchWithoutLosingPacketBoundary()
    {
        var frame = BuildIncoming(0x08, 0x01, 0x01);
        frame[^1] ^= 0xFF;

        var packet = Assert.Single(new PacketReader().Append(frame));

        Assert.False(packet.IsCrcValid);
    }

    private static byte[] BuildIncoming(params byte[] payload)
    {
        var length = payload.Length + 2;
        var result = new byte[length + 4];
        result[0] = 0xFE;
        result[1] = 0xF6;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), checked((ushort)length));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), Crc16High.Compute(payload));
        payload.CopyTo(result.AsSpan(6));
        return result;
    }
}
