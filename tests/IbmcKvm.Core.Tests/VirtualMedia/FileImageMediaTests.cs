using IbmcKvm.Core.VirtualMedia;

namespace IbmcKvm.Core.Tests.VirtualMedia;

public sealed class FileImageMediaTests
{
    [Fact]
    public async Task ReadsWritesWithinBoundsAndHonorsWriteProtection()
    {
        var path = CreateFile(Enumerable.Range(0, 1024).Select(static value => (byte)value).ToArray());
        try
        {
            await using (var writable = new FileImageMedia(path, MediaDeviceKind.Floppy, isReadOnly: false))
            {
                var data = new byte[16];
                await writable.ReadAsync(100, data);
                Assert.Equal(Enumerable.Range(100, 16).Select(static value => (byte)value), data);

                await writable.WriteAsync(100, new byte[] { 9, 8, 7, 6 });
                await writable.FlushAsync();
                var written = new byte[4];
                await writable.ReadAsync(100, written);
                Assert.Equal(new byte[] { 9, 8, 7, 6 }, written);
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                    writable.ReadAsync(1020, new byte[8]).AsTask());
            }

            await using var readOnly = new FileImageMedia(path, MediaDeviceKind.Floppy);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                readOnly.WriteAsync(0, new byte[1]).AsTask());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DetectsFileTruncationWhileMounted()
    {
        var path = CreateFile(new byte[1024]);
        try
        {
            await using var media = new FileImageMedia(path, MediaDeviceKind.Floppy);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.SetLength(16);
            }

            await Assert.ThrowsAsync<EndOfStreamException>(() =>
                media.ReadAsync(0, new byte[512]).AsTask());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ibmc-kvm-{Guid.NewGuid():N}.img");
        File.WriteAllBytes(path, content);
        return path;
    }
}
