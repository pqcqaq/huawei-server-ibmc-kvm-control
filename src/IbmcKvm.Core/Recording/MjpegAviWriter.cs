using System.Buffers.Binary;

namespace IbmcKvm.Core.Recording;

public sealed class MjpegAviWriter : IAsyncDisposable
{
    private const int HeaderLength = 224;
    private const int MaximumJpegLength = 64 * 1024 * 1024;
    private const long RiffSizeOffset = 4;
    private const long MainHeaderMaximumBytesOffset = 36;
    private const long MainHeaderTotalFramesOffset = 48;
    private const long MainHeaderSuggestedBufferOffset = 60;
    private const long StreamHeaderLengthOffset = 140;
    private const long StreamHeaderSuggestedBufferOffset = 144;
    private const long MoviListSizeOffset = 216;
    private const long MoviFourCcOffset = 220;
    private const long MoviDataOffset = HeaderLength;
    private readonly Stream stream;
    private readonly Stream indexStream;
    private readonly bool leaveOpen;
    private readonly int width;
    private readonly int height;
    private readonly int frameRate;
    private uint frameCount;
    private uint maximumFrameLength;
    private int initialized;
    private int disposed;

    public MjpegAviWriter(
        Stream stream,
        int width,
        int height,
        int frameRate = 20,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException("An AVI stream must be writable and seekable.", nameof(stream));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameRate, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameRate, 120);
        this.stream = stream;
        this.width = width;
        this.height = height;
        this.frameRate = frameRate;
        this.leaveOpen = leaveOpen;
        var indexPath = Path.Combine(Path.GetTempPath(), $"ibmc-kvm-{Guid.NewGuid():N}.idx");
        indexStream = new FileStream(
            indexPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    }

    public uint FrameCount => frameCount;

    public async Task WriteFrameAsync(
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ValidateJpeg(jpeg.Span);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (frameCount == uint.MaxValue)
        {
            throw new IOException("The AVI frame count exceeds the format limit.");
        }

        var paddedLength = checked(jpeg.Length + (jpeg.Length & 1));
        if (stream.Length + 8L + paddedLength + 8L + indexStream.Length + 16L > uint.MaxValue + 8L)
        {
            throw new IOException("The AVI file exceeds the 4 GiB RIFF limit.");
        }

        var chunkOffset = checked((uint)(stream.Position - MoviFourCcOffset));
        var chunkHeader = new byte[8];
        WriteFourCc(chunkHeader, "00dc");
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.AsSpan(4), checked((uint)jpeg.Length));
        await stream.WriteAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(jpeg, cancellationToken).ConfigureAwait(false);
        if ((jpeg.Length & 1) != 0)
        {
            await stream.WriteAsync(new byte[1], cancellationToken).ConfigureAwait(false);
        }

        var index = new byte[16];
        WriteFourCc(index, "00dc");
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(4), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(8), chunkOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(12), checked((uint)jpeg.Length));
        await indexStream.WriteAsync(index, cancellationToken).ConfigureAwait(false);
        frameCount++;
        maximumFrameLength = Math.Max(maximumFrameLength, checked((uint)jpeg.Length));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);
            await FinalizeAsync().ConfigureAwait(false);
        }
        finally
        {
            await indexStream.DisposeAsync().ConfigureAwait(false);
            if (!leaveOpen)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref initialized, 1) != 0)
        {
            return;
        }

        stream.SetLength(0);
        await stream.WriteAsync(BuildHeader(), cancellationToken).ConfigureAwait(false);
    }

    private async Task FinalizeAsync()
    {
        var moviEnd = stream.Position;
        var moviSize = checked((uint)(4 + moviEnd - MoviDataOffset));
        var indexLength = checked((uint)indexStream.Length);
        var indexHeader = new byte[8];
        WriteFourCc(indexHeader, "idx1");
        BinaryPrimitives.WriteUInt32LittleEndian(indexHeader.AsSpan(4), indexLength);
        await stream.WriteAsync(indexHeader).ConfigureAwait(false);
        indexStream.Position = 0;
        await indexStream.CopyToAsync(stream).ConfigureAwait(false);

        var fileLength = stream.Position;
        await WriteUInt32Async(RiffSizeOffset, checked((uint)(fileLength - 8))).ConfigureAwait(false);
        await WriteUInt32Async(MoviListSizeOffset, moviSize).ConfigureAwait(false);
        await WriteUInt32Async(MainHeaderTotalFramesOffset, frameCount).ConfigureAwait(false);
        await WriteUInt32Async(StreamHeaderLengthOffset, frameCount).ConfigureAwait(false);
        await WriteUInt32Async(
            MainHeaderMaximumBytesOffset,
            checked((uint)Math.Min(uint.MaxValue, (ulong)maximumFrameLength * (uint)frameRate)))
            .ConfigureAwait(false);
        await WriteUInt32Async(MainHeaderSuggestedBufferOffset, maximumFrameLength).ConfigureAwait(false);
        await WriteUInt32Async(StreamHeaderSuggestedBufferOffset, maximumFrameLength).ConfigureAwait(false);
        stream.Position = fileLength;
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteUInt32Async(long offset, uint value)
    {
        var position = stream.Position;
        stream.Position = offset;
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        await stream.WriteAsync(buffer).ConfigureAwait(false);
        stream.Position = position;
    }

    private byte[] BuildHeader()
    {
        var header = new byte[HeaderLength];
        WriteFourCc(header.AsSpan(0), "RIFF");
        WriteFourCc(header.AsSpan(8), "AVI ");
        WriteFourCc(header.AsSpan(12), "LIST");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 192);
        WriteFourCc(header.AsSpan(20), "hdrl");
        WriteFourCc(header.AsSpan(24), "avih");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), checked((uint)(1_000_000 / frameRate)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), checked((uint)height));

        WriteFourCc(header.AsSpan(88), "LIST");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(92), 116);
        WriteFourCc(header.AsSpan(96), "strl");
        WriteFourCc(header.AsSpan(100), "strh");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(104), 56);
        WriteFourCc(header.AsSpan(108), "vids");
        WriteFourCc(header.AsSpan(112), "MJPG");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(128), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(132), checked((uint)frameRate));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(148), uint.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(160), checked((short)width));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(162), checked((short)height));

        WriteFourCc(header.AsSpan(164), "strf");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(168), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(172), 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(176), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(180), height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(184), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(186), 24);
        WriteFourCc(header.AsSpan(188), "MJPG");
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(192),
            checked((uint)(width * height * 3L)));

        WriteFourCc(header.AsSpan(212), "LIST");
        WriteFourCc(header.AsSpan(220), "movi");
        return header;
    }

    private static void ValidateJpeg(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length is < 4 or > MaximumJpegLength ||
            jpeg[0] != 0xFF || jpeg[1] != 0xD8 ||
            jpeg[^2] != 0xFF || jpeg[^1] != 0xD9)
        {
            throw new InvalidDataException("An AVI MJPEG frame must be a bounded complete JPEG image.");
        }
    }

    private static void WriteFourCc(Span<byte> destination, string value)
    {
        if (value.Length != 4)
        {
            throw new ArgumentException("A RIFF FourCC contains exactly four characters.", nameof(value));
        }

        for (var index = 0; index < 4; index++)
        {
            destination[index] = checked((byte)value[index]);
        }
    }
}
