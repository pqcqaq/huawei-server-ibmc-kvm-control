using System.Runtime.CompilerServices;
using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Protocol.Transport;

public interface IKvmPacketConnection : IAsyncDisposable
{
    Task Completion { get; }

    ValueTask SendPacketAsync(
        int codeKey,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> encodedPacket,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<LegacyPacket> ReadPacketsAsync(
        CancellationToken cancellationToken = default);
}
