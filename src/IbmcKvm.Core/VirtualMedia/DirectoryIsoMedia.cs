using DiscUtils.Iso9660;

namespace IbmcKvm.Core.VirtualMedia;

public sealed class DirectoryIsoMedia : IRandomAccessMedia
{
    private readonly FileImageMedia image;
    private int disposed;

    private DirectoryIsoMedia(string sourceDirectory, string temporaryImagePath, FileImageMedia image)
    {
        SourceDirectory = sourceDirectory;
        TemporaryImagePath = temporaryImagePath;
        this.image = image;
    }

    public string DisplayName => $"{Path.GetFileName(SourceDirectory)} (directory)";

    public string SourceDirectory { get; }

    public string TemporaryImagePath { get; }

    public MediaDeviceKind DeviceKind => MediaDeviceKind.Optical;

    public long Length => image.Length;

    public int BlockSize => 2048;

    public bool IsReadOnly => true;

    public static async Task<DirectoryIsoMedia> CreateAsync(
        string sourceDirectory,
        string? volumeIdentifier = null,
        IProgress<MediaBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "ibmc-kvm");
        Directory.CreateDirectory(tempRoot);
        var tempPath = Path.Combine(tempRoot, $"directory-{Guid.NewGuid():N}.iso");
        try
        {
            await Task.Run(() => BuildIso(source, tempPath, volumeIdentifier, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return new DirectoryIsoMedia(source, tempPath, new FileImageMedia(tempPath, MediaDeviceKind.Optical));
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        image.ReadAsync(offset, buffer, cancellationToken);

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw new UnauthorizedAccessException("Directory-backed optical media is read-only.");

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
        image.FlushAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await image.DisposeAsync().ConfigureAwait(false);
        TryDelete(TemporaryImagePath);
    }

    private static void BuildIso(
        string source,
        string output,
        string? volumeIdentifier,
        IProgress<MediaBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directories = Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).ToArray();
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
        var total = directories.LongLength + files.LongLength;
        long completed = 0;

        var builder = new CDBuilder
        {
            UseJoliet = true,
            VolumeIdentifier = SanitizeVolumeIdentifier(volumeIdentifier ?? Path.GetFileName(source)),
        };

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AddDirectory(ToIsoPath(source, directory));
            progress?.Report(new(++completed, total, directory));
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AddFile(ToIsoPath(source, file), file);
            progress?.Report(new(++completed, total, file));
        }

        cancellationToken.ThrowIfCancellationRequested();
        builder.Build(output);
        progress?.Report(new(total, total, output));
    }

    private static string ToIsoPath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '\\');

    private static string SanitizeVolumeIdentifier(string value)
    {
        var sanitized = new string(value
            .Where(static character => char.IsAsciiLetterOrDigit(character) || character == '_')
            .Select(static character => char.ToUpperInvariant(character))
            .Take(32)
            .ToArray());
        return string.IsNullOrEmpty(sanitized) ? "IBMC_DIRECTORY" : sanitized;
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

public sealed record MediaBuildProgress(long CompletedItems, long TotalItems, string CurrentPath);
