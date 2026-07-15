using System.Buffers.Binary;
using System.Text;
using IbmcKvm.Core.Recording;

namespace IbmcKvm.Core.Tests.Recording;

public sealed class MjpegAviWriterTests
{
    [Fact]
    public async Task WritesIndexedMotionJpegAvi()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new MjpegAviWriter(stream, 640, 480, frameRate: 20, leaveOpen: true))
        {
            await writer.WriteFrameAsync(Jpeg(1, 2));
            await writer.WriteFrameAsync(Jpeg(3, 4));
        }

        var bytes = stream.ToArray();
        Assert.Equal("RIFF", FourCc(bytes, 0));
        Assert.Equal("AVI ", FourCc(bytes, 8));
        Assert.Equal("MJPG", FourCc(bytes, 112));
        Assert.Equal(640, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(176, 4)));
        Assert.Equal(480, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(180, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(48, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(140, 4)));
        Assert.Equal(checked((uint)(bytes.Length - 8)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(216, 4)));

        const int firstChunk = 224;
        const int secondChunk = firstChunk + 14;
        const int indexChunk = secondChunk + 14;
        Assert.Equal("00dc", FourCc(bytes, firstChunk));
        Assert.Equal("00dc", FourCc(bytes, secondChunk));
        Assert.Equal("idx1", FourCc(bytes, indexChunk));
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(indexChunk + 4, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(indexChunk + 16, 4)));
        Assert.Equal(18u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(indexChunk + 32, 4)));
    }

    [Fact]
    public async Task EmptyRecordingStillProducesAValidIndexedContainer()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new MjpegAviWriter(stream, 320, 200, leaveOpen: true))
        {
        }

        var bytes = stream.ToArray();
        Assert.Equal("RIFF", FourCc(bytes, 0));
        Assert.Equal("idx1", FourCc(bytes, 224));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(228, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(48, 4)));
    }

    [Fact]
    public async Task RejectsIncompleteOrOversizedJpegData()
    {
        await using var stream = new MemoryStream();
        await using var writer = new MjpegAviWriter(stream, 320, 200, leaveOpen: true);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.WriteFrameAsync(new byte[] { 0xFF, 0xD8, 0, 0 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteFrameAsync(new byte[64 * 1024 * 1024 + 1]));
    }

    private static byte[] Jpeg(byte first, byte second) => [0xFF, 0xD8, first, second, 0xFF, 0xD9];

    private static string FourCc(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, 4);
}
