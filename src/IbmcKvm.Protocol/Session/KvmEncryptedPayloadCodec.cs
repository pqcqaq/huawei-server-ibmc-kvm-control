using System.Buffers.Binary;

namespace IbmcKvm.Protocol.Session;

public static class KvmEncryptedPayloadCodec
{
    public static void EstablishSession(
        ReadOnlySpan<byte> response,
        byte expectedBladeNumber,
        KvmCipherSuite suite,
        KvmSessionCryptography cryptography)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(cryptography);
        if (response.Length != 2 + KvmSessionCryptography.EncryptedSessionMaterialLength ||
            response[0] != 0x40 ||
            response[1] != expectedBladeNumber)
        {
            throw new InvalidDataException("The encrypted KVM session-material response is malformed.");
        }

        cryptography.EstablishSession(response[2..], suite);
    }

    public static byte[] NormalizeVideoChunk(
        ReadOnlySpan<byte> chunk,
        IKvmPayloadCryptography cryptography)
    {
        ArgumentNullException.ThrowIfNull(cryptography);
        if (chunk.Length < 3)
        {
            throw new InvalidDataException("The encrypted KVM video chunk is shorter than its prefix.");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(chunk) == 0)
        {
            return chunk.ToArray();
        }

        if (chunk.Length < 4 + 16 || (chunk.Length - 4) % 16 != 0)
        {
            throw new InvalidDataException("The encrypted KVM video chunk has an invalid ciphertext length.");
        }

        var realLength = chunk[3];
        var plaintext = cryptography.DecryptData(chunk[4..], realLength);
        var normalized = new byte[3 + plaintext.Length];
        chunk[..3].CopyTo(normalized);
        plaintext.CopyTo(normalized.AsSpan(3));
        return normalized;
    }

    public static byte[] NormalizeVirtualMediaCredential(
        ReadOnlySpan<byte> response,
        IKvmPayloadCryptography cryptography)
    {
        const int plaintextLength = KvmVirtualMediaNegotiationParser.CredentialLength +
                                    KvmVirtualMediaNegotiationParser.SaltLength;
        return NormalizeVirtualMediaResponse(response, 0x32, plaintextLength, cryptography);
    }

    public static byte[] NormalizeVirtualMediaPort(
        ReadOnlySpan<byte> response,
        IKvmPayloadCryptography cryptography) =>
        NormalizeVirtualMediaResponse(response, 0x36, plaintextLength: 2, cryptography);

    private static byte[] NormalizeVirtualMediaResponse(
        ReadOnlySpan<byte> response,
        byte command,
        int plaintextLength,
        IKvmPayloadCryptography cryptography)
    {
        ArgumentNullException.ThrowIfNull(cryptography);
        var ciphertextLength = checked((plaintextLength + 15) / 16 * 16);
        if (response.Length != 2 + ciphertextLength || response[0] != command)
        {
            throw new InvalidDataException("The encrypted KVM virtual-media response is malformed.");
        }

        var plaintext = cryptography.DecryptData(response[2..], plaintextLength);
        var normalized = new byte[2 + plaintext.Length];
        response[..2].CopyTo(normalized);
        plaintext.CopyTo(normalized.AsSpan(2));
        return normalized;
    }
}
