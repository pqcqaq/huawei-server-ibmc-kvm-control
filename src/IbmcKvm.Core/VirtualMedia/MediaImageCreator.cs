namespace IbmcKvm.Core.VirtualMedia;

public sealed record MediaCopyProgress(long BytesCopied, long TotalBytes)
{
    public double Percentage => TotalBytes == 0 ? 100 : BytesCopied * 100d / TotalBytes;
}

public static class MediaImageCreator
{
    public static async Task CreateAsync(
        IRandomAccessMedia source,
        string destinationPath,
        IProgress<MediaCopyProgress>? progress = null,
        int bufferSize = 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 4096);

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            await using var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[Math.Min(bufferSize, checked((int)Math.Min(source.Length, int.MaxValue)))];
            long offset = 0;
            while (offset < source.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(buffer.Length, source.Length - offset));
                await source.ReadAsync(offset, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                offset += count;
                progress?.Report(new(offset, source.Length));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
