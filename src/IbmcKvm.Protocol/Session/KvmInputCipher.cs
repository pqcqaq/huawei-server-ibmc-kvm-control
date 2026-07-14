using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IbmcKvm.Protocol.Session;

public enum KvmKeyboardEncoding
{
    LegacyPlain,
    CodeKeyAes,
}

public enum KvmMouseMode : byte
{
    Relative = 0,
    Absolute = 1,
}

public static class KvmInputCipher
{
    private static readonly byte[] KeyTemplate = [1, 2, 3, 4, 5, 6, 7, 8, 1, 2, 3, 4, 5, 6, 7, 8];

    public static byte[] EncryptKeyboardReport(ReadOnlySpan<byte> report, int codeKey)
    {
        if (report.Length != 8)
        {
            throw new ArgumentException("A boot-protocol keyboard report contains 8 bytes", nameof(report));
        }

        var key = KeyTemplate.ToArray();
        var plaintext = new byte[16];
        var initializationVector = new byte[16];
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(key, codeKey);
            report.CopyTo(plaintext);
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.EncryptCbc(plaintext, initializationVector, PaddingMode.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
