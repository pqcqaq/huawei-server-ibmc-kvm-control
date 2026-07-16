using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.DesktopSmoke;

internal enum LoopbackFailureMode
{
    None,
    ReconnectSucceeds,
    ReconnectFails,
}

internal sealed class LoopbackKvmServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentQueue<byte[]> commands = new();
    private readonly HashSet<byte> pressedLockKeys = [];
    private readonly TaskCompletionSource failureTrigger = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LoopbackFailureMode failureMode;
    private readonly byte[] reconnectToken = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();
    private readonly Task runTask;
    private TcpClient? currentClient;
    private NetworkStream? currentStream;
    private Exception? failure;
    private byte remoteLockKeys = 0x05;
    private int connectionCount;

    public LoopbackKvmServer(LoopbackFailureMode failureMode = LoopbackFailureMode.None)
    {
        this.failureMode = failureMode;
        listener.Start();
        runTask = RunAsync(lifetime.Token);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int ConnectionCount => Volatile.Read(ref connectionCount);

    public IReadOnlyList<byte[]> Commands => commands.Select(static command => command.ToArray()).ToArray();

    public Task TriggerFailureAsync()
    {
        if (failureMode == LoopbackFailureMode.None)
        {
            throw new InvalidOperationException("This loopback server was not configured for failure injection.");
        }

        failureTrigger.TrySetResult();
        currentStream?.Close();
        currentClient?.Close();
        return Task.CompletedTask;
    }

    public void ThrowIfFailed()
    {
        if (failure is not null)
        {
            throw new InvalidOperationException("The loopback KVM server failed.", failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        listener.Stop();
        currentClient?.Dispose();
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (SocketException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            writeLock.Dispose();
            lifetime.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using (var initialClient = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false))
            {
                currentClient = initialClient;
                currentStream = initialClient.GetStream();
                await CompleteHandshakeAsync(currentStream, reconnect: false, cancellationToken).ConfigureAwait(false);
                await SendReconnectTokenAsync(currentStream, cancellationToken).ConfigureAwait(false);
                await SendVideoFrameAsync(currentStream, frameNumber: 1, cancellationToken).ConfigureAwait(false);

                if (failureMode == LoopbackFailureMode.None)
                {
                    await PumpCommandsAsync(currentStream, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var pump = PumpCommandsAsync(currentStream, cancellationToken);
                await failureTrigger.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                initialClient.Dispose();
                await IgnoreExpectedDisconnectAsync(pump).ConfigureAwait(false);
            }

            currentClient = null;
            currentStream = null;
            if (failureMode == LoopbackFailureMode.ReconnectFails)
            {
                listener.Stop();
                return;
            }

            using var reconnectClient = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            currentClient = reconnectClient;
            currentStream = reconnectClient.GetStream();
            await CompleteHandshakeAsync(currentStream, reconnect: true, cancellationToken).ConfigureAwait(false);
            await SendReconnectTokenAsync(currentStream, cancellationToken).ConfigureAwait(false);
            await SendVideoFrameAsync(currentStream, frameNumber: 2, cancellationToken).ConfigureAwait(false);
            await PumpCommandsAsync(currentStream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
            // The desktop owns the session and closes the socket when its window closes.
        }
        catch (IOException) when (currentClient is null || !currentClient.Connected)
        {
            // A graceful desktop shutdown can surface as an I/O reset on Windows.
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            currentStream = null;
            currentClient = null;
        }
    }

    private async Task CompleteHandshakeAsync(
        NetworkStream stream,
        bool reconnect,
        CancellationToken cancellationToken)
    {
        byte[]? connect = null;
        byte[]? mouseMode = null;
        while (connect is null || mouseMode is null)
        {
            var payload = await ReadOutgoingPayloadAsync(stream, cancellationToken).ConfigureAwait(false);
            commands.Enqueue(payload);
            switch (payload[0])
            {
                case 0x06:
                    connect = payload;
                    break;
                case 0x24:
                    mouseMode = payload;
                    break;
            }
        }

        if (reconnect)
        {
            if (connect.Length != 133 || !connect.AsSpan(5).SequenceEqual(reconnectToken))
            {
                throw new InvalidDataException("The reconnect handshake did not contain the expected 128-byte token.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
        }

        Interlocked.Increment(ref connectionCount);
        await SendIncomingAsync(stream, [0x08, 1, 0], cancellationToken).ConfigureAwait(false);
        await SendIncomingAsync(stream, [0x25, 1, mouseMode[2]], cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpCommandsAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            var payload = await ReadOutgoingPayloadAsync(stream, cancellationToken).ConfigureAwait(false);
            commands.Enqueue(payload);
            switch (payload[0])
            {
                case 0x04:
                    await SendIncomingAsync(stream, [0x04, 1, remoteLockKeys], cancellationToken).ConfigureAwait(false);
                    break;
                case 0x03 when payload.Length >= 10:
                    ApplyKeyboardReport(payload.AsSpan(2, 8));
                    break;
                case 0x24 when payload.Length >= 3:
                    await SendIncomingAsync(stream, [0x25, 1, payload[2]], cancellationToken).ConfigureAwait(false);
                    break;
                case 0x27 when payload.Length >= 3:
                    await SendIncomingAsync(stream, [0x28, 0, payload[2]], cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private void ApplyKeyboardReport(ReadOnlySpan<byte> report)
    {
        var currentLockKeys = report[2..]
            .ToArray()
            .Where(static usage => usage is 0x39 or 0x47 or 0x53)
            .ToHashSet();
        foreach (var usage in currentLockKeys)
        {
            if (pressedLockKeys.Contains(usage))
            {
                continue;
            }

            remoteLockKeys ^= usage switch
            {
                0x53 => (byte)0x01,
                0x39 => (byte)0x02,
                _ => (byte)0x04,
            };
        }

        pressedLockKeys.Clear();
        pressedLockKeys.UnionWith(currentLockKeys);
    }

    private async Task SendReconnectTokenAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var payload = new byte[130];
        payload[0] = 0x40;
        payload[1] = 1;
        reconnectToken.CopyTo(payload.AsSpan(2));
        await SendIncomingAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendVideoFrameAsync(
        NetworkStream stream,
        byte frameNumber,
        CancellationToken cancellationToken)
    {
        var metadata = new byte[17];
        metadata[2] = frameNumber;
        BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(3, 4), 4);
        metadata[7] = 0;
        metadata[8] = 64;
        BinaryPrimitives.WriteUInt16BigEndian(metadata.AsSpan(9, 2), 64);
        metadata[16] = 7;

        var firstPayload = new byte[2 + metadata.Length];
        firstPayload[0] = 0x02;
        firstPayload[1] = 1;
        metadata.CopyTo(firstPayload.AsSpan(2));
        await SendIncomingAsync(stream, firstPayload, cancellationToken).ConfigureAwait(false);

        byte[] secondPayload = [0x02, 1, 0, 1, frameNumber, 0, 100, 128, 128];
        await SendIncomingAsync(stream, secondPayload, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendIncomingAsync(
        NetworkStream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var packet = BuildIncoming(payload);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static byte[] BuildIncoming(ReadOnlySpan<byte> payload)
    {
        var result = new byte[payload.Length + 6];
        result[0] = 0xFE;
        result[1] = 0xF6;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), checked((ushort)(payload.Length + 2)));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), Crc16High.Compute(payload));
        payload.CopyTo(result.AsSpan(6));
        return result;
    }

    private static async Task<byte[]> ReadOutgoingPayloadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = await ReadExactlyAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 2).SequenceEqual(new byte[] { 0xFE, 0xF6 }))
        {
            throw new InvalidDataException("The loopback client sent an invalid KVM packet header.");
        }

        var wireLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        var remainder = await ReadExactlyAsync(stream, 4 + wireLength, cancellationToken).ConfigureAwait(false);
        return remainder[6..];
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
            var read = await stream.ReadAsync(output.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        return output;
    }

    private static async Task IgnoreExpectedDisconnectAsync(Task pump)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }
}
