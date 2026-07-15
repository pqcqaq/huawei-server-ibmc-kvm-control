using IbmcKvm.Core.Session;
using IbmcKvm.Protocol.Profiles;

namespace IbmcKvm.Core.Tests.Session;

public sealed class KvmSessionPermissionsTests
{
    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(2, true, false, true)]
    [InlineData(3, true, true, true)]
    [InlineData(4, true, true, true)]
    public void MapsOriginalPrivilegeLevels(
        int privilege,
        bool canControlKvm,
        bool canControlPower,
        bool canUseVirtualMedia)
    {
        var permissions = KvmSessionPermissions.Create(
            privilege,
            ModernKvmProtocolProfile.Instance.Capabilities,
            powerDenied: false,
            virtualMediaDenied: false);

        Assert.Equal(canControlKvm, permissions.CanControlKvm);
        Assert.Equal(canControlPower, permissions.CanControlPower);
        Assert.Equal(canUseVirtualMedia, permissions.CanUseVirtualMedia);
    }

    [Fact]
    public void ServerDenialsRemoveOnlyTheAffectedCapability()
    {
        var capabilities = ModernKvmProtocolProfile.Instance.Capabilities;

        var powerDenied = KvmSessionPermissions.Create(4, capabilities, true, false);
        var mediaDenied = KvmSessionPermissions.Create(4, capabilities, false, true);

        Assert.False(powerDenied.CanControlPower);
        Assert.True(powerDenied.CanUseVirtualMedia);
        Assert.True(mediaDenied.CanControlPower);
        Assert.False(mediaDenied.CanUseVirtualMedia);
    }

    [Fact]
    public void MonitorSessionIsReadOnlyAtEveryControlBoundary()
    {
        var permissions = KvmSessionPermissions.Create(
            4,
            ModernKvmProtocolProfile.Instance.Capabilities,
            powerDenied: false,
            virtualMediaDenied: false,
            readOnly: true);

        Assert.False(permissions.CanControlKvm);
        Assert.False(permissions.CanControlPower);
        Assert.False(permissions.CanUseVirtualMedia);
    }
}
