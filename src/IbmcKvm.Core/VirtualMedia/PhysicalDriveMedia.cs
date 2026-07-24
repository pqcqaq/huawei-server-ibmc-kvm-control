using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IbmcKvm.Core.VirtualMedia;

public sealed record PhysicalDriveDescriptor(
    string DevicePath,
    string DisplayName,
    MediaDeviceKind DeviceKind,
    bool IsReady,
    long? Capacity,
    int BlockSize);

public sealed class PhysicalDriveMedia : IRandomAccessMedia
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlDiskGetDriveGeometry = 0x00070000;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const ulong BlkGetSize64 = 0x80081272;
    private const ulong BlkSszGet = 0x1268;

    private readonly SafeFileHandle handle;
    private int disposed;

    private PhysicalDriveMedia(
        PhysicalDriveDescriptor descriptor,
        SafeFileHandle handle,
        bool isReadOnly,
        long length,
        int blockSize)
    {
        this.handle = handle;
        DisplayName = descriptor.DisplayName;
        DevicePath = descriptor.DevicePath;
        DeviceKind = descriptor.DeviceKind;
        IsReadOnly = isReadOnly;
        Length = length;
        BlockSize = blockSize;
    }

    public string DisplayName { get; }

    public string DevicePath { get; }

    public MediaDeviceKind DeviceKind { get; }

    public long Length { get; }

    public int BlockSize { get; }

    public bool IsReadOnly { get; }

    public static IReadOnlyList<PhysicalDriveDescriptor> Enumerate()
    {
        if (OperatingSystem.IsWindows())
        {
            return EnumerateWindows();
        }

        if (OperatingSystem.IsLinux())
        {
            return EnumerateLinux("/sys/class/block", "/dev");
        }

        return [];
    }

    internal static IReadOnlyList<PhysicalDriveDescriptor> EnumerateLinux(string sysClassBlock, string devRoot)
    {
        if (!Directory.Exists(sysClassBlock))
        {
            return [];
        }

        var result = new List<PhysicalDriveDescriptor>();
        foreach (var entry in Directory.EnumerateDirectories(sysClassBlock))
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrWhiteSpace(name) || File.Exists(Path.Combine(entry, "partition")))
            {
                continue;
            }

            if (!TryGetLinuxDeviceKind(entry, name, out var kind))
            {
                continue;
            }

            var devicePath = Path.Combine(devRoot, name);
            if (!File.Exists(devicePath))
            {
                continue;
            }

            var blockSize = ReadInt64(Path.Combine(entry, "queue", "logical_block_size")) is > 0 and <= int.MaxValue
                ? checked((int)ReadInt64(Path.Combine(entry, "queue", "logical_block_size"))!.Value)
                : kind == MediaDeviceKind.Optical ? 2048 : 512;
            var sectors = ReadInt64(Path.Combine(entry, "size"));
            long? capacity = sectors is > 0 and <= (long.MaxValue / 512) ? sectors.Value * 512 : null;
            result.Add(new PhysicalDriveDescriptor(
                devicePath,
                FormatLinuxDisplayName(entry, name, kind),
                kind,
                capacity is > 0,
                capacity,
                blockSize));
        }

        return result
            .OrderBy(static item => item.DeviceKind)
            .ThenBy(static item => item.DevicePath, StringComparer.Ordinal)
            .ToArray();
    }

    public static PhysicalDriveMedia Open(PhysicalDriveDescriptor descriptor, bool writable = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.DeviceKind == MediaDeviceKind.Optical && writable)
        {
            throw new ArgumentException("Physical optical media is always read-only.", nameof(writable));
        }

        if (OperatingSystem.IsWindows())
        {
            return OpenWindows(descriptor, writable);
        }

        if (OperatingSystem.IsLinux())
        {
            return OpenLinux(descriptor, writable);
        }

        throw new PlatformNotSupportedException("Physical media is supported on Windows and Linux.");
    }

    public async ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
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
                throw new EndOfStreamException("The physical medium was removed during a read.");
            }

            completed += count;
        }
    }

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsReadOnly)
        {
            throw new UnauthorizedAccessException("The physical medium is write-protected.");
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

    private static IReadOnlyList<PhysicalDriveDescriptor> EnumerateWindows()
    {
        var result = new List<PhysicalDriveDescriptor>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            var isFloppy = drive.Name.Length >= 1 &&
                drive.Name[0] is 'A' or 'B' &&
                drive.DriveType == DriveType.Removable;
            var isOptical = drive.DriveType == DriveType.CDRom;
            if (!isFloppy && !isOptical)
            {
                continue;
            }

            bool ready;
            long? capacity = null;
            try
            {
                ready = drive.IsReady;
                if (ready)
                {
                    capacity = drive.TotalSize;
                }
            }
            catch (IOException)
            {
                ready = false;
            }
            catch (UnauthorizedAccessException)
            {
                ready = false;
            }

            var root = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
            result.Add(new PhysicalDriveDescriptor(
                $@"\\.\{root}",
                isOptical ? $"Optical drive {root}" : $"Floppy drive {root}",
                isOptical ? MediaDeviceKind.Optical : MediaDeviceKind.Floppy,
                ready,
                capacity,
                isOptical ? 2048 : 512));
        }

        return result;
    }

    private static PhysicalDriveMedia OpenWindows(PhysicalDriveDescriptor descriptor, bool writable)
    {
        var access = GenericRead | (writable ? GenericWrite : 0);
        var handle = CreateFile(
            descriptor.DevicePath,
            access,
            ShareRead | ShareWrite,
            0,
            OpenExisting,
            FileOptions.Asynchronous | FileOptions.RandomAccess,
            0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to open {descriptor.DisplayName}.");
        }

        try
        {
            var length = QueryWindowsLength(handle, descriptor.Capacity);
            var blockSize = QueryWindowsBlockSize(handle, descriptor.BlockSize);
            return new PhysicalDriveMedia(descriptor, handle, !writable, length, blockSize);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static PhysicalDriveMedia OpenLinux(PhysicalDriveDescriptor descriptor, bool writable)
    {
        var access = writable ? FileAccess.ReadWrite : FileAccess.Read;
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(
                descriptor.DevicePath,
                FileMode.Open,
                access,
                FileShare.ReadWrite,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Unable to open {descriptor.DisplayName}.", exception);
        }

        try
        {
            var length = QueryLinuxLength(handle, descriptor.Capacity);
            var blockSize = QueryLinuxBlockSize(handle, descriptor.BlockSize);
            return new PhysicalDriveMedia(descriptor, handle, !writable, length, blockSize);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static bool TryGetLinuxDeviceKind(string entry, string name, out MediaDeviceKind kind)
    {
        var deviceType = ReadTrimmed(Path.Combine(entry, "device", "type"));
        var deviceMedia = ReadTrimmed(Path.Combine(entry, "device", "media"));
        if (name.StartsWith("sr", StringComparison.Ordinal) ||
            string.Equals(deviceType, "5", StringComparison.Ordinal) ||
            string.Equals(deviceMedia, "cdrom", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaDeviceKind.Optical;
            return true;
        }

        if (name.StartsWith("fd", StringComparison.Ordinal) &&
            name.Length > 2 &&
            name[2..].All(char.IsAsciiDigit))
        {
            kind = MediaDeviceKind.Floppy;
            return true;
        }

        kind = default;
        return false;
    }

    private static string FormatLinuxDisplayName(string entry, string name, MediaDeviceKind kind)
    {
        var vendor = ReadTrimmed(Path.Combine(entry, "device", "vendor"));
        var model = ReadTrimmed(Path.Combine(entry, "device", "model"));
        var label = string.Join(
            ' ',
            new[] { vendor, model }.Where(static part => !string.IsNullOrWhiteSpace(part)))
            .Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            label = kind == MediaDeviceKind.Optical ? "Optical drive" : "Floppy drive";
        }

        return $"{label} ({Path.Combine("/dev", name)})";
    }

    private static long? ReadInt64(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            return long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException or FileNotFoundException)
        {
            return null;
        }
    }

    private static string? ReadTrimmed(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException or FileNotFoundException)
        {
            return null;
        }
    }

    private static long QueryWindowsLength(SafeFileHandle handle, long? fallback)
    {
        var output = new byte[8];
        if (DeviceIoControl(handle, IoctlDiskGetLengthInfo, 0, 0, output, output.Length, out _, 0))
        {
            return BitConverter.ToInt64(output);
        }

        if (fallback is > 0)
        {
            return fallback.Value;
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to determine physical-media length.");
    }

    private static int QueryWindowsBlockSize(SafeFileHandle handle, int fallback)
    {
        var output = new byte[24];
        if (DeviceIoControl(handle, IoctlDiskGetDriveGeometry, 0, 0, output, output.Length, out _, 0))
        {
            var value = BitConverter.ToInt32(output, 20);
            if (value > 0)
            {
                return value;
            }
        }

        return fallback;
    }

    private static long QueryLinuxLength(SafeFileHandle handle, long? fallback)
    {
        if (IoctlGetUlong(handle, BlkGetSize64, out var value) == 0 && value > 0 && value <= long.MaxValue)
        {
            return checked((long)value);
        }

        try
        {
            var length = RandomAccess.GetLength(handle);
            if (length > 0)
            {
                return length;
            }
        }
        catch (IOException)
        {
        }

        if (fallback is > 0)
        {
            return fallback.Value;
        }

        throw new IOException("Unable to determine physical-media length.");
    }

    private static int QueryLinuxBlockSize(SafeFileHandle handle, int fallback)
    {
        if (IoctlGetInt(handle, BlkSszGet, out var value) == 0 && value > 0)
        {
            return value;
        }

        return fallback;
    }

    private static int IoctlGetUlong(SafeFileHandle handle, ulong request, out ulong value) =>
        ioctl(handle.DangerousGetHandle(), request, out value);

    private static int IoctlGetInt(SafeFileHandle handle, ulong request, out int value) =>
        ioctl(handle.DangerousGetHandle(), request, out value);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        FileOptions flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        nint input,
        int inputSize,
        [Out] byte[] output,
        int outputSize,
        out int bytesReturned,
        nint overlapped);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(nint fileDescriptor, ulong request, out ulong value);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(nint fileDescriptor, ulong request, out int value);
}
