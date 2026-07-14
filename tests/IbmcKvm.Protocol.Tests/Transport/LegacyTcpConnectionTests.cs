using System.Net;
using System.Net.Sockets;
using IbmcKvm.Protocol.Transport;
using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Protocol.Tests.Transport;

public sealed class LegacyTcpConnectionTests
{
    [Fact]
    public async Task ReceivesFragmentedFrameAndSendsEncodedPacketWithoutBlockingCaller()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connection = await LegacyTcpConnection.ConnectAsync(
            "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        listener.Stop();

        var frame = BuildIncoming(0x08, 0x01, 0x00);
        await server.GetStream().WriteAsync(frame.AsMemory(0, 3));
        await server.GetStream().WriteAsync(frame.AsMemory(3));

        using var receiveCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var packets = connection.ReadPacketsAsync(receiveCancellation.Token).GetAsyncEnumerator();
        Assert.True(await packets.MoveNextAsync());
        var packet = packets.Current;
        Assert.Equal(0x08, packet.Command);
        Assert.True(packet.IsCrcValid);

        await connection.SendPacketAsync(0x01020304, new byte[] { 0x0B });
        var encoded = await ReadExactlyAsync(server.GetStream(), 11, receiveCancellation.Token);
        Assert.Equal(LegacyPacketEncoder.Encode(0x01020304, new byte[] { 0x0B }), encoded);
    }

    [Fact]
    public async Task ConsumerCancellationDoesNotBlockOrThrowFromNetworkThread()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connection = await LegacyTcpConnection.ConnectAsync(
            "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        listener.Stop();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var enumerator = connection.ReadPacketsAsync(cancellation.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
        await enumerator.DisposeAsync();
    }

    private static byte[] BuildIncoming(params byte[] payload)
    {
        var length = payload.Length + 2;
        var result = new byte[length + 4];
        result[0] = 0xFE;
        result[1] = 0xF6;
        result[2] = 0;
        result[3] = (byte)length;
        var crc = Crc16High.Compute(payload);
        result[4] = (byte)(crc >> 8);
        result[5] = (byte)crc;
        payload.CopyTo(result.AsSpan(6));
        return result;
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var output = new byte[count];
        var offset = 0;
        while (offset < output.Length)
        {
            var read = await stream.ReadAsync(output.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        return output;
    }
}
