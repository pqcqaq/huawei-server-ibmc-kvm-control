using IbmcKvm.Core.Video;

namespace IbmcKvm.Core.Tests.Video;

public sealed class LegacyRleDecoderTests
{
    [Theory]
    [InlineData(2, 2, "00E340", "E3E3E3E3")]
    [InlineData(5, 4, "007F0140", "7F7F7F7F7F7F7F7F7F7F7F7F7F7F7F7F7F7F7F7F")]
    [InlineData(20, 15, "0022052C", null)]
    [InlineData(4, 2, "00E031F5", "E0E0E01F1F1F1F1F")]
    public void MatchesKnownRleVector(int width, int height, string encodedHex, string? expectedHex)
    {
        var result = LegacyRleDecoder.Decode(Convert.FromHexString(encodedHex), width, height);

        if (expectedHex is null)
        {
            Assert.All(result, static value => Assert.Equal(0x22, value));
        }
        else
        {
            Assert.Equal(Convert.FromHexString(expectedHex), result);
        }
    }

    [Fact]
    public void RejectsTruncatedRun()
    {
        Assert.Throws<InvalidDataException>(() => LegacyRleDecoder.Decode(new byte[] { 0, 0xE3 }, 2, 2));
    }

    [Fact]
    public void RejectsZeroLengthExtendedRun()
    {
        Assert.Throws<InvalidDataException>(() => LegacyRleDecoder.Decode(new byte[] { 0, 0xE3, 0, 0 }, 1, 1));
    }
}
