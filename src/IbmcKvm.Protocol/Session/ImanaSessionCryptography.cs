using System.Security.Cryptography;
using System.Text;

namespace IbmcKvm.Protocol.Session;

public sealed class ImanaSessionCryptography : IKvmPayloadCryptography
{
    private readonly object gate = new();
    private readonly byte[] sessionId;
    private readonly byte[] dataKey;
    private readonly byte[] inputKey;
    private readonly byte[] iv;
    private int disposed;

    private ImanaSessionCryptography(int codeKey)
    {
        var password = Encoding.UTF8.GetBytes(codeKey.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var salt = new byte[16];
        byte[]? complete = null;
        try
        {
            complete = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                ImanaSessionMaterial.Iterations,
                HashAlgorithmName.SHA1,
                72);
            sessionId = complete[..24];
            dataKey = ReverseWords(complete.AsSpan(24, 16));
            inputKey = ReverseWords(complete.AsSpan(40, 16));
            iv = ReverseWords(complete.AsSpan(56, 16));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(salt);
            if (complete is not null)
            {
                CryptographicOperations.ZeroMemory(complete);
            }
        }
    }

    public static ImanaSessionCryptography FromCodeKey(int codeKey)
    {
        if (codeKey is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(codeKey));
        }

        return new ImanaSessionCryptography(codeKey);
    }

    public ReadOnlyMemory<byte> SessionId
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return sessionId.ToArray();
            }
        }
    }

    public bool IsSessionEstablished
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return true;
            }
        }
    }

    public byte[] EncryptInput(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty || input.Length > 16)
        {
            throw new ArgumentException("An iMana input report must contain between 1 and 16 bytes.", nameof(input));
        }

        lock (gate)
        {
            ThrowIfDisposed();
            return TransformPadded(input, inputKey, iv, encrypt: true);
        }
    }

    public byte[] DecryptData(ReadOnlySpan<byte> ciphertext, int realLength)
    {
        if (ciphertext.IsEmpty || ciphertext.Length % 16 != 0 || realLength < 1 || realLength > ciphertext.Length)
        {
            throw new InvalidDataException("The iMana encrypted data length is invalid.");
        }

        lock (gate)
        {
            ThrowIfDisposed();
            var plaintext = Transform(ciphertext, dataKey, iv, encrypt: false);
            try
            {
                return plaintext[..realLength];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public byte[] EncryptPower(KvmPowerAction action)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            Span<byte> plaintext = stackalloc byte[16];
            plaintext[15] = (byte)action;
            return Transform(plaintext, dataKey, iv, encrypt: true);
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

            CryptographicOperations.ZeroMemory(sessionId);
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(inputKey);
            CryptographicOperations.ZeroMemory(iv);
        }

        GC.SuppressFinalize(this);
    }

    private static byte[] TransformPadded(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        bool encrypt)
    {
        var padded = new byte[checked((plaintext.Length + 15) / 16 * 16)];
        plaintext.CopyTo(padded);
        try
        {
            return Transform(padded, key, iv, encrypt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(padded);
        }
    }

    private static byte[] Transform(
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        bool encrypt)
    {
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

    private static byte[] ReverseWords(ReadOnlySpan<byte> source)
    {
        var result = source.ToArray();
        for (var offset = 0; offset < result.Length; offset += 4)
        {
            result.AsSpan(offset, 4).Reverse();
        }

        return result;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
