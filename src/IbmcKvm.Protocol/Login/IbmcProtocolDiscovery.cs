using IbmcKvm.Protocol.Profiles;

namespace IbmcKvm.Protocol.Login;

public static class IbmcProtocolDiscovery
{
    public static IKvmProtocolProfile SelectModern() => ModernKvmProtocolProfile.Instance;

    public static IKvmProtocolProfile SelectRmcp(int firmwareRevision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firmwareRevision);

        return firmwareRevision == 0
            ? ImanaKvmProtocolProfile.Instance
            : LegacyIbmcProtocolProfile.Instance;
    }
}
