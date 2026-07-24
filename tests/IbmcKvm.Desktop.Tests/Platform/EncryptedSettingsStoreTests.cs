using System.Security.Cryptography;
using IbmcKvm.Desktop.Platform;
using IbmcKvm.Desktop.Settings;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Desktop.Tests.Platform;

public sealed class EncryptedSettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"ibmc-kvm-{Guid.NewGuid():N}");

    [Fact]
    public async Task RoundTripKeepsPasswordOutOfFile()
    {
        var path = Path.Combine(directory, "settings.bin");
        var store = new EncryptedSettingsStore(new MemorySecretStore(), path);
        var expected = CreateSettings();

        await store.SaveAsync(expected);
        var raw = await File.ReadAllBytesAsync(path);
        var actual = await store.LoadAsync();

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(expected.Password, Convert.ToHexString(raw), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(expected.Password, System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TamperedCiphertextIsRejectedAndDeleted()
    {
        var path = Path.Combine(directory, "settings.bin");
        var store = new EncryptedSettingsStore(new MemorySecretStore(), path);
        await store.SaveAsync(CreateSettings());
        var raw = await File.ReadAllBytesAsync(path);
        raw[^17] ^= 0x40;
        await File.WriteAllBytesAsync(path, raw);

        Assert.Null(await store.LoadAsync());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DifferentKeyCannotDecryptSettings()
    {
        var path = Path.Combine(directory, "settings.bin");
        await new EncryptedSettingsStore(new MemorySecretStore(), path).SaveAsync(CreateSettings());

        var otherStore = new EncryptedSettingsStore(new MemorySecretStore(), path);

        Assert.Null(await otherStore.LoadAsync());
    }

    [Fact]
    public async Task LinuxSettingsFileUsesUserOnlyPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(directory, "settings.bin");
        await new EncryptedSettingsStore(new MemorySecretStore(), path).SaveAsync(CreateSettings());

        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ConnectionSettings CreateSettings() => new(
        "192.0.2.10",
        "Administrator",
        "correct horse battery staple",
        ConnectionMode.Shared,
        TrustSelfSignedCertificate: true,
        RememberSettings: true);

    private sealed class MemorySecretStore : ISecretStore
    {
        private byte[]? value;

        public Task<byte[]> GetOrCreateAsync(
            string key,
            int length,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value ??= RandomNumberGenerator.GetBytes(length);
            return Task.FromResult(value.ToArray());
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (value is not null)
            {
                CryptographicOperations.ZeroMemory(value);
                value = null;
            }

            return Task.CompletedTask;
        }
    }
}
