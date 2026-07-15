using System.Collections.Immutable;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.App.Settings;

internal enum CertificateTrustKind
{
    ServerCertificate,
    CertificateAuthority,
}

internal sealed record CertificateTrustRecord(
    Guid Id,
    CertificateTrustKind Kind,
    string Host,
    int Port,
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    string Sha256Fingerprint,
    string CertificateDerBase64,
    DateTimeOffset CreatedAt)
{
    public string Scope => $"{Host}:{Port}";

    public bool IsActive(DateTime now) => now >= NotBefore && now <= NotAfter;

    public bool Matches(ServerCertificateDetails details) =>
        Kind == CertificateTrustKind.ServerCertificate &&
        string.Equals(
            CertificateFingerprint.Normalize(Sha256Fingerprint),
            CertificateFingerprint.Normalize(details.Sha256Fingerprint),
            StringComparison.Ordinal);
}

internal sealed record CertificateTrustResolution(
    string? ServerFingerprint,
    ImmutableArray<byte[]> AuthorityCertificates)
{
    public bool HasTrust => ServerFingerprint is not null || !AuthorityCertificates.IsEmpty;
}

internal sealed class CertificateTrustStore
{
    private const int CurrentVersion = 1;
    private const int MaximumRecords = 64;
    private const int MaximumEncryptedFileLength = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string filePath;

    public CertificateTrustStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IbmcKvm",
            "certificate-trust.bin"))
    {
    }

    internal CertificateTrustStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = Path.GetFullPath(filePath);
    }

    internal string FilePath => filePath;

    public ImmutableArray<CertificateTrustRecord> Load()
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        byte[]? plaintext = null;
        try
        {
            var length = new FileInfo(filePath).Length;
            if (length is 0 or > MaximumEncryptedFileLength)
            {
                DeleteFile();
                return [];
            }

            var encrypted = File.ReadAllBytes(filePath);
            plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var envelope = JsonSerializer.Deserialize<TrustEnvelope>(plaintext, SerializerOptions);
            if (envelope is null || envelope.Version != CurrentVersion || !TryValidate(envelope.Records))
            {
                DeleteFile();
                return [];
            }

            return envelope.Records.ToImmutableArray();
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or
                                               UnauthorizedAccessException or NotSupportedException or FormatException)
        {
            DeleteFile();
            return [];
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public CertificateTrustRecord TrustServer(IbmcEndpoint endpoint, ServerCertificateDetails details)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(details);
        using var certificate = X509CertificateLoader.LoadCertificate(details.CertificateDer.Span);
        if (!string.Equals(
                CertificateFingerprint.GetSha256(certificate),
                CertificateFingerprint.Normalize(details.Sha256Fingerprint),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The probed certificate fingerprint does not match its encoded certificate.");
        }

        return Upsert(CreateRecord(
            endpoint,
            CertificateTrustKind.ServerCertificate,
            certificate));
    }

    public CertificateTrustRecord TrustAuthority(IbmcEndpoint endpoint, ReadOnlySpan<byte> certificateDer)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (certificateDer.Length is 0 or > CertificateTrustValidator.MaximumCertificateLength)
        {
            throw new ArgumentOutOfRangeException(nameof(certificateDer));
        }

        using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
        var constraints = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .FirstOrDefault();
        if (constraints?.CertificateAuthority != true)
        {
            throw new InvalidDataException("Only a certificate-authority certificate can be imported as a custom CA.");
        }

        return Upsert(CreateRecord(
            endpoint,
            CertificateTrustKind.CertificateAuthority,
            certificate));
    }

    public CertificateTrustResolution Resolve(IbmcEndpoint endpoint, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var effectiveNow = now ?? DateTime.Now;
        var scoped = Load().Where(record => IsScope(record, endpoint) && record.IsActive(effectiveNow)).ToArray();
        var server = scoped
            .Where(record => record.Kind == CertificateTrustKind.ServerCertificate)
            .OrderByDescending(record => record.CreatedAt)
            .FirstOrDefault();
        var authorities = scoped
            .Where(record => record.Kind == CertificateTrustKind.CertificateAuthority)
            .Select(record => Convert.FromBase64String(record.CertificateDerBase64))
            .ToImmutableArray();
        return new CertificateTrustResolution(server?.Sha256Fingerprint, authorities);
    }

    public bool Revoke(Guid id)
    {
        var records = Load().ToList();
        var removed = records.RemoveAll(record => record.Id == id) > 0;
        if (!removed)
        {
            return false;
        }

        Save(records);
        return true;
    }

    private CertificateTrustRecord Upsert(CertificateTrustRecord record)
    {
        var records = Load().ToList();
        records.RemoveAll(existing =>
            existing.Kind == record.Kind &&
            IsSameScope(existing, record) &&
            (record.Kind == CertificateTrustKind.ServerCertificate ||
             string.Equals(existing.Sha256Fingerprint, record.Sha256Fingerprint, StringComparison.Ordinal)));
        records.Add(record);
        if (records.Count > MaximumRecords)
        {
            records = records.OrderByDescending(existing => existing.CreatedAt).Take(MaximumRecords).ToList();
        }

        Save(records);
        return record;
    }

    private void Save(IReadOnlyCollection<CertificateTrustRecord> records)
    {
        if (!TryValidate(records))
        {
            throw new ArgumentException("The certificate trust collection is invalid.", nameof(records));
        }

        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The trust-store path has no parent directory.");
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new TrustEnvelope(CurrentVersion, records.ToArray()),
            SerializerOptions);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporaryPath, encrypted);
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static CertificateTrustRecord CreateRecord(
        IbmcEndpoint endpoint,
        CertificateTrustKind kind,
        X509Certificate2 certificate) =>
        new(
            Guid.NewGuid(),
            kind,
            endpoint.Host,
            endpoint.HttpsPort,
            certificate.Subject,
            certificate.Issuer,
            certificate.NotBefore,
            certificate.NotAfter,
            CertificateFingerprint.GetSha256(certificate),
            Convert.ToBase64String(certificate.Export(X509ContentType.Cert)),
            DateTimeOffset.UtcNow);

    private static bool TryValidate(IReadOnlyCollection<CertificateTrustRecord>? records)
    {
        if (records is null || records.Count > MaximumRecords)
        {
            return false;
        }

        foreach (var record in records)
        {
            if (record.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(record.Host) || record.Host.Length > 255 ||
                record.Port is < 1 or > ushort.MaxValue ||
                string.IsNullOrWhiteSpace(record.Subject) || record.Subject.Length > 4096 ||
                string.IsNullOrWhiteSpace(record.Issuer) || record.Issuer.Length > 4096 ||
                record.NotAfter <= record.NotBefore ||
                record.CertificateDerBase64.Length > CertificateTrustValidator.MaximumCertificateLength * 2)
            {
                return false;
            }

            try
            {
                var encoded = Convert.FromBase64String(record.CertificateDerBase64);
                if (encoded.Length is 0 or > CertificateTrustValidator.MaximumCertificateLength)
                {
                    return false;
                }

                using var certificate = X509CertificateLoader.LoadCertificate(encoded);
                if (!string.Equals(
                        CertificateFingerprint.GetSha256(certificate),
                        CertificateFingerprint.Normalize(record.Sha256Fingerprint),
                        StringComparison.Ordinal))
                {
                    return false;
                }

                if (record.Kind == CertificateTrustKind.CertificateAuthority &&
                    certificate.Extensions.OfType<X509BasicConstraintsExtension>()
                        .FirstOrDefault()?.CertificateAuthority != true)
                {
                    return false;
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsScope(CertificateTrustRecord record, IbmcEndpoint endpoint) =>
        string.Equals(record.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
        record.Port == endpoint.HttpsPort;

    private static bool IsSameScope(CertificateTrustRecord left, CertificateTrustRecord right) =>
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private void DeleteFile()
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record TrustEnvelope(int Version, CertificateTrustRecord[] Records);
}
