using IbmcKvm.Core.VirtualMedia;

namespace IbmcKvm.Core.Tests.VirtualMedia;

public sealed class MediaImageCreatorTests
{
    [Fact]
    public async Task CopiesEntireMediumAndReportsProgress()
    {
        var sourcePath = Path.GetTempFileName();
        var destination = Path.Combine(Path.GetTempPath(), $"ibmc-kvm-copy-{Guid.NewGuid():N}.img");
        var content = Enumerable.Range(0, 20000).Select(static value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(sourcePath, content);
        try
        {
            await using var source = new FileImageMedia(sourcePath, MediaDeviceKind.Floppy);
            var reports = new List<MediaCopyProgress>();
            await MediaImageCreator.CreateAsync(source, destination, new InlineProgress(reports.Add), 4096);

            Assert.Equal(content, await File.ReadAllBytesAsync(destination));
            Assert.Equal(content.Length, reports[^1].BytesCopied);
            Assert.Equal(100, reports[^1].Percentage);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destination);
        }
    }

    [Fact]
    public async Task CancellationRemovesPartialImage()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"ibmc-kvm-copy-{Guid.NewGuid():N}.img");
        await using var source = new CancelingMedia();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MediaImageCreator.CreateAsync(source, destination));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, Path.GetFileName(destination) + ".*.partial"));
    }

    private sealed class InlineProgress(Action<MediaCopyProgress> report) : IProgress<MediaCopyProgress>
    {
        public void Report(MediaCopyProgress value) => report(value);
    }

    private sealed class CancelingMedia : IRandomAccessMedia
    {
        public string DisplayName => "cancel";
        public MediaDeviceKind DeviceKind => MediaDeviceKind.Floppy;
        public long Length => 8192;
        public int BlockSize => 512;
        public bool IsReadOnly => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromCanceled(new CancellationToken(canceled: true));
        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
