using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Protocol.Transport;

/// <summary>
/// Owns the asynchronous read/write pumps for one KVM TCP stream. Both queues
/// are bounded, and no synchronous network operation is exposed to callers.
/// </summary>
public sealed class LegacyTcpConnection : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly IAsyncDisposable? owner;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<byte[]> outbound;
    private readonly Channel<LegacyPacket> inbound;
    private readonly PacketReader packetReader;
    private readonly Task sendLoop;
    private readonly Task receiveLoop;
    private int disposed;

    private LegacyTcpConnection(Stream stream, IAsyncDisposable? owner, ushort maximumPacketLength, int queueCapacity)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException("The KVM stream must be readable and writable", nameof(stream));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);

        this.stream = stream;
        this.owner = owner;
        packetReader = new PacketReader(maximumPacketLength);
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

    public static async Task<LegacyTcpConnection> ConnectAsync(
        string host,
        int port,
        ushort maximumPacketLength = 250,
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
            return new LegacyTcpConnection(
                client.GetStream(),
                new TcpClientOwner(client),
                maximumPacketLength,
                queueCapacity);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static LegacyTcpConnection FromStream(
        Stream stream,
        bool ownsStream = false,
        ushort maximumPacketLength = 250,
        int queueCapacity = 128) =>
        new(stream, ownsStream ? new StreamOwner(stream) : null, maximumPacketLength, queueCapacity);

    public ValueTask SendPacketAsync(
        int codeKey,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        SendAsync(LegacyPacketEncoder.Encode(codeKey, payload.Span), cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> encodedPacket, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return outbound.Writer.WriteAsync(encodedPacket.ToArray(), cancellationToken);
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
                await stream.WriteAsync(packet, lifetime.Token).ConfigureAwait(false);
                await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
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
            lifetime.Cancel();
            inbound.Writer.TryComplete(error);
        }
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
