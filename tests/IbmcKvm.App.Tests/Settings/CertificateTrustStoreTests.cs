using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Text;
using IbmcKvm.App.Settings;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.App.Tests.Settings;

public sealed class CertificateTrustStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "IbmcKvm.App.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string filePath;

    public CertificateTrustStoreTests()
    {
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "trust.bin");
    }

    [Fact]
    public void ServerPinIsEncryptedAndScopedToExactHostAndPort()
    {
        var store = new CertificateTrustStore(filePath);
        var endpoint = new IbmcEndpoint("ibmc.example.test", 8443);
        using var certificate = CreateCertificate(authority: false, "CN=ibmc.example.test");
        var details = Details(certificate);

        store.TrustServer(endpoint, details);

        var resolution = store.Resolve(endpoint);
        Assert.Equal(details.Sha256Fingerprint, resolution.ServerFingerprint);
        Assert.False(store.Resolve(new IbmcEndpoint(endpoint.Host, 443)).HasTrust);
        Assert.Equal(
            -1,
            File.ReadAllBytes(filePath).AsSpan().IndexOf(
                Encoding.ASCII.GetBytes(details.Sha256Fingerprint)));
    }

    [Fact]
    public void ChangedServerCertificateDoesNotMatchStoredDecision()
    {
        var store = new CertificateTrustStore(filePath);
        var endpoint = new IbmcEndpoint("ibmc.example.test", 443);
        using var previous = CreateCertificate(false, "CN=ibmc.example.test");
        using var replacement = CreateCertificate(false, "CN=ibmc.example.test");
        store.TrustServer(endpoint, Details(previous));

        var record = Assert.Single(store.Load());

        Assert.False(record.Matches(Details(replacement)));
    }

    [Fact]
    public void AuthorityCanBeResolvedAndRevokedButLeafCannotBeImportedAsCa()
    {
        var store = new CertificateTrustStore(filePath);
        var endpoint = new IbmcEndpoint("ibmc.example.test", 443);
        using var authority = CreateCertificate(true, "CN=Private CA");
        using var leaf = CreateCertificate(false, "CN=Leaf");

        var record = store.TrustAuthority(endpoint, authority.Export(X509ContentType.Cert));

        Assert.Single(store.Resolve(endpoint).AuthorityCertificates);
        Assert.Throws<InvalidDataException>(() =>
            store.TrustAuthority(endpoint, leaf.Export(X509ContentType.Cert)));
        Assert.True(store.Revoke(record.Id));
        Assert.False(store.Resolve(endpoint).HasTrust);
        Assert.False(store.Revoke(record.Id));
    }

    [Fact]
    public void CorruptionFallsBackToStrictTrustAndRemovesInvalidStore()
    {
        File.WriteAllBytes(filePath, [1, 2, 3, 4]);
        var store = new CertificateTrustStore(filePath);

        Assert.Empty(store.Load());
        Assert.False(File.Exists(filePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static X509Certificate2 CreateCertificate(bool authority, string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subject,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        if (authority)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        }

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
    }

    private static ServerCertificateDetails Details(X509Certificate2 certificate) => new(
        certificate.Subject,
        certificate.Issuer,
        certificate.NotBefore,
        certificate.NotAfter,
        CertificateFingerprint.GetSha256(certificate),
        System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,
        certificate.Export(X509ContentType.Cert));
}
