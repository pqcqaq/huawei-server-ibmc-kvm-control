using DiscUtils.Iso9660;
using IbmcKvm.Core.VirtualMedia;

namespace IbmcKvm.Core.Tests.VirtualMedia;

public sealed class DirectoryIsoMediaTests
{
    [Fact]
    public async Task BuildsReadableJolietIsoAndDeletesTemporaryFile()
    {
        var source = Path.Combine(Path.GetTempPath(), $"ibmc-kvm-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "hello.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "世界.txt"), "world");
        string temporary;
        try
        {
            await using (var media = await DirectoryIsoMedia.CreateAsync(source))
            {
                temporary = media.TemporaryImagePath;
                Assert.True(File.Exists(temporary));
                Assert.True(media.IsReadOnly);
                Assert.Equal(2048, media.BlockSize);
                await using var isoStream = File.OpenRead(temporary);
                var fileSystem = new CDReader(isoStream, joliet: true);
                var files = fileSystem.Root.GetFiles()
                    .Concat(fileSystem.Root.GetDirectories("*", SearchOption.AllDirectories)
                        .SelectMany(static directory => directory.GetFiles()))
                    .Select(static file => file.FullName)
                    .ToArray();
                var hello = Assert.Single(files, static path =>
                    path.Split(';')[0].EndsWith("hello.txt", StringComparison.OrdinalIgnoreCase));
                var unicode = Assert.Single(files, static path =>
                    path.Split(';')[0].EndsWith("世界.txt", StringComparison.Ordinal));
                Assert.Equal("hello", await ReadTextAsync(fileSystem.OpenFile(hello, FileMode.Open)));
                Assert.Equal("world", await ReadTextAsync(fileSystem.OpenFile(unicode, FileMode.Open)));
            }

            Assert.False(File.Exists(temporary));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task CancelsBeforeBuildAndLeavesNoMountedImage()
    {
        var source = Path.Combine(Path.GetTempPath(), $"ibmc-kvm-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), new byte[1024]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DirectoryIsoMedia.CreateAsync(source, cancellationToken: cancellation.Token));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    private static async Task<string> ReadTextAsync(Stream stream)
    {
        await using (stream)
        using (var reader = new StreamReader(stream))
        {
            return await reader.ReadToEndAsync();
        }
    }
}
