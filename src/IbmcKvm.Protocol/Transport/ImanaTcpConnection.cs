using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using IbmcKvm.Protocol.Session;
using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Protocol.Transport;

public sealed class ImanaTcpConnection : IKvmPacketConnection
{
    private readonly Stream stream;
    private readonly IAsyncDisposable? owner;
    private readonly bool encrypted;
    private readonly byte[] sessionId;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<byte[]> outbound;
    private readonly Channel<LegacyPacket> inbound;
    private readonly ImanaPacketReader packetReader;
    private readonly Task sendLoop;
    private readonly Task receiveLoop;
    private int disposed;

    private ImanaTcpConnection(
        Stream stream,
        IAsyncDisposable? owner,
        int codeKey,
        bool encrypted,
        byte maximumPacketLength,
        int queueCapacity)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException("The iMana stream must be readable and writable.", nameof(stream));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        this.stream = stream;
        this.owner = owner;
        this.encrypted = encrypted;
        packetReader = new ImanaPacketReader(maximumPacketLength);
        sessionId = CreateSessionId(codeKey, encrypted);
        outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        inbound = Channel.CreateBounded<LegacyPacket>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        sendLoop = RunSendLoopAsync();
        receiveLoop = RunReceiveLoopAsync();
    }

    public Task Completion => Task.WhenAll(sendLoop, receiveLoop);

    public static async Task<ImanaTcpConnection> ConnectAsync(
        string host,
        int port,
        int codeKey,
        bool encrypted,
        byte maximumPacketLength = 250,
        int queueCapacity = 128,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new ImanaTcpConnection(
                client.GetStream(),
                new TcpClientOwner(client),
                codeKey,
                encrypted,
                maximumPacketLength,
                queueCapacity);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static ImanaTcpConnection FromStream(
        Stream stream,
        int codeKey,
        bool encrypted,
        bool ownsStream = false,
        byte maximumPacketLength = 250,
        int queueCapacity = 128) =>
        new(
            stream,
            ownsStream ? new StreamOwner(stream) : null,
            codeKey,
            encrypted,
            maximumPacketLength,
            queueCapacity);

    public async ValueTask SendPacketAsync(
        int codeKey,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        var encoded = ImanaPacketEncoder.Encode(sessionId, encrypted, payload.Span);
        try
        {
            await SendAsync(encoded, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> encodedPacket,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var queued = encodedPacket.ToArray();
        try
        {
            await outbound.Writer.WriteAsync(queued, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(queued);
            throw;
        }
    }

    public async IAsyncEnumerable<LegacyPacket> ReadPacketsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var packet in inbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return packet;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        outbound.Writer.TryComplete();
        lifetime.Cancel();
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionId);
            inbound.Writer.TryComplete();
            if (owner is not null)
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }

            lifetime.Dispose();
        }
    }

    private async Task RunSendLoopAsync()
    {
        try
        {
            await foreach (var packet in outbound.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await stream.WriteAsync(packet, lifetime.Token).ConfigureAwait(false);
                    await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(packet);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            while (outbound.Reader.TryRead(out var packet))
            {
                CryptographicOperations.ZeroMemory(packet);
            }
        }
    }

    private async Task RunReceiveLoopAsync()
    {
        var receiveBuffer = new byte[16 * 1024];
        Exception? error = null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(receiveBuffer, lifetime.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                foreach (var packet in packetReader.Append(receiveBuffer.AsSpan(0, count)))
                {
                    await inbound.Writer.WriteAsync(packet, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            error = exception;
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(receiveBuffer);
            lifetime.Cancel();
            inbound.Writer.TryComplete(error);
        }
    }

    private static byte[] CreateSessionId(int codeKey, bool encrypted)
    {
        if (encrypted)
        {
            using var cryptography = ImanaSessionCryptography.FromCodeKey(codeKey);
            return cryptography.SessionId.ToArray();
        }

        var result = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result, codeKey);
        return result;
    }

    private sealed class TcpClientOwner(TcpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StreamOwner(Stream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
