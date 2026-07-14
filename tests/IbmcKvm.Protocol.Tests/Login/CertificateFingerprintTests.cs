using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Protocol.Tests.Login;

public sealed class CertificateFingerprintTests
{
    [Fact]
    public void NormalizesDisplayFingerprintAndMatchesCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ibmc.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint = CertificateFingerprint.GetSha256(certificate);
        var displayed = string.Join(':', Enumerable.Range(0, 32).Select(index => fingerprint.Substring(index * 2, 2)));

        Assert.Equal(fingerprint, CertificateFingerprint.Normalize(displayed));
        Assert.True(CertificateFingerprint.Matches(certificate, displayed));
    }

    [Fact]
    public void RejectsMalformedFingerprint()
    {
        Assert.Throws<FormatException>(() => CertificateFingerprint.Normalize("not-a-pin"));
    }
}
