namespace IbmcKvm.Core.VirtualMedia;

public enum MediaDeviceKind
{
    Floppy,
    Optical,
}

public interface IRandomAccessMedia : IAsyncDisposable
{
    string DisplayName { get; }

    MediaDeviceKind DeviceKind { get; }

    long Length { get; }

    int BlockSize { get; }

    bool IsReadOnly { get; }

    ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

internal static class MediaRange
{
    public static void Validate(long offset, int count, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > length || count > length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The media request exceeds the mounted medium.");
        }
    }
}
