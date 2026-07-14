using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Protocol.Tests.Wire;

public sealed class LegacyPacketEncoderTests
{
    [Theory]
    [InlineData(0x00000003, "0B", "FEF6000300000003B16B0B")]
    [InlineData(0x00000005, "1401", "FEF6000400000005DF961401")]
    [InlineData(0x00000009, "01020304", "FEF60006000000090D0301020304")]
    public void MatchesLegacyJarOracle(int codeKey, string payloadHex, string packetHex)
    {
        var actual = LegacyPacketEncoder.Encode(codeKey, Convert.FromHexString(payloadHex));

        Assert.Equal(Convert.FromHexString(packetHex), actual);
    }

    [Fact]
    public void RejectsPayloadThatCannotFitLengthField()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LegacyPacketEncoder.Encode(0, new byte[ushort.MaxValue - 1]));
    }
}
