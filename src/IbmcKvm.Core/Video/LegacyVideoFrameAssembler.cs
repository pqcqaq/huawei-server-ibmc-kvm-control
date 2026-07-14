using System.Buffers.Binary;

namespace IbmcKvm.Core.Video;

public sealed record EncodedVideoFrame(
    byte FrameNumber,
    bool IsDifference,
    int Width,
    int Height,
    int RemoteCursorX,
    int RemoteCursorY,
    byte ColorDepth,
    int QuantizationTable,
    byte[] EncodedData);

/// <summary>
/// Mirrors KVMUtil.isComplete/combine while bounding all attacker-controlled
/// lengths. Chunks contain a 2-byte index, frame number, then frame data.
/// </summary>
public sealed class LegacyVideoFrameAssembler
{
    private const int ChunkPrefixLength = 3;
    private const int FirstChunkMetadataLength = 17;
    private const int MaximumTrackedFrames = 2;
    private readonly int maximumFrameBytes;
    private readonly Dictionary<byte, PendingFrame> pending = [];
    private readonly Queue<byte> insertionOrder = [];

    public LegacyVideoFrameAssembler(int maximumFrameBytes = 16 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFrameBytes, 1);
        this.maximumFrameBytes = maximumFrameBytes;
    }

    public bool TryAddChunk(ReadOnlySpan<byte> chunk, out EncodedVideoFrame? frame)
    {
        frame = null;
        if (chunk.Length < ChunkPrefixLength)
        {
            throw new InvalidDataException("The video chunk is shorter than its prefix.");
        }

        var index = BinaryPrimitives.ReadUInt16BigEndian(chunk);
        var frameNumber = chunk[2];
        if (!pending.TryGetValue(frameNumber, out var state))
        {
            state = new PendingFrame(frameNumber);
            pending.Add(frameNumber, state);
            insertionOrder.Enqueue(frameNumber);
            TrimTrackedFrames();
        }

        if (state.Chunks.TryGetValue(index, out var existing))
        {
            if (!existing.AsSpan().SequenceEqual(chunk))
            {
                Drop(frameNumber);
                throw new InvalidDataException("Conflicting duplicate video chunk.");
            }

            return false;
        }

        var copy = chunk.ToArray();
        state.Chunks.Add(index, copy);
        if (index != 0)
        {
            state.CollectedLength = checked(state.CollectedLength + chunk.Length - ChunkPrefixLength);
        }

        if (index == 0)
        {
            if (chunk.Length < FirstChunkMetadataLength)
            {
                Drop(frameNumber);
                throw new InvalidDataException("The first video chunk lacks frame metadata.");
            }

            state.ExpectedLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunk.Slice(3, 4)));
            if (state.ExpectedLength is < 1 || state.ExpectedLength > maximumFrameBytes)
            {
                Drop(frameNumber);
                throw new InvalidDataException("The advertised video frame length is invalid.");
            }
        }

        if (state.ExpectedLength is null)
        {
            return false;
        }

        if (state.CollectedLength > state.ExpectedLength)
        {
            Drop(frameNumber);
            throw new InvalidDataException("Video chunks exceed the advertised frame length.");
        }

        if (state.CollectedLength != state.ExpectedLength)
        {
            return false;
        }

        frame = Complete(state);
        Drop(frameNumber);
        return true;
    }

    public void Reset()
    {
        pending.Clear();
        insertionOrder.Clear();
    }

    private static EncodedVideoFrame Complete(PendingFrame state)
    {
        if (!state.Chunks.TryGetValue(0, out var first))
        {
            throw new InvalidDataException("A complete frame has no first chunk.");
        }

        var expectedIndex = 0;
        foreach (var actualIndex in state.Chunks.Keys.Order())
        {
            if (actualIndex != expectedIndex++)
            {
                throw new InvalidDataException("The video frame has a missing chunk.");
            }
        }

        var flags = first[7];
        var width = ((flags & 0x7F) << 8) | first[8];
        var height = BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(9, 2));
        if (width is < 1 or > 8192 || height is < 1 or > 8192)
        {
            throw new InvalidDataException("The video frame dimensions are invalid.");
        }

        var encoded = new byte[checked(state.ExpectedLength!.Value + 1)];
        encoded[0] = (byte)(flags >> 7);
        var destination = 1;
        foreach (var chunk in state.Chunks
                     .Where(static item => item.Key != 0)
                     .OrderBy(static item => item.Key)
                     .Select(static item => item.Value))
        {
            chunk.AsSpan(ChunkPrefixLength).CopyTo(encoded.AsSpan(destination));
            destination += chunk.Length - ChunkPrefixLength;
        }

        return new EncodedVideoFrame(
            state.FrameNumber,
            (flags & 0x80) != 0,
            width,
            height,
            BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(12, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(14, 2)),
            first[16],
            Math.Clamp((first[16] & 0x0F) - 1, 0, 9),
            encoded);
    }

    private void TrimTrackedFrames()
    {
        while (pending.Count > MaximumTrackedFrames && insertionOrder.TryDequeue(out var oldest))
        {
            pending.Remove(oldest);
        }
    }

    private void Drop(byte frameNumber) => pending.Remove(frameNumber);

    private sealed class PendingFrame(byte frameNumber)
    {
        public byte FrameNumber { get; } = frameNumber;
        public SortedDictionary<ushort, byte[]> Chunks { get; } = [];
        public int CollectedLength { get; set; }
        public int? ExpectedLength { get; set; }
    }
}
