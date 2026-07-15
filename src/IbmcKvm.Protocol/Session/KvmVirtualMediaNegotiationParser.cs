using System.Buffers.Binary;

namespace IbmcKvm.Protocol.Session;

public sealed record KvmVirtualMediaCredential(byte BladeNumber, byte[] Credential, byte[] Salt);

public enum KvmPrivilegeDenial
{
    None,
    Power,
    VirtualMedia,
}

public static class KvmVirtualMediaNegotiationParser
{
    public const int CredentialLength = 20;
    public const int SaltLength = 16;

    public static KvmVirtualMediaCredential ParseCredential(ReadOnlySpan<byte> payload)
    {
        const int expectedLength = 2 + CredentialLength + SaltLength;
        if (payload.Length != expectedLength || payload[0] != 0x32)
        {
            throw new InvalidDataException("The VMM credential response is malformed.");
        }

        return new KvmVirtualMediaCredential(
            payload[1],
            payload.Slice(2, CredentialLength).ToArray(),
            payload.Slice(2 + CredentialLength, SaltLength).ToArray());
    }

    public static (byte BladeNumber, int Port) ParsePort(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4 || payload[0] != 0x36)
        {
            throw new InvalidDataException("The VMM port response is malformed.");
        }

        var port = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        if (port == 0)
        {
            throw new InvalidDataException("The VMM port response contains port zero.");
        }

        return (payload[1], port);
    }

    public static (byte BladeNumber, byte State) ParsePrivilege(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3 || payload[0] != 0x51)
        {
            throw new InvalidDataException("The VMM privilege response is malformed.");
        }

        return (payload[1], payload[2]);
    }

    public static bool IsDeniedPrivilege(byte state) => state is 2 or 3;

    public static KvmPrivilegeDenial GetPrivilegeDenial(byte state) => state switch
    {
        1 => KvmPrivilegeDenial.Power,
        2 or 3 => KvmPrivilegeDenial.VirtualMedia,
        _ => KvmPrivilegeDenial.None,
    };
}
