using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace IbmcKvm.Core.VirtualMedia;

public sealed record PhysicalDriveDescriptor(
    string DevicePath,
    string DisplayName,
    MediaDeviceKind DeviceKind,
    bool IsReady,
    long? Capacity,
    int BlockSize);

[SupportedOSPlatform("windows")]
public sealed class PhysicalDriveMedia : IRandomAccessMedia
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlDiskGetDriveGeometry = 0x00070000;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;

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
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

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

    public static PhysicalDriveMedia Open(PhysicalDriveDescriptor descriptor, bool writable = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.DeviceKind == MediaDeviceKind.Optical && writable)
        {
            throw new ArgumentException("Physical optical media is always read-only.", nameof(writable));
        }

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
            var length = QueryLength(handle, descriptor.Capacity);
            var blockSize = QueryBlockSize(handle, descriptor.BlockSize);
            return new PhysicalDriveMedia(descriptor, handle, !writable, length, blockSize);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
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

    private static long QueryLength(SafeFileHandle handle, long? fallback)
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

    private static int QueryBlockSize(SafeFileHandle handle, int fallback)
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
}
