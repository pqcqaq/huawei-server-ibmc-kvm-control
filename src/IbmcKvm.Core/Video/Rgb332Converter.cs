namespace IbmcKvm.Core.Video;

public static class Rgb332Converter
{
    public static byte[] ToBgra32(ReadOnlySpan<byte> pixels)
    {
        var output = new byte[checked(pixels.Length * 4)];
        for (var index = 0; index < pixels.Length; index++)
        {
            var value = pixels[index];
            var destination = index * 4;
            output[destination] = (byte)(((value >> 6) & 0x03) * 255 / 3);
            output[destination + 1] = (byte)(((value >> 3) & 0x07) * 255 / 7);
            output[destination + 2] = (byte)((value & 0x07) * 255 / 7);
            output[destination + 3] = 0xFF;
        }

        return output;
    }
}
