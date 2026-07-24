using IbmcKvm.Core.VirtualMedia;

namespace IbmcKvm.Core.Tests.VirtualMedia;

public sealed class PhysicalDriveMediaTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"ibmc-physical-{Guid.NewGuid():N}");

    [Fact]
    public void LinuxEnumerationFindsOpticalAndFloppyDevices()
    {
        var sys = Path.Combine(root, "sys");
        var dev = Path.Combine(root, "dev");
        CreateDevice(sys, dev, "sr0", type: "5", sectors: 4096, blockSize: 2048, vendor: "ACME", model: "DVD");
        CreateDevice(sys, dev, "fd0", type: null, sectors: 2880, blockSize: 512);

        var devices = PhysicalDriveMedia.EnumerateLinux(sys, dev);

        Assert.Collection(
            devices,
            floppy =>
            {
                Assert.Equal(MediaDeviceKind.Floppy, floppy.DeviceKind);
                Assert.Equal(Path.Combine(dev, "fd0"), floppy.DevicePath);
                Assert.Equal(1_474_560, floppy.Capacity);
                Assert.Equal(512, floppy.BlockSize);
            },
            optical =>
            {
                Assert.Equal(MediaDeviceKind.Optical, optical.DeviceKind);
                Assert.Contains("ACME DVD", optical.DisplayName, StringComparison.Ordinal);
                Assert.Equal(2_097_152, optical.Capacity);
                Assert.Equal(2048, optical.BlockSize);
            });
    }

    [Fact]
    public void LinuxEnumerationIgnoresDisksPartitionsAndMissingDeviceNodes()
    {
        var sys = Path.Combine(root, "sys");
        var dev = Path.Combine(root, "dev");
        CreateDevice(sys, dev, "sda", type: "0", sectors: 1024, blockSize: 512);
        CreateDevice(sys, dev, "sr0", type: "5", sectors: 4096, blockSize: 2048, createNode: false);
        CreateDevice(sys, dev, "sr0p1", type: "5", sectors: 1024, blockSize: 2048, partition: true);

        Assert.Empty(PhysicalDriveMedia.EnumerateLinux(sys, dev));
    }

    [Fact]
    public void MissingSysfsDirectoryReturnsEmptyList()
    {
        Assert.Empty(PhysicalDriveMedia.EnumerateLinux(Path.Combine(root, "missing"), Path.Combine(root, "dev")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateDevice(
        string sys,
        string dev,
        string name,
        string? type,
        long sectors,
        int blockSize,
        string? vendor = null,
        string? model = null,
        bool partition = false,
        bool createNode = true)
    {
        var entry = Path.Combine(sys, name);
        Directory.CreateDirectory(Path.Combine(entry, "queue"));
        Directory.CreateDirectory(Path.Combine(entry, "device"));
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(entry, "size"), sectors.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(entry, "queue", "logical_block_size"), blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (type is not null)
        {
            File.WriteAllText(Path.Combine(entry, "device", "type"), type);
        }

        if (vendor is not null)
        {
            File.WriteAllText(Path.Combine(entry, "device", "vendor"), vendor);
        }

        if (model is not null)
        {
            File.WriteAllText(Path.Combine(entry, "device", "model"), model);
        }

        if (partition)
        {
            File.WriteAllText(Path.Combine(entry, "partition"), "1");
        }

        if (createNode)
        {
            File.WriteAllBytes(Path.Combine(dev, name), new byte[1]);
        }
    }
}
