using System.Buffers.Binary;
using IbmcKvm.Core.Recording;
using IbmcKvm.Core.Video;

namespace IbmcKvm.Core.Tests.Recording;

public sealed class RepRecordingWriterTests
{
    [Fact]
    public async Task WritesHeaderIndexAndFrameUsingProtocolRepLayout()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new RepRecordingWriter(stream, leaveOpen: true))
        {
            await writer.WriteFrameAsync(Frame(difference: false), 1, 0x0102030405060708);
        }

        var bytes = stream.ToArray();
        Assert.Equal(new byte[] { 0xFE, 0xF6 }, bytes[..2]);
        Assert.Equal(52, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(2, 4)));
        Assert.Equal(1, bytes[6]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(bytes.Length, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(20, 8)));
        Assert.Equal(0, bytes[28]);
        Assert.Equal(10, bytes[29]);

        const int indexOffset = 52;
        Assert.Equal(2, bytes[indexOffset + 6]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(indexOffset + 7, 4)));
        const int frameOffset = 52 + 255;
        Assert.Equal(frameOffset, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(indexOffset + 11, 8)));
        Assert.Equal(3, bytes[frameOffset + 6]);
        Assert.Equal(0x20, bytes[frameOffset + 7]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(frameOffset + 9, 4)));
        Assert.Equal(1280, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(frameOffset + 13, 2)));
        Assert.Equal(720, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(frameOffset + 15, 2)));
        Assert.Equal(0x0102030405060708, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(frameOffset + 17, 8)));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, bytes[(frameOffset + 25)..]);
    }

    [Fact]
    public async Task ConsoleRecorderReportsDropsInsteadOfBlockingProducer()
    {
        await using var stream = new MemoryStream();
        await using var recorder = new ConsoleRecorder(
            new RepRecordingWriter(stream, leaveOpen: true),
            queueCapacity: 1);

        var accepted = 0;
        for (var index = 0; index < 1000; index++)
        {
            if (recorder.TryRecord(Frame(difference: index != 0), index))
            {
                accepted++;
            }
        }

        Assert.True(accepted > 0);
        Assert.True(recorder.DroppedFrames > 0);
    }

    [Fact]
    public async Task LinksAdditionalIndexBlocksAfterTwentyKeyFrames()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new RepRecordingWriter(stream, leaveOpen: true))
        {
            for (var sequence = 1; sequence <= 25; sequence++)
            {
                await writer.WriteFrameAsync(Frame(difference: false), sequence, sequence * 10L);
            }
        }

        var bytes = stream.ToArray();
        const int firstIndexOffset = 52;
        var secondIndexOffset = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(firstIndexOffset + 247, 8));
        Assert.True(secondIndexOffset > firstIndexOffset);
        Assert.Equal(2, bytes[checked((int)secondIndexOffset) + 6]);
        Assert.Equal(
            21,
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(checked((int)secondIndexOffset) + 7, 4)));
        Assert.Equal(25, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4)));
    }

    private static EncodedVideoFrame Frame(bool difference) => new(
        FrameNumber: 1,
        IsDifference: difference,
        Width: 1280,
        Height: 720,
        RemoteCursorX: 0,
        RemoteCursorY: 0,
        ColorDepth: 3,
        QuantizationTable: 2,
        EncodedData: [0, 1, 2, 3]);
}
