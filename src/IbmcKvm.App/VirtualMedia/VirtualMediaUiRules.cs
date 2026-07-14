using IbmcKvm.Core.VirtualMedia;

namespace IbmcKvm.App.VirtualMedia;

public enum VirtualMediaSourceKind
{
    Image,
    PhysicalDrive,
    Directory,
}

public sealed record VirtualMediaControlState(
    bool ShowPath,
    bool ShowPhysicalDrive,
    bool ShowWriteProtection,
    bool CanCreateImage);

public static class VirtualMediaUiRules
{
    public static VirtualMediaControlState GetControlState(
        MediaDeviceKind deviceKind,
        VirtualMediaSourceKind sourceKind)
    {
        if (deviceKind == MediaDeviceKind.Floppy && sourceKind == VirtualMediaSourceKind.Directory)
        {
            throw new ArgumentException("A directory can only be mapped as optical media.", nameof(sourceKind));
        }

        return new VirtualMediaControlState(
            ShowPath: sourceKind is VirtualMediaSourceKind.Image or VirtualMediaSourceKind.Directory,
            ShowPhysicalDrive: sourceKind == VirtualMediaSourceKind.PhysicalDrive,
            ShowWriteProtection: deviceKind == MediaDeviceKind.Floppy,
            CanCreateImage: sourceKind == VirtualMediaSourceKind.PhysicalDrive);
    }
}
