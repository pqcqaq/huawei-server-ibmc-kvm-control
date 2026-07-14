using IbmcKvm.App.VirtualMedia;
using IbmcKvm.Core.VirtualMedia;

namespace IbmcKvm.App.Tests.VirtualMedia;

public sealed class VirtualMediaUiRulesTests
{
    [Theory]
    [InlineData(MediaDeviceKind.Floppy, VirtualMediaSourceKind.Image, true, false, true, false)]
    [InlineData(MediaDeviceKind.Floppy, VirtualMediaSourceKind.PhysicalDrive, false, true, true, true)]
    [InlineData(MediaDeviceKind.Optical, VirtualMediaSourceKind.Image, true, false, false, false)]
    [InlineData(MediaDeviceKind.Optical, VirtualMediaSourceKind.PhysicalDrive, false, true, false, true)]
    [InlineData(MediaDeviceKind.Optical, VirtualMediaSourceKind.Directory, true, false, false, false)]
    public void MapsSourceToFeatureCompleteControlState(
        MediaDeviceKind kind,
        VirtualMediaSourceKind source,
        bool path,
        bool drive,
        bool writeProtect,
        bool createImage)
    {
        var state = VirtualMediaUiRules.GetControlState(kind, source);

        Assert.Equal(path, state.ShowPath);
        Assert.Equal(drive, state.ShowPhysicalDrive);
        Assert.Equal(writeProtect, state.ShowWriteProtection);
        Assert.Equal(createImage, state.CanCreateImage);
    }

    [Fact]
    public void RejectsDirectoryAsFloppySource()
    {
        Assert.Throws<ArgumentException>(() =>
            VirtualMediaUiRules.GetControlState(MediaDeviceKind.Floppy, VirtualMediaSourceKind.Directory));
    }
}
