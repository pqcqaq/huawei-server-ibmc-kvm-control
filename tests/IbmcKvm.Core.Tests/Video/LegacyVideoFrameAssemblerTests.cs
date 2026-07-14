using System.Buffers.Binary;
using IbmcKvm.Core.Video;

namespace IbmcKvm.Core.Tests.Video;

public sealed class LegacyVideoFrameAssemblerTests
{
    [Fact]
    public void CompletesFrameAndExtractsMetadata()
    {
        var assembler = new LegacyVideoFrameAssembler();
        var first = FirstChunk(frame: 9, expectedLength: 5, difference: true);
        Assert.False(assembler.TryAddChunk(first, out _));

        Assert.True(assembler.TryAddChunk(Chunk(1, 9, 1, 2, 3, 4, 5), out var frame));
        Assert.NotNull(frame);
        Assert.Equal(9, frame.FrameNumber);
        Assert.True(frame.IsDifference);
        Assert.Equal(1280, frame.Width);
        Assert.Equal(720, frame.Height);
        Assert.Equal(120, frame.RemoteCursorX);
        Assert.Equal(240, frame.RemoteCursorY);
        Assert.Equal(3, frame.ColorDepth);
        Assert.Equal(2, frame.QuantizationTable);
        Assert.Equal(new byte[] { 1, 1, 2, 3, 4, 5 }, frame.EncodedData);
    }

    [Fact]
    public void ReordersChunksReceivedBeforeFirstChunk()
    {
        var assembler = new LegacyVideoFrameAssembler();
        var second = Chunk(index: 1, frame: 4, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE);
        Assert.False(assembler.TryAddChunk(second, out _));

        var first = FirstChunk(frame: 4, expectedLength: 5, difference: false);
        Assert.True(assembler.TryAddChunk(first, out var frame));

        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }, frame!.EncodedData[^5..]);
    }

    [Fact]
    public void RejectsConflictingDuplicateChunk()
    {
        var assembler = new LegacyVideoFrameAssembler();
        Assert.False(assembler.TryAddChunk(Chunk(1, 7, 1), out _));

        Assert.Throws<InvalidDataException>(() => assembler.TryAddChunk(Chunk(1, 7, 2), out _));
    }

    [Fact]
    public void RejectsAdvertisedFrameAboveConfiguredBound()
    {
        var assembler = new LegacyVideoFrameAssembler(13);

        Assert.Throws<InvalidDataException>(() => assembler.TryAddChunk(
            FirstChunk(frame: 1, expectedLength: 14, difference: false), out _));
    }

    private static byte[] FirstChunk(byte frame, int expectedLength, bool difference)
    {
        var chunk = new byte[17];
        chunk[2] = frame;
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(3, 4), checked((uint)expectedLength));
        chunk[7] = (byte)((difference ? 0x80 : 0) | (1280 >> 8));
        chunk[8] = (byte)(1280 & 0xFF);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(9, 2), 720);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(12, 2), 120);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(14, 2), 240);
        chunk[16] = 3;
        return chunk;
    }

    private static byte[] Chunk(ushort index, byte frame, params byte[] data)
    {
        var chunk = new byte[data.Length + 3];
        BinaryPrimitives.WriteUInt16BigEndian(chunk, index);
        chunk[2] = frame;
        data.CopyTo(chunk.AsSpan(3));
        return chunk;
    }
}
