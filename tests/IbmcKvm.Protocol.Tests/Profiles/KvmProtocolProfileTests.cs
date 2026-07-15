using IbmcKvm.Protocol.Profiles;

namespace IbmcKvm.Protocol.Tests.Profiles;

public sealed class KvmProtocolProfileTests
{
    [Fact]
    public void ModernAndLegacyIbmcUseTheModernConnectContract()
    {
        Assert.Equal(
            new byte[] { 0x06, 1, 3, 1, 1 },
            ModernKvmProtocolProfile.Instance.BuildConnectPayload(1, 3));
        Assert.Equal(
            new byte[] { 0x06, 2, 2, 1, 1 },
            LegacyIbmcProtocolProfile.Instance.BuildConnectPayload(2, 2));
        Assert.Equal(KvmWireFormat.ModernCodeKey, ModernKvmProtocolProfile.Instance.WireFormat);
        Assert.Equal(KvmWireFormat.ModernCodeKey, LegacyIbmcProtocolProfile.Instance.WireFormat);
    }

    [Fact]
    public void ImanaUsesItsSourceVerifiedConnectAndReconnectFlags()
    {
        var profile = ImanaKvmProtocolProfile.Instance;

        Assert.Equal(new byte[] { 0x06, 1, 3, 0 }, profile.BuildConnectPayload(1, 3));
        Assert.Equal(new byte[] { 0x06, 1, 3, 1 }, profile.BuildConnectPayload(1, 3, reconnect: true));
        Assert.Equal(KvmWireFormat.ImanaSessionId, profile.WireFormat);
        Assert.True(profile.Capabilities.SupportsEncryptedKvm);
        Assert.True(profile.Capabilities.InputModes.HasFlag(KvmInputModes.Relative));
    }

    [Fact]
    public void ProfilesExposeImmutableColorDepths()
    {
        var depths = ModernKvmProtocolProfile.Instance.Capabilities.ColorDepths;

        Assert.Equal(new byte[] { 3, 2, 1, 0 }, depths);
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)depths).Add(4));
    }
}
