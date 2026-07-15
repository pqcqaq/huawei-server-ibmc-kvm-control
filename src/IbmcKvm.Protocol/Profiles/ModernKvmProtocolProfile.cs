using System.Collections.Immutable;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Protocol.Profiles;

public sealed class ModernKvmProtocolProfile : IKvmProtocolProfile
{
    public static ModernKvmProtocolProfile Instance { get; } = new();

    private ModernKvmProtocolProfile()
    {
    }

    public KvmProtocolKind Kind => KvmProtocolKind.ModernIbmc;

    public KvmWireFormat WireFormat => KvmWireFormat.ModernCodeKey;

    public KvmProtocolCapabilities Capabilities { get; } = new(
        SupportsEncryptedKvm: true,
        SupportsEncryptedVirtualMedia: true,
        SupportsReconnect: true,
        SupportsRecording: true,
        SupportsChassis: true,
        SupportsVirtualMedia: true,
        SupportsPowerControl: true,
        SupportsVideoQuality: true,
        InputModes: KvmInputModes.Relative | KvmInputModes.Absolute | KvmInputModes.Captured,
        ColorDepths: ImmutableArray.Create<byte>(3, 2, 1, 0));

    public byte[] BuildConnectPayload(
        byte bladeNumber,
        byte colorDepth,
        bool reconnect = false,
        ReadOnlySpan<byte> reconnectKey = default) =>
        KvmCommandBuilder.ConnectBlade(bladeNumber, colorDepth, reconnect ? reconnectKey : default);
}
