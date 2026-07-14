namespace IbmcKvm.Core.Video;

/// <summary>
/// Decodes the packed RGB332 run-length format used by the legacy iBMC stream.
/// The first source byte is a frame marker; records then alternate an 8-bit
/// color and a 4/6/10/18/22-bit run length.
/// </summary>
public static class LegacyRleDecoder
{
    public static byte[] Decode(ReadOnlySpan<byte> encoded, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var pixelCount = checked(width * height);
        if (encoded.Length < 2)
        {
            throw new InvalidDataException("The RLE frame is truncated.");
        }

        var output = new byte[pixelCount];
        var reader = new MsbBitReader(encoded, 8);
        var outputOffset = 0;
        while (outputOffset < output.Length)
        {
            var color = checked((byte)reader.Read(8));
            var shortLength = reader.Read(4);
            int runLength;
            if (shortLength != 0)
            {
                runLength = shortLength;
            }
            else
            {
                var lengthType = reader.Read(2);
                var lengthBits = lengthType switch
                {
                    0 => 6,
                    1 => 10,
                    2 => 18,
                    3 => 22,
                    _ => throw new InvalidDataException("The RLE run-length type is invalid."),
                };

                // Two low bits remain in the current nibble after the type.
                runLength = reader.Read(lengthBits);
            }

            if (runLength <= 0)
            {
                throw new InvalidDataException("The RLE frame contains a zero-length run.");
            }

            var writable = Math.Min(runLength, output.Length - outputOffset);
            output.AsSpan(outputOffset, writable).Fill(color);
            outputOffset += writable;
        }

        return output;
    }

    private ref struct MsbBitReader(ReadOnlySpan<byte> source, int bitOffset)
    {
        private readonly ReadOnlySpan<byte> source = source;
        private int bitOffset = bitOffset;

        public int Read(int bitCount)
        {
            if (bitCount is < 1 or > 24 || bitOffset + bitCount > source.Length * 8)
            {
                throw new InvalidDataException("The RLE frame ended inside a run.");
            }

            var value = 0;
            for (var index = 0; index < bitCount; index++)
            {
                var byteIndex = bitOffset >> 3;
                var shift = 7 - (bitOffset & 7);
                value = (value << 1) | ((source[byteIndex] >> shift) & 1);
                bitOffset++;
            }

            return value;
        }
    }
}
