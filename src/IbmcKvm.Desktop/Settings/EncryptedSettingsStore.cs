using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using IbmcKvm.Desktop.Platform;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Desktop.Settings;

internal sealed class EncryptedSettingsStore
{
    private const int CurrentVersion = 1;
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = 8;
    private const int MaximumEncryptedFileLength = 64 * 1024;
    private const string KeyName = "connection-settings-v1";
    private static readonly byte[] Magic = "IBKS"u8.ToArray();
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ISecretStore secretStore;
    private readonly string filePath;

    public EncryptedSettingsStore(ISecretStore secretStore)
        : this(
            secretStore,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IbmcKvm",
                "connection-settings.bin"))
    {
    }

    internal EncryptedSettingsStore(ISecretStore secretStore, string filePath)
    {
        this.secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = Path.GetFullPath(filePath);
    }

    internal string FilePath => filePath;

    public async Task<ConnectionSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return null;
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
                return null;
            }

            encrypted = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (encrypted.Length != fileLength || !HasValidHeader(encrypted))
            {
                DeleteFile();
                return null;
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

            var settings = JsonSerializer.Deserialize<ConnectionSettings>(plaintext, SerializerOptions);
            if (!IsValid(settings))
            {
                DeleteFile();
                return null;
            }

            return settings;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or
                                               UnauthorizedAccessException or NotSupportedException)
        {
            DeleteFile();
            return null;
        }
        finally
        {
            Zero(encrypted);
            Zero(plaintext);
            Zero(key);
        }
    }

    public async Task SaveAsync(ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValid(settings))
        {
            throw new ArgumentException("Connection settings are incomplete or invalid.", nameof(settings));
        }

        var directoryPath = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The settings path has no parent directory.");
        Directory.CreateDirectory(directoryPath);
        SetUnixMode(directoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        byte[]? plaintext = null;
        byte[]? key = null;
        byte[]? encrypted = null;
        var temporaryPath = Path.Combine(directoryPath, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);
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

    public bool Delete() => DeleteFile();

    private bool DeleteFile() => TryDeleteFile(filePath);

    private static bool HasValidHeader(ReadOnlySpan<byte> encrypted) =>
        encrypted[..Magic.Length].SequenceEqual(Magic) &&
        BinaryPrimitives.ReadInt32LittleEndian(encrypted.Slice(Magic.Length, sizeof(int))) == CurrentVersion;

    private static bool IsValid(ConnectionSettings? settings) => settings is
    {
        Host.Length: > 0 and <= 2048,
        UserName.Length: > 0 and <= 1024,
        Password.Length: > 0 and <= 4096,
        RememberSettings: true,
    } &&
        !string.IsNullOrWhiteSpace(settings.Host) &&
        !string.IsNullOrWhiteSpace(settings.UserName) &&
        settings.ConnectionMode is ConnectionMode.Shared or ConnectionMode.Exclusive;

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

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

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
