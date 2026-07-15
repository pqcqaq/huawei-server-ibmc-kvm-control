using System.Security.Cryptography;
using System.Text;

namespace IbmcKvm.Protocol.Session;

public static class ImanaSessionMaterial
{
    public const int SessionIdLength = 24;
    public const int Iterations = 5000;

    public static byte[] DeriveSessionId(int codeKey)
    {
        if (codeKey is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(codeKey), "The iMana source protocol uses a one-digit code key.");
        }

        var password = Encoding.UTF8.GetBytes(codeKey.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var salt = new byte[16];
        try
        {
            var complete = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA1,
                72);
            try
            {
                return complete[..SessionIdLength];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(complete);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(salt);
        }
    }
}
