using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IbmcKvm.Core.Session;
using IbmcKvm.Protocol.Session;
using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Core.Tests.Session;

public sealed class KvmClientSessionTests
{
    [Fact]
    public async Task RejectsTheSessionWhenAbsoluteMouseModeIsNotConfirmed()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(
            listener,
            (stream, cancellationToken) => CompleteHandshakeAsync(
                stream,
                cancellationToken,
                confirmedMouseMode: KvmMouseMode.Relative),
            timeout.Token);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KvmClientSession.ConnectAsync(
                new KvmConnectionOptions("127.0.0.1", GetPort(listener), 0x01020304),
                timeout.Token));

        Assert.Contains("absolute mouse mode", exception.Message, StringComparison.OrdinalIgnoreCase);
        await serverTask;
    }

    [Fact]
    public async Task SendsEncryptedKeyboardAndAbsoluteMouseForTheModernSession()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            byte[]? keyboard = null;
            byte[]? mouse = null;
            while (keyboard is null || mouse is null)
            {
                var payload = await ReadOutgoingPayloadAsync(stream, cancellationToken);
                if (payload[0] == 0x03)
                {
                    keyboard = payload;
                }
                else if (payload[0] == 0x05)
                {
                    mouse = payload;
                }
            }

            Assert.Equal(
                Convert.FromHexString("03015B2D00AAA634E6CF0E5F37283142DFB8"),
                keyboard);
            Assert.Equal(Convert.FromHexString("0501030BB805DCFF"), mouse);
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", GetPort(listener), 0x01020304),
            timeout.Token);

        await session.SendKeyboardAsync(Convert.FromHexString("05004C0000000000"), timeout.Token);
        await session.SendAbsoluteMouseAsync(3, 3000, 1500, -1, timeout.Token);
        await serverTask;
    }

    [Fact]
    public async Task QueriesVirtualMediaCredentialSaltAndPort()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            var commands = await ReadUntilVirtualMediaQueryAsync(stream, cancellationToken);
            Assert.Contains((byte)0x31, commands);
            Assert.Contains((byte)0x35, commands);

            var credential = new byte[38];
            credential[0] = 0x32;
            for (var index = 0; index < 20; index++)
            {
                credential[index + 2] = (byte)index;
            }

            for (var index = 0; index < 16; index++)
            {
                credential[index + 22] = (byte)(0xA0 + index);
            }

            await stream.WriteAsync(BuildIncoming(credential), cancellationToken);
            await stream.WriteAsync(BuildIncoming(0x36, 0, 0x34, 0x12), cancellationToken);
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", GetPort(listener), 0x01020304),
            timeout.Token);
        var endpoint = await session.GetVirtualMediaEndpointAsync(cancellationToken: timeout.Token);

        Assert.Equal(0x1234, endpoint.Port);
        Assert.Equal(Enumerable.Range(0, 20).Select(static value => (byte)value), endpoint.Credential.ToArray());
        Assert.Equal(Enumerable.Range(0, 16).Select(static value => (byte)(0xA0 + value)), endpoint.Salt.ToArray());
        Assert.Equal(new(1, 5000), endpoint.CipherSuite);
        await serverTask;
    }

    [Fact]
    public async Task ReportsPrivilegeDenial()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            await ReadUntilVirtualMediaQueryAsync(stream, cancellationToken);
            await stream.WriteAsync(BuildIncoming(0x51, 0, 2), cancellationToken);
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", GetPort(listener), 7),
            timeout.Token);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            session.GetVirtualMediaEndpointAsync(cancellationToken: timeout.Token));
        Assert.Contains("state 2", exception.Message, StringComparison.Ordinal);
        await serverTask;
    }

    [Fact]
    public async Task ConvertsNegotiationTimeoutWithoutFaultingKvmSession()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            await ReadUntilVirtualMediaQueryAsync(stream, cancellationToken);
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", GetPort(listener), 11),
            timeout.Token);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            session.GetVirtualMediaEndpointAsync(TimeSpan.FromMilliseconds(50), timeout.Token));
        Assert.Equal(KvmSessionState.Connected, session.State);
        await serverTask;
    }

    private static TcpListener StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int GetPort(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static async Task RunServerAsync(
        TcpListener listener,
        Func<NetworkStream, CancellationToken, Task> body,
        CancellationToken cancellationToken)
    {
        using (listener)
        using (var client = await listener.AcceptTcpClientAsync(cancellationToken))
        {
            await body(client.GetStream(), cancellationToken);
        }
    }

    private static async Task CompleteHandshakeAsync(
        NetworkStream stream,
        CancellationToken cancellationToken,
        KvmMouseMode confirmedMouseMode = KvmMouseMode.Absolute)
    {
        byte[]? connect = null;
        byte[]? mouseMode = null;
        while (connect is null || mouseMode is null)
        {
            var payload = await ReadOutgoingPayloadAsync(stream, cancellationToken);
            if (payload[0] == 0x06)
            {
                connect = payload;
            }
            else if (payload[0] == 0x24)
            {
                mouseMode = payload;
            }
        }

        Assert.Equal(new byte[] { 0x24, 0, 1, 0, 0 }, mouseMode);
        await stream.WriteAsync(BuildIncoming(0x08, 1, 0), cancellationToken);
        await stream.WriteAsync(BuildIncoming(0x25, 1, (byte)confirmedMouseMode), cancellationToken);
    }

    private static async Task<IReadOnlyList<byte>> ReadUntilVirtualMediaQueryAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var commands = new List<byte>();
        while (!commands.Contains(0x31) || !commands.Contains(0x35))
        {
            commands.Add((await ReadOutgoingPayloadAsync(stream, cancellationToken))[0]);
        }

        return commands;
    }

    private static async Task ReadUntilCommandAsync(
        NetworkStream stream,
        byte command,
        CancellationToken cancellationToken)
    {
        while ((await ReadOutgoingPayloadAsync(stream, cancellationToken))[0] != command)
        {
        }
    }

    private static async Task<byte[]> ReadOutgoingPayloadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactlyAsync(stream, 4, cancellationToken);
        Assert.Equal(new byte[] { 0xFE, 0xF6 }, header[..2]);
        var wireLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        var remainder = await ReadExactlyAsync(stream, 4 + wireLength, cancellationToken);
        return remainder[6..];
    }

    private static byte[] BuildIncoming(params byte[] payload)
    {
        var result = new byte[payload.Length + 6];
        result[0] = 0xFE;
        result[1] = 0xF6;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), checked((ushort)(payload.Length + 2)));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), Crc16High.Compute(payload));
        payload.CopyTo(result.AsSpan(6));
        return result;
    }

    private static async Task<byte[]> ReadExactlyAsync(
        Stream stream,
        int count,
        CancellationToken cancellationToken)
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
