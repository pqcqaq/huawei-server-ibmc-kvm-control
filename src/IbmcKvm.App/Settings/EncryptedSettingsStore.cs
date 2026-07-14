using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.App.Settings;

internal sealed class EncryptedSettingsStore
{
    private const int CurrentVersion = 1;
    private const int MaximumEncryptedFileLength = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string filePath;

    public EncryptedSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IbmcKvm",
            "connection-settings.bin"))
    {
    }

    internal EncryptedSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = Path.GetFullPath(filePath);
    }

    internal string FilePath => filePath;

    public ConnectionSettings? Load()
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        byte[]? plaintext = null;
        try
        {
            var encryptedLength = new FileInfo(filePath).Length;
            if (encryptedLength is 0 or > MaximumEncryptedFileLength)
            {
                Delete();
                return null;
            }

            var encrypted = File.ReadAllBytes(filePath);
            if (encrypted.Length != encryptedLength)
            {
                Delete();
                return null;
            }

            plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var envelope = JsonSerializer.Deserialize<SettingsEnvelope>(plaintext, SerializerOptions);
            if (envelope is null || envelope.Version != CurrentVersion || !IsValid(envelope.Settings))
            {
                Delete();
                return null;
            }

            return envelope.Settings;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or
                                               UnauthorizedAccessException or NotSupportedException)
        {
            Delete();
            return null;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public void Save(ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValid(settings))
        {
            throw new ArgumentException("连接设置不完整或包含无效值。", nameof(settings));
        }

        var directoryPath = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("设置文件路径缺少父目录。");
        Directory.CreateDirectory(directoryPath);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new SettingsEnvelope(CurrentVersion, settings),
            SerializerOptions);
        var temporaryPath = Path.Combine(directoryPath, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporaryPath, encrypted);
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            TryDeleteFile(temporaryPath);
        }
    }

    public bool Delete() => TryDeleteFile(filePath);

    private static bool IsValid(ConnectionSettings? settings) =>
        settings is
        {
            Host.Length: > 0 and <= 2048,
            UserName.Length: > 0 and <= 1024,
            Password.Length: > 0 and <= 4096,
            RememberSettings: true,
        } &&
        !string.IsNullOrWhiteSpace(settings.Host) &&
        !string.IsNullOrWhiteSpace(settings.UserName) &&
        settings.ConnectionMode is ConnectionMode.Shared or ConnectionMode.Exclusive;

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

    private sealed record SettingsEnvelope(int Version, ConnectionSettings Settings);
}
