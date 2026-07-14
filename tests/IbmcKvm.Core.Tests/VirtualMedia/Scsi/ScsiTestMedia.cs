using IbmcKvm.Core.VirtualMedia;

namespace IbmcKvm.Core.Tests.VirtualMedia.Scsi;

internal sealed class ScsiTestMedia(
    MediaDeviceKind kind,
    int blockSize,
    int blocks,
    bool readOnly = false) : IRandomAccessMedia
{
    private readonly byte[] content = Enumerable.Range(0, checked(blockSize * blocks))
        .Select(static value => (byte)value)
        .ToArray();

    public string DisplayName => "test-media";
    public MediaDeviceKind DeviceKind => kind;
    public long Length => content.Length;
    public int BlockSize => blockSize;
    public bool IsReadOnly => readOnly;
    public ReadOnlyMemory<byte> Content => content;

    public ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        content.AsMemory(checked((int)offset), buffer.Length).CopyTo(buffer);
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (readOnly)
        {
            throw new UnauthorizedAccessException();
        }

        buffer.CopyTo(content.AsMemory(checked((int)offset), buffer.Length));
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
