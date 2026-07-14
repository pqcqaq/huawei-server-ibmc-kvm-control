using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IbmcKvm.Protocol.Login;

public static class CertificateFingerprint
{
    public static string GetSha256(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var certificate2 = new X509Certificate2(certificate);
        return Convert.ToHexString(certificate2.GetCertHash(HashAlgorithmName.SHA256));
    }

    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new FormatException("A SHA-256 certificate fingerprint must contain 64 hexadecimal digits.");
        }

        return normalized;
    }

    public static bool Matches(X509Certificate certificate, string expectedSha256)
    {
        var actual = Convert.FromHexString(GetSha256(certificate));
        var expected = Convert.FromHexString(Normalize(expectedSha256));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
