using System.Security.Cryptography;
using System.Text;

namespace IbmcKvm.Protocol.Session;

public sealed class KvmSessionCryptography : IKvmPayloadCryptography
{
    public const int LoginKeyLength = 32;
    public const int ConnectAuthenticatorLength = 24;
    public const int EncryptedSessionMaterialLength = 48;
    public const int SessionMaterialLength = 48;

    private readonly object gate = new();
    private readonly byte[] userKey;
    private readonly byte[] userIv;
    private byte[]? sessionMaterial;
    private int disposed;

    private KvmSessionCryptography(ReadOnlySpan<byte> loginKey)
    {
        userKey = loginKey[..16].ToArray();
        userIv = loginKey[16..].ToArray();
    }

    public bool IsSessionEstablished
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return sessionMaterial is not null;
            }
        }
    }

    public static KvmSessionCryptography FromLoginKey(string hexadecimalKey)
    {
        if (hexadecimalKey is null || hexadecimalKey.Length != LoginKeyLength * 2)
        {
            throw new FormatException("The encrypted KVM login key must contain exactly 32 bytes of hexadecimal data.");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromHexString(hexadecimalKey);
        }
        catch (FormatException exception)
        {
            throw new FormatException("The encrypted KVM login key is not valid hexadecimal data.", exception);
        }

        try
        {
            return new KvmSessionCryptography(decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    public byte[] DeriveConnectAuthenticator(string verificationValue, KvmCipherSuite suite)
    {
        ArgumentException.ThrowIfNullOrEmpty(verificationValue);
        ArgumentNullException.ThrowIfNull(suite);
        lock (gate)
        {
            ThrowIfDisposed();
            return DeriveWordReversed(
                verificationValue,
                userIv,
                suite,
                ConnectAuthenticatorLength);
        }
    }

    public void EstablishSession(ReadOnlySpan<byte> encryptedMaterial, KvmCipherSuite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        if (encryptedMaterial.Length != EncryptedSessionMaterialLength)
        {
            throw new InvalidDataException("The encrypted KVM session material must contain exactly 48 bytes.");
        }

        lock (gate)
        {
            ThrowIfDisposed();
            var plaintext = Transform(encryptedMaterial, userKey, userIv, encrypt: false);
            byte[]? derived = null;
            try
            {
                var password = CreateJavaPassword(plaintext.AsSpan(0, 32));
                derived = DeriveWordReversed(
                    password,
                    plaintext.AsSpan(32, 16),
                    suite,
                    SessionMaterialLength);
                var previous = sessionMaterial;
                sessionMaterial = derived;
                derived = null;
                if (previous is not null)
                {
                    CryptographicOperations.ZeroMemory(previous);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (derived is not null)
                {
                    CryptographicOperations.ZeroMemory(derived);
                }
            }
        }
    }

    public byte[] EncryptInput(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty || input.Length > 16)
        {
            throw new ArgumentException("An encrypted KVM input report must contain between 1 and 16 bytes.", nameof(input));
        }

        lock (gate)
        {
            var material = GetSessionMaterial();
            return EncryptPadded(input, material.AsSpan(16, 16), material.AsSpan(32, 16));
        }
    }

    public byte[] EncryptPower(KvmPowerAction action)
    {
        lock (gate)
        {
            var material = GetSessionMaterial();
            Span<byte> plaintext = stackalloc byte[16];
            plaintext[15] = (byte)action;
            return Transform(
                plaintext,
                material.AsSpan(0, 16),
                material.AsSpan(32, 16),
                encrypt: true);
        }
    }

    public byte[] DecryptData(ReadOnlySpan<byte> ciphertext, int realLength)
    {
        if (ciphertext.IsEmpty || ciphertext.Length % 16 != 0)
        {
            throw new InvalidDataException("The encrypted KVM data length must be a positive multiple of 16 bytes.");
        }

        if (realLength < 1 || realLength > ciphertext.Length)
        {
            throw new InvalidDataException("The encrypted KVM data real length is invalid.");
        }

        lock (gate)
        {
            var material = GetSessionMaterial();
            var padded = Transform(
                ciphertext,
                material.AsSpan(0, 16),
                material.AsSpan(32, 16),
                encrypt: false);
            try
            {
                return padded.AsSpan(0, realLength).ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(padded);
            }
        }
    }

    public byte[] DecryptLoginData(ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.IsEmpty || ciphertext.Length % 16 != 0)
        {
            throw new InvalidDataException("The encrypted KVM login data length is invalid.");
        }

        lock (gate)
        {
            ThrowIfDisposed();
            return Transform(ciphertext, userKey, userIv, encrypt: false);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(userKey);
            CryptographicOperations.ZeroMemory(userIv);
            if (sessionMaterial is not null)
            {
                CryptographicOperations.ZeroMemory(sessionMaterial);
                sessionMaterial = null;
            }
        }

        GC.SuppressFinalize(this);
    }

    private byte[] GetSessionMaterial()
    {
        ThrowIfDisposed();
        return sessionMaterial ?? throw new InvalidOperationException("The encrypted KVM session key has not been established.");
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private static byte[] EncryptPadded(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv)
    {
        var paddedLength = checked((plaintext.Length + 15) / 16 * 16);
        var padded = new byte[paddedLength];
        try
        {
            plaintext.CopyTo(padded);
            return Transform(padded, key, iv, encrypt: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(padded);
        }
    }

    private static byte[] DeriveWordReversed(
        string password,
        ReadOnlySpan<byte> salt,
        KvmCipherSuite suite,
        int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(suite.Iterations, 1);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var saltBytes = salt.ToArray();
        byte[]? derived = null;
        try
        {
            derived = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                saltBytes,
                suite.Iterations,
                GetHashAlgorithm(suite.Algorithm),
                length);
            ReverseFourByteWords(derived);
            var result = derived;
            derived = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(saltBytes);
            if (derived is not null)
            {
                CryptographicOperations.ZeroMemory(derived);
            }
        }
    }

    private static HashAlgorithmName GetHashAlgorithm(byte algorithm) => algorithm switch
    {
        1 or 2 => HashAlgorithmName.SHA1,
        3 => HashAlgorithmName.SHA256,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), "The KVM PBKDF2 suite is not supported."),
    };

    private static void ReverseFourByteWords(Span<byte> value)
    {
        if (value.Length % 4 != 0)
        {
            throw new ArgumentException("The KVM key material must contain complete four-byte words.", nameof(value));
        }

        for (var offset = 0; offset < value.Length; offset += 4)
        {
            value.Slice(offset, 4).Reverse();
        }
    }

    private static string CreateJavaPassword(ReadOnlySpan<byte> bytes) =>
        string.Create(bytes.Length, bytes.ToArray(), static (characters, source) =>
        {
            try
            {
                for (var index = 0; index < source.Length; index++)
                {
                    characters[index] = unchecked((char)(sbyte)source[index]);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(source);
            }
        });

    private static byte[] Transform(
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        bool encrypt)
    {
        if (input.IsEmpty || input.Length % 16 != 0 || key.Length != 16 || iv.Length != 16)
        {
            throw new CryptographicException("The KVM AES input, key, or IV length is invalid.");
        }

        var inputBytes = input.ToArray();
        var keyBytes = key.ToArray();
        var ivBytes = iv.ToArray();
        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = keyBytes;
            aes.IV = ivBytes;
            using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
            return transform.TransformFinalBlock(inputBytes, 0, inputBytes.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes);
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(ivBytes);
        }
    }
}
