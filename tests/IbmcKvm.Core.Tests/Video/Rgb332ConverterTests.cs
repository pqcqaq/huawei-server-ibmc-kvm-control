using IbmcKvm.Core.Video;

namespace IbmcKvm.Core.Tests.Video;

public sealed class Rgb332ConverterTests
{
    [Fact]
    public void ExpandsLegacyBitLayoutToBgra32()
    {
        var result = Rgb332Converter.ToBgra32(new byte[] { 0x00, 0x07, 0x38, 0xC0, 0xFF });

        Assert.Equal(
            Convert.FromHexString("000000FF0000FFFF00FF00FFFF0000FFFFFFFFFF"),
            result);
    }
}
