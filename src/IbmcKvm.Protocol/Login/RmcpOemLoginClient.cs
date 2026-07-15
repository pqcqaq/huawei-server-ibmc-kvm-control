using System.Security.Cryptography;
using IbmcKvm.Protocol.Profiles;

namespace IbmcKvm.Protocol.Login;

public interface IRmcpOemTransport
{
    ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(
        string host,
        int port,
        string userName,
        ReadOnlyMemory<char> password,
        RmcpOemCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record RmcpOemLoginResult(
    IKvmProtocolProfile Profile,
    int CodeKey,
    bool KvmEncrypted,
    bool VirtualMediaEncrypted,
    int KvmPort,
    int VirtualMediaPort,
    int Privilege,
    string? LoginDecryptionKey);

public sealed class RmcpOemLoginClient(IRmcpOemTransport transport)
{
    private const int AdministratorPrivilege = 4;

    public async Task<RmcpOemLoginResult> LoginAsync(
        string host,
        int port,
        string userName,
        ReadOnlyMemory<char> password,
        ConnectionMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        if (password.IsEmpty)
        {
            throw new ArgumentException("The RMCP+ password cannot be empty.", nameof(password));
        }

        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var codeKey = RandomNumberGenerator.GetInt32(0, 10);
        var login = RmcpOemCommandCodec.BuildLogin(userName, mode, codeKey);
        await transport.ExecuteAsync(host, port, userName, password, login.Command, cancellationToken)
            .ConfigureAwait(false);

        var deviceId = await transport.ExecuteAsync(
            host,
            port,
            userName,
            password,
            RmcpOemCommandCodec.DeviceId(),
            cancellationToken).ConfigureAwait(false);
        var profile = IbmcProtocolDiscovery.SelectRmcp(
            RmcpOemCommandCodec.ParseFirmwareRevision(deviceId.Span));

        var kvmEncryption = await ExecuteAsync(
            host,
            port,
            userName,
            password,
            RmcpOemCommandCodec.EncryptionInfo(virtualMedia: false),
            cancellationToken).ConfigureAwait(false);
        var virtualMediaEncryption = await ExecuteAsync(
            host,
            port,
            userName,
            password,
            RmcpOemCommandCodec.EncryptionInfo(virtualMedia: true),
            cancellationToken).ConfigureAwait(false);
        var kvmPort = await ExecuteAsync(
            host,
            port,
            userName,
            password,
            RmcpOemCommandCodec.KvmPort(profile.Kind),
            cancellationToken).ConfigureAwait(false);
        var virtualMediaPort = await ExecuteAsync(
            host,
            port,
            userName,
            password,
            RmcpOemCommandCodec.VirtualMediaPort(profile.Kind),
            cancellationToken).ConfigureAwait(false);

        return new RmcpOemLoginResult(
            profile,
            codeKey,
            RmcpOemCommandCodec.ParseEncryptionFlag(kvmEncryption.Span),
            RmcpOemCommandCodec.ParseEncryptionFlag(virtualMediaEncryption.Span),
            RmcpOemCommandCodec.ParsePort(kvmPort.Span, profile.Kind),
            RmcpOemCommandCodec.ParsePort(virtualMediaPort.Span, profile.Kind),
            AdministratorPrivilege,
            profile.Kind == KvmProtocolKind.LegacyIbmc ? new string('0', 64) : null);
    }

    private ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(
        string host,
        int port,
        string userName,
        ReadOnlyMemory<char> password,
        RmcpOemCommand command,
        CancellationToken cancellationToken) =>
        transport.ExecuteAsync(host, port, userName, password, command, cancellationToken);
}
