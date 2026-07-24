using IbmcKvm.Core.Agent;
using SkiaSharp;

namespace IbmcKvm.Core.Tests.Agent;

public sealed class AgentFrameDecoderTests
{
    [Fact]
    public void CompositesJpegTileIntoBgraFrame()
    {
        var decoder = new AgentFrameDecoder();
        var jpeg = EncodeSolidJpeg(SKColors.Red, 2, 1);
        var frame = new AgentVideoFrame(
            1,
            2,
            1,
            true,
            64,
            [new AgentTile(0, 0, 2, 1, jpeg)]);

        var decoded = decoder.Decode(frame);

        Assert.Equal(8, decoded.BgraPixels.Length);
        Assert.InRange(decoded.BgraPixels[0], 0, 5);
        Assert.InRange(decoded.BgraPixels[1], 0, 5);
        Assert.InRange(decoded.BgraPixels[2], 245, 255);
        Assert.Equal(255, decoded.BgraPixels[3]);
    }

    [Fact]
    public void RejectsDifferenceFrameAfterSequenceGap()
    {
        var decoder = new AgentFrameDecoder();
        decoder.Decode(new AgentVideoFrame(
            4,
            1,
            1,
            true,
            64,
            [new AgentTile(0, 0, 1, 1, EncodeSolidJpeg(SKColors.Black, 1, 1))]));

        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AgentVideoFrame(
            6,
            1,
            1,
            false,
            64,
            [])));
    }

    [Fact]
    public void RejectsFrameWithExcessivePixelCountBeforeAllocation()
    {
        var decoder = new AgentFrameDecoder();

        Assert.Throws<InvalidDataException>(() => decoder.Decode(new AgentVideoFrame(
            1,
            8193,
            4096,
            true,
            64,
            [])));
    }

    private static byte[] EncodeSolidJpeg(SKColor color, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return encoded.ToArray();
    }
}
