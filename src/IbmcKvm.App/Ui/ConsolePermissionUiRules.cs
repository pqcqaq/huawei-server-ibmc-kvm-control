using IbmcKvm.Core.Session;
using IbmcKvm.Protocol.Profiles;

namespace IbmcKvm.App.Ui;

internal sealed record ConsoleControlAvailability(
    bool Input,
    bool MouseMode,
    bool VideoQuality,
    bool ColorDepth,
    bool Recording,
    bool Keyboard,
    bool Power,
    bool VirtualMedia);

internal static class ConsolePermissionUiRules
{
    public static ConsoleControlAvailability Resolve(
        KvmSessionPermissions permissions,
        KvmProtocolCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(capabilities);
        var kvm = permissions.CanControlKvm;
        return new ConsoleControlAvailability(
            Input: kvm,
            MouseMode: kvm && capabilities.InputModes != KvmInputModes.None,
            VideoQuality: kvm && capabilities.SupportsVideoQuality,
            ColorDepth: kvm && capabilities.ColorDepths.Length > 1,
            Recording: kvm && capabilities.SupportsRecording,
            Keyboard: kvm,
            Power: permissions.CanControlPower,
            VirtualMedia: permissions.CanUseVirtualMedia);
    }
}
