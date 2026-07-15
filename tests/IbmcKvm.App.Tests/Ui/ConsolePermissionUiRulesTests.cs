using IbmcKvm.App.Ui;
using IbmcKvm.Core.Session;
using IbmcKvm.Protocol.Profiles;

namespace IbmcKvm.App.Tests.Ui;

public sealed class ConsolePermissionUiRulesTests
{
    [Fact]
    public void UserCanControlConsoleButNotPower()
    {
        var capabilities = ModernKvmProtocolProfile.Instance.Capabilities;
        var permissions = KvmSessionPermissions.Create(2, capabilities, false, false);

        var availability = ConsolePermissionUiRules.Resolve(permissions, capabilities);

        Assert.True(availability.Input);
        Assert.True(availability.Keyboard);
        Assert.True(availability.VirtualMedia);
        Assert.False(availability.Power);
    }

    [Fact]
    public void ServerMediaDenialDisablesOnlyVirtualMedia()
    {
        var capabilities = ModernKvmProtocolProfile.Instance.Capabilities;
        var permissions = KvmSessionPermissions.Create(4, capabilities, false, true);

        var availability = ConsolePermissionUiRules.Resolve(permissions, capabilities);

        Assert.True(availability.Input);
        Assert.True(availability.Power);
        Assert.False(availability.VirtualMedia);
    }

    [Fact]
    public void ProfileCapabilitiesRestrictUnsupportedControls()
    {
        var capabilities = ImanaKvmProtocolProfile.Instance.Capabilities;
        var permissions = KvmSessionPermissions.Create(4, capabilities, false, false);

        var availability = ConsolePermissionUiRules.Resolve(permissions, capabilities);

        Assert.False(availability.VideoQuality);
        Assert.False(availability.Recording);
        Assert.True(availability.ColorDepth);
    }
}
