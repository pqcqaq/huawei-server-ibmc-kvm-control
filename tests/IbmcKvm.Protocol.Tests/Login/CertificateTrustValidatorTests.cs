using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Protocol.Tests.Login;

public sealed class CertificateTrustValidatorTests
{
    [Fact]
    public void AcceptsAValidLeafFromTheExplicitCustomAuthority()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            "CN=Test Root",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        using var root = rootRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            "CN=ibmc.example.test",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var serial = RandomNumberGenerator.GetBytes(16);
        using var unsignedLeaf = leafRequest.Create(
            root,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            serial);
        using var leaf = unsignedLeaf.CopyWithPrivateKey(leafKey);

        var trusted = CertificateTrustValidator.IsTrustedByCustomAuthority(
            leaf,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            [root.Export(X509ContentType.Cert)]);

        Assert.True(trusted);
    }

    [Fact]
    public void NeverOverridesHostNameMismatchOrAcceptsALeafAsAnAuthority()
    {
        using var certificate = CreateSelfSignedLeaf();
        var encoded = certificate.Export(X509ContentType.Cert);

        Assert.False(CertificateTrustValidator.IsTrustedByCustomAuthority(
            certificate,
            null,
            SslPolicyErrors.RemoteCertificateNameMismatch,
            [encoded]));
        Assert.False(CertificateTrustValidator.IsTrustedByCustomAuthority(
            certificate,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            [encoded]));
    }

    private static X509Certificate2 CreateSelfSignedLeaf()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=leaf.example.test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
    }
}
