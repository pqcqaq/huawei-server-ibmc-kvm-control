using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IbmcKvm.Core.Agent;

namespace IbmcKvm.Core.Tests.Agent;

public sealed class AgentClientSessionTests
{
    [Fact]
    public async Task AuthenticatesReceivesFrameAndSendsKeyboard()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunServerAsync(listener, token, timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var options = new AgentConnectionOptions("127.0.0.1", port, Convert.ToBase64String(token), new string('0', 64));

        await using var session = await AgentClientSession.ConnectStreamAsync(
            client.GetStream(),
            owner: null,
            options,
            token,
            timeout.Token);
        await using var frames = session.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal(1u, frames.Current.Sequence);
        await session.SendKeyboardAsync(new byte[] { 0, 0, 4, 0, 0, 0, 0, 0 }, timeout.Token);

        var keyboard = await server;
        Assert.Equal("0000040000000000", Convert.ToHexString(keyboard));
    }

    [Fact]
    public async Task DisposeReleasesMouseButtonsAtLastSentPosition()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunMouseReleaseServerAsync(listener, token, timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var options = new AgentConnectionOptions("127.0.0.1", port, Convert.ToBase64String(token), new string('0', 64));
        var session = await AgentClientSession.ConnectStreamAsync(
            client.GetStream(),
            owner: null,
            options,
            token,
            timeout.Token);

        await session.SendMouseAsync(1, 1234, 4321, 0, timeout.Token);
        await session.DisposeAsync();

        var reports = await server;
        Assert.Equal(new AgentMouseReport(1, 1234, 4321, 0), reports.Pressed);
        Assert.Equal(new AgentMouseReport(0, 1234, 4321, 0), reports.Released);
    }

    private static async Task<byte[]> RunServerAsync(
        TcpListener listener,
        byte[] token,
        CancellationToken cancellationToken)
    {
        using var server = await listener.AcceptTcpClientAsync(cancellationToken);
        listener.Stop();
        var stream = server.GetStream();
        var hello = await AgentProtocol.ReadAsync(stream, cancellationToken);
        Assert.Equal(AgentMessageKind.ClientHello, hello.Kind);
        Assert.Equal(token, hello.Payload[2..]);

        var helloPayload = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(helloPayload.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(helloPayload.AsSpan(2, 2), 1);
        helloPayload[4] = 10;
        helloPayload[5] = 64;
        BinaryPrimitives.WriteUInt16BigEndian(helloPayload.AsSpan(6, 2), 7);
        await AgentProtocol.WriteAsync(
            stream,
            new AgentEnvelope(AgentMessageKind.ServerHello, helloPayload),
            cancellationToken);
        await AgentProtocol.WriteAsync(stream, BuildFrame(), cancellationToken);

        var keyboard = await AgentProtocol.ReadAsync(stream, cancellationToken);
        Assert.Equal(AgentMessageKind.Keyboard, keyboard.Kind);
        return keyboard.Payload;
    }

    private static async Task<(AgentMouseReport Pressed, AgentMouseReport Released)> RunMouseReleaseServerAsync(
        TcpListener listener,
        byte[] token,
        CancellationToken cancellationToken)
    {
        using var server = await listener.AcceptTcpClientAsync(cancellationToken);
        listener.Stop();
        var stream = server.GetStream();
        var clientHello = await AgentProtocol.ReadAsync(stream, cancellationToken);
        Assert.Equal(AgentMessageKind.ClientHello, clientHello.Kind);
        Assert.Equal(token, clientHello.Payload[2..]);

        var helloPayload = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(helloPayload.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(helloPayload.AsSpan(2, 2), 1);
        helloPayload[4] = 10;
        helloPayload[5] = 64;
        BinaryPrimitives.WriteUInt16BigEndian(helloPayload.AsSpan(6, 2), 7);
        await AgentProtocol.WriteAsync(
            stream,
            new AgentEnvelope(AgentMessageKind.ServerHello, helloPayload),
            cancellationToken);

        var pressed = ParseMouse(await AgentProtocol.ReadAsync(stream, cancellationToken));
        var keyboardRelease = await AgentProtocol.ReadAsync(stream, cancellationToken);
        Assert.Equal(AgentMessageKind.Keyboard, keyboardRelease.Kind);
        Assert.Equal(new byte[8], keyboardRelease.Payload);
        var released = ParseMouse(await AgentProtocol.ReadAsync(stream, cancellationToken));
        return (pressed, released);
    }

    private static AgentMouseReport ParseMouse(AgentEnvelope envelope)
    {
        Assert.Equal(AgentMessageKind.Mouse, envelope.Kind);
        Assert.Equal(6, envelope.Payload.Length);
        return new AgentMouseReport(
            envelope.Payload[0],
            BinaryPrimitives.ReadUInt16BigEndian(envelope.Payload.AsSpan(1, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(envelope.Payload.AsSpan(3, 2)),
            unchecked((sbyte)envelope.Payload[5]));
    }

    private static AgentEnvelope BuildFrame()
    {
        var payload = new byte[12 + 14 + 1];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 1);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), 1);
        payload[8] = 1;
        payload[9] = 64;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(16, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(18, 2), 1);
        payload[20] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(22, 4), 1);
        payload[26] = 0xff;
        return new AgentEnvelope(AgentMessageKind.Frame, payload);
    }
}
