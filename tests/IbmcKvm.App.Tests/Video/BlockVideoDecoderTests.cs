using IbmcKvm.Core.Video;

namespace IbmcKvm.App.Tests.Video;

public sealed class BlockVideoDecoderTests
{
    [Fact]
    public void DecodesSolidRleBlockToBgra32()
    {
        var decoder = new BlockVideoDecoder();
        var frame = Frame(64, 64, [0, 0, 100, 128, 128]);

        var pixels = decoder.Decode(frame);

        Assert.Equal(64 * 64 * 4, pixels.Length);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, pixels[^4..]);
    }

    [Fact]
    public void ComposesAReferencedLeftBlock()
    {
        var decoder = new BlockVideoDecoder();
        var frame = Frame(128, 64, [0, 0, 100, 128, 128, 0xC0]);

        var pixels = decoder.Decode(frame);

        Assert.Equal(new byte[] { 100, 100, 100, 255 }, PixelAt(pixels, 128, 0, 0));
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, PixelAt(pixels, 128, 64, 0));
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, PixelAt(pixels, 128, 127, 63));
    }

    [Fact]
    public void RetainsAnUnchangedBlockAcrossDifferenceFrames()
    {
        var decoder = new BlockVideoDecoder();
        _ = decoder.Decode(Frame(64, 64, [0, 0, 100, 128, 128]));

        var pixels = decoder.Decode(Frame(64, 64, [1, 0x80], difference: true));

        Assert.Equal(new byte[] { 100, 100, 100, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, pixels[^4..]);
    }

    [Fact]
    public void DecodesAHeaderlessBaselineJpegScan()
    {
        var decoder = new BlockVideoDecoder();
        var scan = CreateNeutralGray444Scan();
        var encoded = new byte[scan.Length + 4];
        encoded[1] = 0x40;
        encoded[2] = checked((byte)(scan.Length >> 8));
        encoded[3] = checked((byte)scan.Length);
        scan.CopyTo(encoded.AsSpan(4));

        var pixels = decoder.Decode(Frame(64, 64, encoded));

        foreach (var (x, y) in new[] { (0, 0), (31, 31), (63, 63) })
        {
            var pixel = PixelAt(pixels, 64, x, y);
            Assert.InRange(pixel[0], (byte)127, (byte)129);
            Assert.InRange(pixel[1], (byte)127, (byte)129);
            Assert.InRange(pixel[2], (byte)127, (byte)129);
            Assert.Equal(255, pixel[3]);
        }
    }

    private static EncodedVideoFrame Frame(int width, int height, byte[] encoded, bool difference = false) =>
        new(1, difference, width, height, 0, 0, 7, 6, encoded);

    private static byte[] PixelAt(byte[] pixels, int width, int x, int y) =>
        pixels.AsSpan((y * width + x) * 4, 4).ToArray();

    private static byte[] CreateNeutralGray444Scan()
    {
        var writer = new EntropyBitWriter();
        for (var mcu = 0; mcu < 64; mcu++)
        {
            writer.Write(0, 2);      // luminance DC category 0
            writer.Write(0b1010, 4); // luminance EOB
            writer.Write(0, 2);      // Cb DC category 0
            writer.Write(0, 2);      // Cb EOB
            writer.Write(0, 2);      // Cr DC category 0
            writer.Write(0, 2);      // Cr EOB
        }

        return writer.ToArray();
    }

    private sealed class EntropyBitWriter
    {
        private readonly List<byte> bytes = [];
        private int current;
        private int bitCount;

        public void Write(int value, int count)
        {
            for (var bit = count - 1; bit >= 0; bit--)
            {
                current = (current << 1) | ((value >> bit) & 1);
                bitCount++;
                if (bitCount == 8)
                {
                    FlushByte();
                }
            }
        }

        public byte[] ToArray()
        {
            if (bitCount != 0)
            {
                current = (current << (8 - bitCount)) | ((1 << (8 - bitCount)) - 1);
                FlushByte();
            }

            return bytes.ToArray();
        }

        private void FlushByte()
        {
            var value = checked((byte)current);
            bytes.Add(value);
            if (value == 0xFF)
            {
                bytes.Add(0);
            }

            current = 0;
            bitCount = 0;
        }
    }
}
