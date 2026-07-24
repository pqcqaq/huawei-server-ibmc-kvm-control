using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using IbmcKvm.Desktop.Platform;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Desktop.Settings;

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
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = 8;
    private const int MaximumRecords = 64;
    private const int MaximumEncryptedFileLength = 1024 * 1024;
    private const string KeyName = "certificate-trust-v1";
    private static readonly byte[] Magic = "IBCT"u8.ToArray();
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ISecretStore secretStore;
    private readonly string filePath;

    public CertificateTrustStore(ISecretStore secretStore)
        : this(
            secretStore,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IbmcKvm",
                "certificate-trust.bin"))
    {
    }

    internal CertificateTrustStore(ISecretStore secretStore, string filePath)
    {
        this.secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = Path.GetFullPath(filePath);
    }

    public async Task<ImmutableArray<CertificateTrustRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        byte[]? encrypted = null;
        byte[]? plaintext = null;
        byte[]? key = null;
        try
        {
            var fileLength = new FileInfo(filePath).Length;
            if (fileLength is < (HeaderLength + NonceLength + TagLength + 1) or > MaximumEncryptedFileLength)
            {
                DeleteFile();
                return [];
            }

            encrypted = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (encrypted.Length != fileLength || !HasValidHeader(encrypted))
            {
                DeleteFile();
                return [];
            }

            var ciphertextLength = encrypted.Length - HeaderLength - NonceLength - TagLength;
            plaintext = new byte[ciphertextLength];
            key = await secretStore.GetOrCreateAsync(KeyName, KeyLength, cancellationToken).ConfigureAwait(false);
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Decrypt(
                    encrypted.AsSpan(HeaderLength, NonceLength),
                    encrypted.AsSpan(HeaderLength + NonceLength, ciphertextLength),
                    encrypted.AsSpan(HeaderLength + NonceLength + ciphertextLength, TagLength),
                    plaintext,
                    encrypted.AsSpan(0, HeaderLength));
            }

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
            Zero(encrypted);
            Zero(plaintext);
            Zero(key);
        }
    }

    public async Task<CertificateTrustRecord> TrustServerAsync(
        IbmcEndpoint endpoint,
        ServerCertificateDetails details,
        CancellationToken cancellationToken = default)
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

        return await UpsertAsync(
            CreateRecord(endpoint, CertificateTrustKind.ServerCertificate, certificate),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CertificateTrustRecord> TrustAuthorityAsync(
        IbmcEndpoint endpoint,
        ReadOnlyMemory<byte> certificateDer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (certificateDer.Length is 0 or > CertificateTrustValidator.MaximumCertificateLength)
        {
            throw new ArgumentOutOfRangeException(nameof(certificateDer));
        }

        using var certificate = X509CertificateLoader.LoadCertificate(certificateDer.Span);
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>()
                .FirstOrDefault()?.CertificateAuthority != true)
        {
            throw new InvalidDataException("Only a certificate-authority certificate can be imported as a custom CA.");
        }

        return await UpsertAsync(
            CreateRecord(endpoint, CertificateTrustKind.CertificateAuthority, certificate),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CertificateTrustResolution> ResolveAsync(
        IbmcEndpoint endpoint,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var effectiveNow = now ?? DateTime.Now;
        var scoped = (await LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(record => IsScope(record, endpoint) && record.IsActive(effectiveNow))
            .ToArray();
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

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var records = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (records.RemoveAll(record => record.Id == id) == 0)
        {
            return false;
        }

        await SaveAsync(records, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<CertificateTrustRecord> UpsertAsync(
        CertificateTrustRecord record,
        CancellationToken cancellationToken)
    {
        var records = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
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

        await SaveAsync(records, cancellationToken).ConfigureAwait(false);
        return record;
    }

    private async Task SaveAsync(
        IReadOnlyCollection<CertificateTrustRecord> records,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(records))
        {
            throw new ArgumentException("The certificate trust collection is invalid.", nameof(records));
        }

        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The trust-store path has no parent directory.");
        Directory.CreateDirectory(directory);
        SetUnixMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        byte[]? plaintext = null;
        byte[]? key = null;
        byte[]? encrypted = null;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new TrustEnvelope(CurrentVersion, records.ToArray()),
                SerializerOptions);
            key = await secretStore.GetOrCreateAsync(KeyName, KeyLength, cancellationToken).ConfigureAwait(false);
            encrypted = new byte[HeaderLength + NonceLength + plaintext.Length + TagLength];
            Magic.CopyTo(encrypted, 0);
            BinaryPrimitives.WriteInt32LittleEndian(encrypted.AsSpan(Magic.Length, sizeof(int)), CurrentVersion);
            RandomNumberGenerator.Fill(encrypted.AsSpan(HeaderLength, NonceLength));
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Encrypt(
                    encrypted.AsSpan(HeaderLength, NonceLength),
                    plaintext,
                    encrypted.AsSpan(HeaderLength + NonceLength, plaintext.Length),
                    encrypted.AsSpan(HeaderLength + NonceLength + plaintext.Length, TagLength),
                    encrypted.AsSpan(0, HeaderLength));
            }

            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken).ConfigureAwait(false);
            SetUnixMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, filePath, overwrite: true);
            SetUnixMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Zero(plaintext);
            Zero(key);
            Zero(encrypted);
            TryDeleteFile(temporaryPath);
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
                        StringComparison.Ordinal) ||
                    record.Kind == CertificateTrustKind.CertificateAuthority &&
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

    private static bool HasValidHeader(ReadOnlySpan<byte> encrypted) =>
        encrypted[..Magic.Length].SequenceEqual(Magic) &&
        BinaryPrimitives.ReadInt32LittleEndian(encrypted.Slice(Magic.Length, sizeof(int))) == CurrentVersion;

    private static bool IsScope(CertificateTrustRecord record, IbmcEndpoint endpoint) =>
        string.Equals(record.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
        record.Port == endpoint.HttpsPort;

    private static bool IsSameScope(CertificateTrustRecord left, CertificateTrustRecord right) =>
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private bool DeleteFile() => TryDeleteFile(filePath);

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private sealed record TrustEnvelope(int Version, CertificateTrustRecord[] Records);
}
