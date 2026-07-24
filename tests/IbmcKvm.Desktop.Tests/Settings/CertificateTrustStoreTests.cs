using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IbmcKvm.Desktop.Platform;
using IbmcKvm.Desktop.Settings;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Desktop.Tests.Settings;

public sealed class CertificateTrustStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"ibmc-kvm-cert-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task AuthorityTrustIsEncryptedScopedAndRevocable()
    {
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "trust.bin");
        var secretStore = new MemorySecretStore();
        var store = new CertificateTrustStore(secretStore, filePath);
        var endpoint = IbmcEndpoint.Parse("https://ibmc.example:8443");
        var otherEndpoint = IbmcEndpoint.Parse("https://other.example:8443");
        using var authority = CreateAuthorityCertificate();
        var encoded = authority.Export(X509ContentType.Cert);

        var record = await store.TrustAuthorityAsync(endpoint, encoded);

        var encrypted = await File.ReadAllBytesAsync(filePath);
        Assert.DoesNotContain(Convert.ToBase64String(encoded), Convert.ToBase64String(encrypted), StringComparison.Ordinal);
        var resolved = await store.ResolveAsync(endpoint, authority.NotBefore.AddMinutes(1));
        Assert.Single(resolved.AuthorityCertificates);
        Assert.Equal(encoded, resolved.AuthorityCertificates[0]);
        Assert.False((await store.ResolveAsync(otherEndpoint)).HasTrust);

        Assert.True(await store.RevokeAsync(record.Id));
        Assert.False((await store.ResolveAsync(endpoint)).HasTrust);
    }

    [Fact]
    public async Task InvalidCiphertextIsDiscarded()
    {
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "trust.bin");
        await File.WriteAllBytesAsync(filePath, RandomNumberGenerator.GetBytes(96));
        var store = new CertificateTrustStore(new MemorySecretStore(), filePath);

        Assert.Empty(await store.LoadAsync());
        Assert.False(File.Exists(filePath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static X509Certificate2 CreateAuthorityCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=iBMC KVM test authority",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> values = new(StringComparer.Ordinal);

        public Task<byte[]> GetOrCreateAsync(
            string key,
            int length,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!values.TryGetValue(key, out var value))
            {
                value = RandomNumberGenerator.GetBytes(length);
                values.Add(key, value);
            }

            return Task.FromResult(value.ToArray());
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
