using System.Buffers.Binary;
using IbmcKvm.Core.Video;

namespace IbmcKvm.Core.Recording;

public sealed class RepRecordingWriter : IAsyncDisposable
{
    private const int FileHeaderLength = 52;
    private const int IndexEntryCount = 20;
    private const int IndexEntryLength = 12;
    private const int IndexHeaderLength = 7;
    private const int NextIndexPointerLength = 8;
    private const int IndexBlockLength = IndexHeaderLength + IndexEntryCount * IndexEntryLength + NextIndexPointerLength;
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly bool newCompression;
    private readonly byte quantizationTableCount;
    private long indexBlockPosition = -1;
    private int indexEntryPosition;
    private int lastSequence;
    private int initialized;
    private int disposed;

    public RepRecordingWriter(
        Stream stream,
        bool leaveOpen = false,
        bool newCompression = true,
        byte quantizationTableCount = 10)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException("A .rep stream must be writable and seekable.", nameof(stream));
        }

        this.stream = stream;
        this.leaveOpen = leaveOpen;
        this.newCompression = newCompression;
        this.quantizationTableCount = quantizationTableCount;
    }

    public async Task WriteFrameAsync(
        EncodedVideoFrame frame,
        int sequence,
        long timestampMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        if (frame.EncodedData.Length > int.MaxValue - 25)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (!frame.IsDifference)
        {
            await AddIndexAsync(sequence, cancellationToken).ConfigureAwait(false);
        }

        var record = BuildFrameRecord(frame, sequence, timestampMilliseconds);
        await stream.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        lastSequence = sequence;
        await UpdateHeaderAsync(cancellationToken).ConfigureAwait(false);
        stream.Position = stream.Length;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref initialized) != 0)
        {
            await UpdateHeaderAsync(CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (!leaveOpen)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref initialized, 1) != 0)
        {
            return;
        }

        stream.SetLength(0);
        await stream.WriteAsync(BuildFileHeader(0, 0), cancellationToken).ConfigureAwait(false);
    }

    private async Task AddIndexAsync(int sequence, CancellationToken cancellationToken)
    {
        if (indexBlockPosition < 0)
        {
            await AppendIndexBlockAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (indexEntryPosition >= IndexEntryCount)
        {
            var nextPosition = stream.Length;
            stream.Position = indexBlockPosition + IndexHeaderLength + IndexEntryCount * IndexEntryLength;
            var next = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(next, nextPosition);
            await stream.WriteAsync(next, cancellationToken).ConfigureAwait(false);
            stream.Position = nextPosition;
            await AppendIndexBlockAsync(cancellationToken).ConfigureAwait(false);
        }

        var entryPosition = indexBlockPosition + IndexHeaderLength + indexEntryPosition * IndexEntryLength;
        var framePosition = stream.Length;
        var entry = new byte[IndexEntryLength];
        BinaryPrimitives.WriteInt32BigEndian(entry, sequence);
        BinaryPrimitives.WriteInt64BigEndian(entry.AsSpan(4), framePosition);
        stream.Position = entryPosition;
        await stream.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
        indexEntryPosition++;
        stream.Position = stream.Length;
    }

    private async Task AppendIndexBlockAsync(CancellationToken cancellationToken)
    {
        indexBlockPosition = stream.Length;
        indexEntryPosition = 0;
        var block = new byte[IndexBlockLength];
        WriteRecordHeader(block, type: 2);
        await stream.WriteAsync(block, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateHeaderAsync(CancellationToken cancellationToken)
    {
        var position = stream.Position;
        stream.Position = 0;
        await stream.WriteAsync(BuildFileHeader(lastSequence, stream.Length), cancellationToken).ConfigureAwait(false);
        stream.Position = position;
    }

    private byte[] BuildFileHeader(int totalFrames, long fileLength)
    {
        var header = new byte[FileHeaderLength];
        WriteRecordHeader(header, type: 1);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), totalFrames);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(20), fileLength);
        header[28] = newCompression ? (byte)0 : (byte)1;
        header[29] = quantizationTableCount;
        return header;
    }

    private static byte[] BuildFrameRecord(
        EncodedVideoFrame frame,
        int sequence,
        long timestampMilliseconds)
    {
        var record = new byte[25 + frame.EncodedData.Length];
        WriteRecordHeader(record, type: 3);
        record[7] = checked((byte)((frame.QuantizationTable & 0x0F) << 4 | (frame.IsDifference ? 1 : 0)));
        BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(9), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(13), checked((ushort)frame.Width));
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(15), checked((ushort)frame.Height));
        BinaryPrimitives.WriteInt64BigEndian(record.AsSpan(17), timestampMilliseconds);
        frame.EncodedData.CopyTo(record.AsSpan(25));
        return record;
    }

    private static void WriteRecordHeader(Span<byte> destination, byte type)
    {
        destination[0] = 0xFE;
        destination[1] = 0xF6;
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(2, 4), destination.Length);
        destination[6] = type;
    }
}
