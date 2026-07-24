namespace IbmcKvm.Desktop.Platform;

internal interface ISecretStore
{
    Task<byte[]> GetOrCreateAsync(
        string key,
        int length,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
