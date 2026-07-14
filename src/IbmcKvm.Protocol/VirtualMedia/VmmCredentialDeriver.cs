using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Protocol.VirtualMedia;

public sealed record VmmDerivedCredential(byte[] SessionId, byte[] DataKey, byte[] InitializationVector);

public static class VmmCredentialDeriver
{
    public const int DerivedLength = 56;

    public static VmmDerivedCredential Derive(
        ReadOnlySpan<byte> credential,
        ReadOnlySpan<byte> salt,
        KvmCipherSuite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        if (credential.Length != 20)
        {
            throw new ArgumentException("A VMM credential contains exactly 20 bytes.", nameof(credential));
        }

        if (salt.Length != 16)
        {
            throw new ArgumentException("A VMM salt contains exactly 16 bytes.", nameof(salt));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(suite.Iterations, 1);
        var hashAlgorithm = suite.Algorithm switch
        {
            1 or 2 => HashAlgorithmName.SHA1,
            3 => HashAlgorithmName.SHA256,
            _ => throw new ArgumentOutOfRangeException(nameof(suite), "The PBKDF2 suite is not supported."),
        };

        // The Java client converts the 20-byte credential to a UTF-8 String first.
        var normalizedPassword = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(credential));
        var complete = Rfc2898DeriveBytes.Pbkdf2(
            normalizedPassword,
            salt,
            suite.Iterations,
            hashAlgorithm,
            DerivedLength);
        return new VmmDerivedCredential(complete[..24], complete[24..40], complete[40..56]);
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, VmmDerivedCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (plaintext.IsEmpty)
        {
            throw new ArgumentException("The legacy VMM cipher does not encode empty data blocks.", nameof(plaintext));
        }

        var padded = new byte[checked((plaintext.Length + 15) / 16 * 16)];
        plaintext.CopyTo(padded);
        return Transform(padded, credential, encrypt: true);
    }

    public static byte[] WrapEncryptedPayload(ReadOnlySpan<byte> plaintext, VmmDerivedCredential credential)
    {
        var ciphertext = Encrypt(plaintext, credential);
        var result = new byte[4 + ciphertext.Length];
        BinaryPrimitives.WriteInt32BigEndian(result, plaintext.Length);
        ciphertext.CopyTo(result.AsSpan(4));
        return result;
    }

    public static byte[] UnwrapEncryptedPayload(ReadOnlySpan<byte> payload, VmmDerivedCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (payload.Length < 20 || (payload.Length - 4) % 16 != 0)
        {
            throw new InvalidDataException("The encrypted VMM data block has an invalid length.");
        }

        var realLength = BinaryPrimitives.ReadInt32BigEndian(payload[..4]);
        var paddedLength = payload.Length - 4;
        if (realLength < 1 || realLength > paddedLength)
        {
            throw new InvalidDataException("The encrypted VMM data block has an invalid real length.");
        }

        var plaintext = Transform(payload[4..], credential, encrypt: false);
        return plaintext[..realLength];
    }

    private static byte[] Transform(
        ReadOnlySpan<byte> input,
        VmmDerivedCredential credential,
        bool encrypt)
    {
        if (credential.DataKey.Length != 16 || credential.InitializationVector.Length != 16)
        {
            throw new ArgumentException("The VMM AES key and IV must each contain 16 bytes.", nameof(credential));
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = credential.DataKey;
        aes.IV = credential.InitializationVector;
        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(input.ToArray(), 0, input.Length);
    }
}
