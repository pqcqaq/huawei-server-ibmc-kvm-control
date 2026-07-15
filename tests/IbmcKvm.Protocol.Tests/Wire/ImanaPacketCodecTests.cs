using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Protocol.Tests.Wire;

public sealed class ImanaPacketCodecTests
{
    [Fact]
    public void EncodesUnencryptedFrameWithFourByteSessionId()
    {
        var packet = ImanaPacketEncoder.Encode(
            Convert.FromHexString("01020304"),
            encrypted: false,
            [0x06, 1, 3, 0]);

        Assert.Equal(
            Convert.FromHexString("FEF6000601020304" + ""),
            packet[..8]);
        Assert.Equal(new byte[] { 0x06, 1, 3, 0 }, packet[^4..]);
        Assert.Equal(Crc16High.Compute(packet.AsSpan(packet.Length - 4)),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2)));
    }

    [Fact]
    public void EncodesEncryptedFrameWithTwentyFourByteSessionId()
    {
        var sessionId = Enumerable.Range(0x10, 24).Select(static value => (byte)value).ToArray();
        var packet = ImanaPacketEncoder.Encode(sessionId, encrypted: true, [0x24, 0, 2, 0, 0]);

        Assert.Equal(new byte[] { 0xFE, 0xF6, 0x80, 0x07 }, packet[..4]);
        Assert.Equal(sessionId, packet[4..28]);
        Assert.Equal(new byte[] { 0x24, 0, 2, 0, 0 }, packet[^5..]);
    }

    [Fact]
    public void ParsesFragmentedIncomingFramesWithoutSessionId()
    {
        var encoded = BuildIncoming(0x08, 1, 0);
        var reader = new ImanaPacketReader();

        Assert.Empty(reader.Append(encoded.AsSpan(0, 5)));
        var packet = Assert.Single(reader.Append(encoded.AsSpan(5)));
        Assert.Equal(0x08, packet.Command);
        Assert.Equal(new byte[] { 0x08, 1, 0 }, packet.Payload.ToArray());
        Assert.True(packet.IsCrcValid);
    }

    [Theory]
    [InlineData(true, 4)]
    [InlineData(false, 24)]
    public void RejectsWrongSessionIdLength(bool encrypted, int length)
    {
        Assert.Throws<ArgumentException>(() => ImanaPacketEncoder.Encode(new byte[length], encrypted, [0x01]));
    }

    private static byte[] BuildIncoming(params byte[] payload)
    {
        var packet = new byte[6 + payload.Length];
        packet[0] = 0xFE;
        packet[1] = 0xF6;
        packet[3] = checked((byte)(payload.Length + 2));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(4, 2),
            Crc16High.Compute(payload));
        payload.CopyTo(packet.AsSpan(6));
        return packet;
    }
}
