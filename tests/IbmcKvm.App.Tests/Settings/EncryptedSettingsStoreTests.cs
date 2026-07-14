using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IbmcKvm.App.Settings;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.App.Tests.Settings;

public sealed class EncryptedSettingsStoreTests : IDisposable
{
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        "IbmcKvm.App.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string filePath;

    public EncryptedSettingsStoreTests()
    {
        Directory.CreateDirectory(directoryPath);
        filePath = Path.Combine(directoryPath, "connection-settings.bin");
    }

    [Fact]
    public void DefaultPathIsUnderTheCurrentUsersLocalApplicationData()
    {
        var store = new EncryptedSettingsStore();
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IbmcKvm",
            "connection-settings.bin");

        Assert.Equal(Path.GetFullPath(expected), store.FilePath);
    }

    [Fact]
    public void ConnectionSettingsStringRepresentationRedactsThePassword()
    {
        var settings = CreateSettings(password: "must-not-appear");

        var description = settings.ToString();

        Assert.DoesNotContain(settings.Password, description, StringComparison.Ordinal);
        Assert.Contains("<redacted>", description, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAndLoadRoundTripsAllConnectionSettings()
    {
        var store = new EncryptedSettingsStore(filePath);
        var expected = CreateSettings();

        store.Save(expected);
        var actual = store.Load();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SaveDoesNotWriteThePasswordAsPlaintext()
    {
        var store = new EncryptedSettingsStore(filePath);
        var settings = CreateSettings(password: "unique-test-password-9EBD3FC8");

        store.Save(settings);
        var encrypted = File.ReadAllBytes(filePath);
        var passwordBytes = Encoding.UTF8.GetBytes(settings.Password);

        var passwordOffset = encrypted.AsSpan().IndexOf(passwordBytes);
        Assert.Equal(-1, passwordOffset);
    }

    [Fact]
    public void LoadUsesCurrentUserDpapiProtection()
    {
        var store = new EncryptedSettingsStore(filePath);
        var settings = CreateSettings();
        store.Save(settings);
        var encrypted = File.ReadAllBytes(filePath);

        var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            Assert.Contains(settings.UserName, Encoding.UTF8.GetString(plaintext), StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

    }

    [Fact]
    public void LoadDeletesCorruptSettingsAndReturnsNull()
    {
        File.WriteAllBytes(filePath, [0x01, 0x02, 0x03, 0x04]);
        var store = new EncryptedSettingsStore(filePath);

        var settings = store.Load();

        Assert.Null(settings);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void RepeatedSaveAtomicallyReplacesThePreviousFile()
    {
        var store = new EncryptedSettingsStore(filePath);
        store.Save(CreateSettings(userName: "first-user"));
        var expected = CreateSettings(userName: "second-user", connectionMode: ConnectionMode.Exclusive);

        store.Save(expected);

        Assert.Equal(expected, store.Load());
        Assert.Equal(filePath, Assert.Single(Directory.EnumerateFiles(directoryPath)));
    }

    [Fact]
    public void DeleteRemovesSavedSettingsAndIsIdempotent()
    {
        var store = new EncryptedSettingsStore(filePath);
        store.Save(CreateSettings());

        Assert.True(store.Delete());
        Assert.False(File.Exists(filePath));
        Assert.True(store.Delete());
    }

    [Fact]
    public void LoadDeletesAnUnsupportedVersionAndReturnsNull()
    {
        var store = new EncryptedSettingsStore(filePath);
        store.Save(CreateSettings());
        RewriteVersion(99);

        var settings = store.Load();

        Assert.Null(settings);
        Assert.False(File.Exists(filePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private void RewriteVersion(int version)
    {
        var encrypted = File.ReadAllBytes(filePath);
        var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        byte[]? rewrittenPlaintext = null;
        try
        {
            var document = JsonNode.Parse(plaintext)
                ?? throw new InvalidOperationException("The saved settings JSON was empty.");
            document["version"] = version;
            rewrittenPlaintext = Encoding.UTF8.GetBytes(document.ToJsonString());
            var rewrittenEncrypted = ProtectedData.Protect(
                rewrittenPlaintext,
                null,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(filePath, rewrittenEncrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (rewrittenPlaintext is not null)
            {
                CryptographicOperations.ZeroMemory(rewrittenPlaintext);
            }
        }
    }

    private static ConnectionSettings CreateSettings(
        string userName = "test-user",
        string password = "test-password",
        ConnectionMode connectionMode = ConnectionMode.Shared) =>
        new(
            "https://192.0.2.10",
            userName,
            password,
            connectionMode,
            TrustSelfSignedCertificate: true,
            RememberSettings: true);
}
