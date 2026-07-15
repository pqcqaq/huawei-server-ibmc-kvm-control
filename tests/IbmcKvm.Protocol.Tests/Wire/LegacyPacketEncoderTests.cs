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

    [Fact]
    public void EncodesEncryptedConnectWithExtendedAuthenticator()
    {
        var authenticator = Convert.FromHexString(
            "0C829FAA0BD699D5A2A413E9BCA114061BE027FD07682FDC");

        var packet = LegacyPacketEncoder.EncodeExtendedAuthenticator(
            authenticator,
            Convert.FromHexString("0601030101"));

        Assert.Equal(
            Convert.FromHexString(
                "FEF68007" +
                "0C829FAA0BD699D5A2A413E9BCA114061BE027FD07682FDC" +
                "C171" +
                "0601030101"),
            packet);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(25)]
    public void RejectsInvalidExtendedAuthenticatorLength(int length)
    {
        Assert.Throws<ArgumentException>(() =>
            LegacyPacketEncoder.EncodeExtendedAuthenticator(new byte[length], [0x06]));
    }
}
