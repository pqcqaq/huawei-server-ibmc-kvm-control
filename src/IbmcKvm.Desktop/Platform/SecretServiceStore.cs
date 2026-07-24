using System.Security.Cryptography;
using DBus.Services.Secrets;

namespace IbmcKvm.Desktop.Platform;

internal sealed class SecretServiceStore : ISecretStore
{
    private const string ApplicationAttribute = "application";
    private const string KeyAttribute = "key";
    private const string ApplicationName = "ibmc-kvm";

    public async Task<byte[]> GetOrCreateAsync(
        string key,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        cancellationToken.ThrowIfCancellationRequested();

        var service = await SecretService.ConnectAsync(EncryptionType.Dh).ConfigureAwait(false);
        var collection = await service.GetDefaultCollectionAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The desktop Secret Service has no default collection. Unlock or configure GNOME Keyring and retry.");
        if (await collection.IsLockedAsync().ConfigureAwait(false))
        {
            await collection.UnlockAsync().ConfigureAwait(false);
            if (await collection.IsLockedAsync().ConfigureAwait(false))
            {
                throw new UnauthorizedAccessException("The GNOME Keyring collection could not be unlocked.");
            }
        }

        var attributes = CreateAttributes(key);
        var items = await collection.SearchItemsAsync(attributes).ConfigureAwait(false);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await item.IsLockedAsync().ConfigureAwait(false))
            {
                await item.UnlockAsync().ConfigureAwait(false);
                if (await item.IsLockedAsync().ConfigureAwait(false))
                {
                    continue;
                }
            }

            var value = await item.GetSecretAsync().ConfigureAwait(false);
            if (value.Length == length)
            {
                return value;
            }

            CryptographicOperations.ZeroMemory(value);
            await item.DeleteAsync().ConfigureAwait(false);
        }

        var secret = RandomNumberGenerator.GetBytes(length);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var created = await collection.CreateItemAsync(
                "iBMC KVM settings key",
                attributes,
                secret,
                "application/octet-stream",
                replace: true).ConfigureAwait(false);
            if (created is null)
            {
                throw new InvalidOperationException("GNOME Keyring did not create the iBMC KVM settings key.");
            }

            return secret.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        var service = await SecretService.ConnectAsync(EncryptionType.Dh).ConfigureAwait(false);
        var collection = await service.GetDefaultCollectionAsync().ConfigureAwait(false);
        if (collection is null)
        {
            return;
        }

        foreach (var item in await collection.SearchItemsAsync(CreateAttributes(key)).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await item.DeleteAsync().ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> CreateAttributes(string key) => new(StringComparer.Ordinal)
    {
        [ApplicationAttribute] = ApplicationName,
        [KeyAttribute] = key,
    };
}
