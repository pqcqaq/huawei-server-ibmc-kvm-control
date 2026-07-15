using System.Collections.Immutable;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Protocol.Profiles;

public enum KvmProtocolKind
{
    ModernIbmc,
    LegacyIbmc,
    Imana,
}

public enum KvmWireFormat
{
    ModernCodeKey,
    ImanaSessionId,
}

[Flags]
public enum KvmInputModes
{
    None = 0,
    Relative = 1,
    Absolute = 2,
    Captured = 4,
}

public sealed record KvmProtocolCapabilities(
    bool SupportsEncryptedKvm,
    bool SupportsEncryptedVirtualMedia,
    bool SupportsReconnect,
    bool SupportsRecording,
    bool SupportsChassis,
    bool SupportsVirtualMedia,
    bool SupportsPowerControl,
    bool SupportsVideoQuality,
    KvmInputModes InputModes,
    ImmutableArray<byte> ColorDepths);

public interface IKvmProtocolProfile
{
    KvmProtocolKind Kind { get; }

    KvmWireFormat WireFormat { get; }

    KvmProtocolCapabilities Capabilities { get; }

    byte[] BuildConnectPayload(
        byte bladeNumber,
        byte colorDepth,
        bool reconnect = false,
        ReadOnlySpan<byte> reconnectKey = default);
}
