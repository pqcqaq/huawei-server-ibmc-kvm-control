using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Protocol.Tests.Wire;

public sealed class Crc16HighTests
{
    [Theory]
    [InlineData("", 0x0000)]
    [InlineData("0B", 0xB16B)]
    [InlineData("1401", 0xDF96)]
    [InlineData("01020304", 0x0D03)]
    [InlineData("313233343536373839", 0x31C3)]
    public void MatchesKnownChecksumVector(string hexadecimal, ushort expected)
    {
        Assert.Equal(expected, Crc16High.Compute(Convert.FromHexString(hexadecimal)));
    }
}
