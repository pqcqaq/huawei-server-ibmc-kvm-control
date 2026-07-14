using Microsoft.Win32.SafeHandles;

namespace IbmcKvm.Core.VirtualMedia;

public sealed class FileImageMedia : IRandomAccessMedia
{
    private readonly SafeFileHandle handle;
    private int disposed;

    public FileImageMedia(
        string path,
        MediaDeviceKind deviceKind,
        bool isReadOnly = true,
        int? blockSize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var access = isReadOnly ? FileAccess.Read : FileAccess.ReadWrite;
        handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            access,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            Length = RandomAccess.GetLength(handle);
            DeviceKind = deviceKind;
            IsReadOnly = isReadOnly;
            BlockSize = blockSize ?? (deviceKind == MediaDeviceKind.Optical ? 2048 : 512);
            ArgumentOutOfRangeException.ThrowIfLessThan(BlockSize, 1);
            DisplayName = Path.GetFileName(fullPath);
            FilePath = fullPath;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public string DisplayName { get; }

    public string FilePath { get; }

    public MediaDeviceKind DeviceKind { get; }

    public long Length { get; }

    public int BlockSize { get; }

    public bool IsReadOnly { get; }

    public async ValueTask ReadAsync(
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        MediaRange.Validate(offset, buffer.Length, Length);
        var completed = 0;
        while (completed < buffer.Length)
        {
            var count = await RandomAccess.ReadAsync(
                handle,
                buffer[completed..],
                offset + completed,
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The image file changed while it was mounted.");
            }

            completed += count;
        }
    }

    public async ValueTask WriteAsync(
        long offset,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsReadOnly)
        {
            throw new UnauthorizedAccessException("The mounted image is write-protected.");
        }

        MediaRange.Validate(offset, buffer.Length, Length);
        await RandomAccess.WriteAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReadOnly)
        {
            RandomAccess.FlushToDisk(handle);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            handle.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
