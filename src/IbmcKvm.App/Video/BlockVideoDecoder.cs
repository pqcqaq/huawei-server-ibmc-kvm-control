using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IbmcKvm.Core.Video;

namespace IbmcKvm.App;

/// <summary>
/// Decodes the 64x64 block stream used by newer Huawei iBMC firmware.
/// Blocks may contain a headerless JPEG scan, RLE pixels, an unchanged block,
/// or a reference to the block above/left.
/// </summary>
public sealed class BlockVideoDecoder
{
    private const int BlockSize = 64;
    private BlockState?[] blocks = [];
    private int width;
    private int height;
    private int blocksAcross;
    private int blocksDown;

    public byte[] Decode(EncodedVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnsureDimensions(frame.Width, frame.Height);

        var source = frame.EncodedData.AsSpan();
        var offset = 1; // byte zero is the legacy full/difference marker
        var blockNumber = 0;
        while (offset < source.Length)
        {
            if (blockNumber >= blocks.Length)
            {
                throw new InvalidDataException("The block stream contains more blocks than the frame dimensions.");
            }

            var descriptor = source[offset];
            var blockType = (descriptor & 0xE0) >> 5;
            var rleType = (descriptor & 0x1C) >> 2;
            var consumed = blockType switch
            {
                0 or 1 => DecodeRleBlock(source, offset, blockNumber, rleType),
                2 or 3 => DecodeJpegBlock(source, offset, blockNumber, frame.QuantizationTable),
                4 => KeepPreviousBlock(blockNumber),
                5 => CopyReferencedBlock(blockNumber, blockNumber - blocksAcross),
                6 => CopyReferencedBlock(blockNumber, blockNumber - 1),
                _ => throw new InvalidDataException($"Unsupported iBMC block type {blockType}."),
            };
            if (consumed < 1)
            {
                throw new InvalidDataException("The block decoder made no forward progress.");
            }

            offset = checked(offset + consumed);
            blockNumber++;
        }

        if (blockNumber != blocks.Length || blocks.Any(static block => block is null))
        {
            throw new InvalidDataException($"The frame supplied {blockNumber} of {blocks.Length} blocks.");
        }

        return ComposeFrame();
    }

    public void Reset()
    {
        blocks = [];
        width = 0;
        height = 0;
        blocksAcross = 0;
        blocksDown = 0;
    }

    private void EnsureDimensions(int frameWidth, int frameHeight)
    {
        if (frameWidth == width && frameHeight == height)
        {
            return;
        }

        width = frameWidth;
        height = frameHeight;
        blocksAcross = (width + BlockSize - 1) / BlockSize;
        blocksDown = (height + BlockSize - 1) / BlockSize;
        blocks = new BlockState[checked(blocksAcross * blocksDown)];
    }

    private int DecodeJpegBlock(ReadOnlySpan<byte> source, int offset, int blockNumber, int quantizationTable)
    {
        EnsureAvailable(source, offset, 3);
        var length = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(offset + 1, 2));
        EnsureAvailable(source, offset + 3, length);
        var header = JpegHeaderFactory.Create(quantizationTable);
        var jpeg = new byte[checked(header.Length + length + 2)];
        header.CopyTo(jpeg, 0);
        source.Slice(offset + 3, length).CopyTo(jpeg.AsSpan(header.Length));
        jpeg[^2] = 0xFF;
        jpeg[^1] = 0xD9;

        using var stream = new MemoryStream(jpeg, writable: false);
        var decoder = new JpegBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        if (converted.PixelWidth != BlockSize || converted.PixelHeight != BlockSize)
        {
            throw new InvalidDataException("An iBMC JPEG block is not 64x64 pixels.");
        }

        var pixels = new byte[BlockSize * BlockSize * 4];
        converted.CopyPixels(pixels, BlockSize * 4, 0);
        blocks[blockNumber] = new BlockState(pixels, -1, []);
        return 3 + length;
    }

    private int DecodeRleBlock(ReadOnlySpan<byte> source, int offset, int blockNumber, int encodedRleType)
    {
        if (encodedRleType is <= 3)
        {
            return DecodeRlePayload(source, offset, blockNumber, encodedRleType, inheritedColors: null);
        }

        var referenceNumber = encodedRleType is 4 or 5
            ? blockNumber - 1
            : blockNumber - blocksAcross;
        var reference = GetBlock(referenceNumber);
        if (reference.RleType == 0 && encodedRleType is 4 or 6)
        {
            blocks[blockNumber] = reference;
            return 1;
        }

        var colors = reference.Colors.ToArray();
        if (reference.RleType == 1 && encodedRleType is 5 or 7 && colors.Length >= 6)
        {
            (colors[0], colors[3]) = (colors[3], colors[0]);
            (colors[1], colors[4]) = (colors[4], colors[1]);
            (colors[2], colors[5]) = (colors[5], colors[2]);
        }

        return DecodeRlePayload(source, offset, blockNumber, reference.RleType, colors);
    }

    private int DecodeRlePayload(
        ReadOnlySpan<byte> source,
        int offset,
        int blockNumber,
        int rleType,
        byte[]? inheritedColors)
    {
        var coefficient = inheritedColors is null ? 1 : 0;
        byte[] colors;
        if (inheritedColors is not null)
        {
            colors = inheritedColors;
        }
        else
        {
            var colorCount = rleType switch
            {
                0 => 1,
                1 => 2,
                2 => 3,
                3 => 4,
                _ => throw new InvalidDataException($"Unsupported iBMC RLE type {rleType}."),
            };
            var colorOffset = rleType == 0 ? offset + 1 : offset + 3;
            EnsureAvailable(source, colorOffset, colorCount * 3);
            colors = source.Slice(colorOffset, colorCount * 3).ToArray();
        }

        var pixels = new byte[BlockSize * BlockSize * 4];
        int consumed;
        switch (rleType)
        {
            case 0:
                FillRun(pixels, 0, BlockSize * BlockSize, colors, 0);
                consumed = 4;
                break;
            case 1:
            {
                EnsureAvailable(source, offset, 3);
                var length = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(offset + 1, 2));
                var dataOffset = offset + 3 + 6 * coefficient;
                EnsureAvailable(source, dataOffset, length);
                DecodeTwoColorRle(source.Slice(dataOffset, length), colors, pixels);
                consumed = 3 + 6 * coefficient + length;
                break;
            }
            case 2:
            case 3:
            {
                EnsureAvailable(source, offset, 3);
                var length = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(offset + 1, 2));
                var colorCount = rleType == 2 ? 3 : 4;
                var dataOffset = offset + 3 + colorCount * 3 * coefficient;
                EnsureAvailable(source, dataOffset, length);
                DecodePaletteRle(source.Slice(dataOffset, length), colors, pixels);
                consumed = 3 + colorCount * 3 * coefficient + length;
                break;
            }
            default:
                throw new InvalidDataException($"Unsupported iBMC RLE type {rleType}.");
        }

        blocks[blockNumber] = new BlockState(pixels, rleType, colors);
        return consumed;
    }

    private static void DecodeTwoColorRle(ReadOnlySpan<byte> source, byte[] colors, byte[] output)
    {
        if (colors.Length < 6)
        {
            throw new InvalidDataException("A two-color RLE block lacks its palette.");
        }

        var reader = new MsbBitReader(source);
        var pixel = 0;
        var colorIndex = 0;
        while (pixel < BlockSize * BlockSize)
        {
            var length = reader.Read(6) + 1;
            var keepColor = length == 64 && reader.Read(1) != 0;
            var writable = Math.Min(length, BlockSize * BlockSize - pixel);
            FillRun(output, pixel, writable, colors, colorIndex);
            pixel += writable;
            if (!keepColor)
            {
                colorIndex ^= 1;
            }
        }
    }

    private static void DecodePaletteRle(ReadOnlySpan<byte> source, byte[] colors, byte[] output)
    {
        var pixel = 0;
        foreach (var value in source)
        {
            var length = (value >> 2) + 1;
            var colorIndex = value & 0x03;
            if (colorIndex * 3 + 2 >= colors.Length)
            {
                throw new InvalidDataException("An RLE block references a missing palette color.");
            }

            var writable = Math.Min(length, BlockSize * BlockSize - pixel);
            FillRun(output, pixel, writable, colors, colorIndex);
            pixel += writable;
            if (pixel == BlockSize * BlockSize)
            {
                return;
            }
        }

        throw new InvalidDataException("An RLE block did not fill 64x64 pixels.");
    }

    private static void FillRun(byte[] output, int pixelOffset, int count, byte[] colors, int colorIndex)
    {
        var colorOffset = colorIndex * 3;
        if (colorOffset + 2 >= colors.Length)
        {
            throw new InvalidDataException("An RLE palette is incomplete.");
        }

        var (red, green, blue) = ConvertYcbcr(colors[colorOffset], colors[colorOffset + 1], colors[colorOffset + 2]);
        for (var index = 0; index < count; index++)
        {
            var destination = (pixelOffset + index) * 4;
            output[destination] = blue;
            output[destination + 1] = green;
            output[destination + 2] = red;
            output[destination + 3] = 0xFF;
        }
    }

    private static (byte Red, byte Green, byte Blue) ConvertYcbcr(byte yValue, byte cbValue, byte crValue)
    {
        var y = yValue;
        var cb = cbValue - 128;
        var cr = crValue - 128;
        var red = (int)(y + 1.402 * cr);
        var green = (int)(y - 0.34414 * cb - 0.71414 * cr);
        var blue = (int)(y + 1.772 * cb);
        return ((byte)Math.Clamp(red, 0, 255), (byte)Math.Clamp(green, 0, 255), (byte)Math.Clamp(blue, 0, 255));
    }

    private int KeepPreviousBlock(int blockNumber)
    {
        _ = GetBlock(blockNumber);
        return 1;
    }

    private int CopyReferencedBlock(int blockNumber, int referenceNumber)
    {
        blocks[blockNumber] = GetBlock(referenceNumber);
        return 1;
    }

    private BlockState GetBlock(int blockNumber)
    {
        if (blockNumber < 0 || blockNumber >= blocks.Length || blocks[blockNumber] is null)
        {
            throw new InvalidDataException("A block references unavailable prior image data.");
        }

        return blocks[blockNumber]!;
    }

    private byte[] ComposeFrame()
    {
        var result = new byte[checked(width * height * 4)];
        for (var blockNumber = 0; blockNumber < blocks.Length; blockNumber++)
        {
            var block = blocks[blockNumber]!;
            var blockX = blockNumber % blocksAcross * BlockSize;
            var blockY = blockNumber / blocksAcross * BlockSize;
            var copyWidth = Math.Min(BlockSize, width - blockX);
            var copyHeight = Math.Min(BlockSize, height - blockY);
            for (var row = 0; row < copyHeight; row++)
            {
                Buffer.BlockCopy(
                    block.Pixels,
                    row * BlockSize * 4,
                    result,
                    ((blockY + row) * width + blockX) * 4,
                    copyWidth * 4);
            }
        }

        return result;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> source, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > source.Length - length)
        {
            throw new InvalidDataException("The iBMC block stream is truncated.");
        }
    }

    private sealed record BlockState(byte[] Pixels, int RleType, byte[] Colors);

    private ref struct MsbBitReader(ReadOnlySpan<byte> source)
    {
        private readonly ReadOnlySpan<byte> source = source;
        private int bitOffset;

        public int Read(int count)
        {
            if (bitOffset + count > source.Length * 8)
            {
                throw new InvalidDataException("The two-color RLE bit stream is truncated.");
            }

            var result = 0;
            for (var index = 0; index < count; index++)
            {
                result = (result << 1) | ((source[bitOffset >> 3] >> (7 - (bitOffset & 7))) & 1);
                bitOffset++;
            }

            return result;
        }
    }
}

internal static class JpegHeaderFactory
{
    // Huawei uses standard baseline JPEG Huffman tables and swaps only the
    // three quantization tables. This is the baseline quality-50 header.
    private const string Quality50Header =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/2wBDAhESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCABAAEADAREAAhEBAxEC/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/90ABABk/9oADAMBAAIRAxEAPwA=";

    private static readonly byte[] LuminanceBase =
    [
        16, 11, 12, 14, 12, 10, 16, 14, 13, 14, 18, 17, 16, 19, 24, 40,
        26, 24, 22, 22, 24, 49, 35, 37, 29, 40, 58, 51, 61, 60, 57, 51,
        56, 55, 64, 72, 92, 78, 64, 68, 87, 69, 55, 56, 80, 109, 81, 87,
        95, 98, 103, 104, 103, 62, 77, 113, 121, 112, 100, 120, 92, 101, 103, 99,
    ];

    private static readonly byte[] ChrominanceBase =
    [
        17, 18, 18, 24, 21, 24, 47, 26, 26, 47, 99, 66, 56, 66, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    // The legacy client ships this one table in natural (rather than zig-zag)
    // order. Preserve that byte-for-byte quirk for table index 5.
    private static readonly byte[] LuminanceQuality60 =
    [
        13, 9, 8, 13, 19, 32, 41, 49, 10, 10, 11, 15, 21, 46, 48, 44,
        11, 10, 13, 19, 32, 46, 55, 45, 11, 14, 18, 23, 41, 70, 64, 50,
        14, 18, 30, 45, 54, 87, 82, 62, 19, 28, 44, 51, 65, 83, 90, 74,
        39, 51, 62, 70, 82, 97, 96, 81, 58, 74, 76, 78, 90, 80, 82, 79,
    ];

    public static byte[] Create(int tableIndex)
    {
        tableIndex = Math.Clamp(tableIndex, 0, 9);
        var quality = (tableIndex + 1) * 10;
        var scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var result = Convert.FromBase64String(Quality50Header);
        var markerOffset = 0;
        for (var table = 0; table < 3; table++)
        {
            markerOffset = FindMarker(result, 0xDB, markerOffset);
            var values = table == 0 ? LuminanceBase : ChrominanceBase;
            for (var index = 0; index < 64; index++)
            {
                var value = table == 0 && tableIndex == 5
                    ? LuminanceQuality60[index]
                    : Math.Clamp((values[index] * scale + 50) / 100, 1, 255);
                result[markerOffset + 5 + index] = checked((byte)value);
            }

            markerOffset += 69;
        }

        return result;
    }

    private static int FindMarker(byte[] data, byte marker, int start)
    {
        for (var index = start; index + 1 < data.Length; index++)
        {
            if (data[index] == 0xFF && data[index + 1] == marker)
            {
                return index;
            }
        }

        throw new InvalidDataException($"JPEG marker FF{marker:X2} is missing from the synthetic header.");
    }
}
