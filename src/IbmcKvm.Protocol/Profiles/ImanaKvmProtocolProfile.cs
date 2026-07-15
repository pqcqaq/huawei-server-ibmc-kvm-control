using System.Collections.Immutable;

namespace IbmcKvm.Protocol.Profiles;

public sealed class ImanaKvmProtocolProfile : IKvmProtocolProfile
{
    public static ImanaKvmProtocolProfile Instance { get; } = new();

    private ImanaKvmProtocolProfile()
    {
    }

    public KvmProtocolKind Kind => KvmProtocolKind.Imana;

    public KvmWireFormat WireFormat => KvmWireFormat.ImanaSessionId;

    public KvmProtocolCapabilities Capabilities { get; } = new(
        SupportsEncryptedKvm: true,
        SupportsEncryptedVirtualMedia: true,
        SupportsReconnect: true,
        SupportsRecording: false,
        SupportsChassis: true,
        SupportsVirtualMedia: true,
        SupportsPowerControl: true,
        SupportsVideoQuality: false,
        InputModes: KvmInputModes.Relative | KvmInputModes.Absolute | KvmInputModes.Captured,
        ColorDepths: ImmutableArray.Create<byte>(3, 2, 1, 0));

    public byte[] BuildConnectPayload(
        byte bladeNumber,
        byte colorDepth,
        bool reconnect = false,
        ReadOnlySpan<byte> reconnectKey = default)
    {
        if (bladeNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bladeNumber));
        }

        if (!reconnectKey.IsEmpty)
        {
            throw new ArgumentException("iMana reconnect state is carried by the connect flag.", nameof(reconnectKey));
        }

        return [0x06, bladeNumber, colorDepth, reconnect ? (byte)1 : (byte)0];
    }
}
