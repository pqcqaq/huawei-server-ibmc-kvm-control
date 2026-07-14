using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using IbmcKvm.Core.Session;
using IbmcKvm.Core.VirtualMedia.Scsi;
using IbmcKvm.Protocol.VirtualMedia;

namespace IbmcKvm.Core.VirtualMedia;

public enum VirtualMediaSessionState
{
    Connecting,
    Connected,
    Faulted,
    Closed,
}

public sealed class VirtualMediaSession : IAsyncDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly VmmPacketCodec codec = new();
    private readonly VmmDerivedCredential credential;
    private readonly bool encrypted;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<VmmPacket> outbound;
    private readonly Channel<byte> acknowledgements;
    private readonly Channel<ScsiWork> scsiWork;
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private readonly Dictionary<MediaDeviceKind, IScsiCommandProcessor> processors = [];
    private readonly Dictionary<(MediaDeviceKind Kind, byte Id), MemoryStream> commandFragments = [];
    private readonly Dictionary<(MediaDeviceKind Kind, byte Id), PendingRequest> pendingRequests = [];
    private readonly Task sendLoop;
    private readonly Task receiveLoop;
    private readonly Task heartbeatLoop;
    private readonly Task scsiLoop;
    private long lastReceiveTick = Environment.TickCount64;
    private int disposed;

    private VirtualMediaSession(TcpClient client, VmmDerivedCredential credential, bool encrypted)
    {
        this.client = client;
        this.credential = credential;
        this.encrypted = encrypted;
        stream = client.GetStream();
        outbound = Channel.CreateBounded<VmmPacket>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        acknowledgements = Channel.CreateBounded<byte>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
        scsiWork = Channel.CreateBounded<ScsiWork>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        State = VirtualMediaSessionState.Connecting;
        sendLoop = RunSendLoopAsync();
        receiveLoop = RunReceiveLoopAsync();
        heartbeatLoop = RunHeartbeatLoopAsync();
        scsiLoop = RunScsiLoopAsync();
    }

    public VirtualMediaSessionState State { get; private set; }

    public Exception? Failure { get; private set; }

    public IReadOnlyCollection<MediaDeviceKind> MountedDevices
    {
        get
        {
            lock (processors)
            {
                return processors.Keys.ToArray();
            }
        }
    }

    public static async Task<VirtualMediaSession> ConnectAsync(
        KvmVirtualMediaEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var client = new TcpClient { NoDelay = true };
        VirtualMediaSession? session = null;
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
            var derived = VmmCredentialDeriver.Derive(
                endpoint.Credential.Span,
                endpoint.Salt.Span,
                endpoint.CipherSuite);
            session = new VirtualMediaSession(client, derived, endpoint.Encrypted);
            var localAddress = ((IPEndPoint)client.Client.LocalEndPoint!).Address.GetAddressBytes();
            await session.SendAsync(VmmPacket.Authenticate(derived.SessionId, localAddress), cancellationToken)
                .ConfigureAwait(false);
            var acknowledgement = await session.WaitForAcknowledgementAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            if (acknowledgement != 0)
            {
                throw new UnauthorizedAccessException(acknowledgement switch
                {
                    1 => "The iBMC rejected the virtual-media session identifier.",
                    2 => "The iBMC does not support this virtual-media protocol version.",
                    _ => $"The iBMC rejected virtual-media authentication (ACK {acknowledgement}).",
                });
            }

            session.State = VirtualMediaSessionState.Connected;
            return session;
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                client.Dispose();
            }

            throw;
        }
    }

    public async Task MountAsync(IRandomAccessMedia media, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (processors.ContainsKey(media.DeviceKind))
            {
                throw new InvalidOperationException($"A {media.DeviceKind} device is already mounted.");
            }

            IScsiCommandProcessor processor = media.DeviceKind switch
            {
                MediaDeviceKind.Floppy => new UfiFloppyProcessor(media),
                MediaDeviceKind.Optical => new SffOpticalProcessor(media),
                _ => throw new ArgumentOutOfRangeException(nameof(media)),
            };
            lock (processors)
            {
                processors.Add(media.DeviceKind, processor);
            }

            try
            {
                await SendAsync(VmmPacket.CreateDevice(ToVmmDevice(media.DeviceKind)), cancellationToken)
                    .ConfigureAwait(false);
                var acknowledgement = await WaitForAcknowledgementAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);
                if (acknowledgement != 0x10)
                {
                    throw new IOException(acknowledgement == 0x11
                        ? "The iBMC could not enumerate the virtual-media device."
                        : $"The iBMC rejected virtual-media device creation (ACK {acknowledgement}).");
                }
            }
            catch
            {
                lock (processors)
                {
                    processors.Remove(media.DeviceKind);
                }

                throw;
            }
        }
        finally
        {
            controlGate.Release();
        }
    }

    public async Task EjectAsync(MediaDeviceKind kind, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (processors)
            {
                if (!processors.Remove(kind))
                {
                    return;
                }
            }

            await SendAsync(VmmPacket.Close(ToVmmDevice(kind)), cancellationToken).ConfigureAwait(false);
            RemovePending(kind);
        }
        finally
        {
            controlGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (State == VirtualMediaSessionState.Connected)
            {
                await SendAsync(VmmPacket.Close(VmmDeviceType.Link), CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }

        lifetime.Cancel();
        outbound.Writer.TryComplete();
        scsiWork.Writer.TryComplete();
        client.Dispose();
        try
        {
            await Task.WhenAll(sendLoop, receiveLoop, heartbeatLoop, scsiLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (IOException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            State = VirtualMediaSessionState.Closed;
            controlGate.Dispose();
            lifetime.Dispose();
            foreach (var fragment in commandFragments.Values)
            {
                fragment.Dispose();
            }

            foreach (var pending in pendingRequests.Values)
            {
                pending.Data.Dispose();
            }
        }
    }

    private ValueTask SendAsync(VmmPacket packet, CancellationToken cancellationToken) =>
        outbound.Writer.WriteAsync(packet, cancellationToken);

    private async Task<byte> WaitForAcknowledgementAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        source.CancelAfter(timeout);
        try
        {
            return await acknowledgements.Reader.ReadAsync(source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("The iBMC did not acknowledge the virtual-media operation in time.");
        }
    }

    private async Task RunSendLoopAsync()
    {
        try
        {
            await foreach (var packet in outbound.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                var encoded = codec.Encode(packet);
                await stream.WriteAsync(encoded, lifetime.Token).ConfigureAwait(false);
                await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetFailure(exception);
        }
    }

    private async Task RunReceiveLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var packet = await codec.ReadAsync(stream, lifetime.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref lastReceiveTick, Environment.TickCount64);
                switch (packet.Type)
                {
                    case VmmPacketType.Acknowledgement:
                        acknowledgements.Writer.TryWrite(packet.Field2);
                        break;
                    case VmmPacketType.FloppyData:
                        await HandleTransferAsync(MediaDeviceKind.Floppy, packet).ConfigureAwait(false);
                        break;
                    case VmmPacketType.OpticalData:
                        await HandleTransferAsync(MediaDeviceKind.Optical, packet).ConfigureAwait(false);
                        break;
                    case VmmPacketType.Close:
                        HandleRemoteClose(packet);
                        break;
                    case VmmPacketType.Shutdown:
                        throw new IOException($"The iBMC shut down the virtual-media link (reason {packet.Field2}).");
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetFailure(exception);
        }
    }

    private async Task HandleTransferAsync(MediaDeviceKind kind, VmmPacket packet)
    {
        var key = (kind, packet.CommandId);
        var transferKind = packet.Field1 & 0x0F;
        var isEnd = (packet.Field1 >> 4) != (byte)VmmTransferState.Continue;
        if (transferKind == (byte)VmmTransferKind.Command)
        {
            if (!commandFragments.TryGetValue(key, out var fragment))
            {
                fragment = new MemoryStream(CommandLengthCapacity);
                commandFragments.Add(key, fragment);
            }

            if (fragment.Length + packet.Payload.Length > CommandLengthCapacity)
            {
                fragment.Dispose();
                commandFragments.Remove(key);
                await SendAsync(VmmPacket.Complete(ToVmmDevice(kind), 1, packet.CommandId), lifetime.Token)
                    .ConfigureAwait(false);
                return;
            }

            fragment.Write(packet.Payload);
            if (!isEnd)
            {
                return;
            }

            var command = fragment.ToArray();
            fragment.Dispose();
            commandFragments.Remove(key);
            if (command.Length != CommandLengthCapacity || !TryGetProcessor(kind, out var processor))
            {
                await SendAsync(VmmPacket.Complete(ToVmmDevice(kind), 1, packet.CommandId), lifetime.Token)
                    .ConfigureAwait(false);
                return;
            }

            var expected = processor.GetExpectedDataOutLength(command);
            if (expected == 0)
            {
                await scsiWork.Writer.WriteAsync(new(kind, packet.CommandId, command, []), lifetime.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                pendingRequests[key] = new PendingRequest(command, expected);
            }

            return;
        }

        if (transferKind != (byte)VmmTransferKind.Data || !pendingRequests.TryGetValue(key, out var pending))
        {
            await SendAsync(VmmPacket.Complete(ToVmmDevice(kind), 1, packet.CommandId), lifetime.Token)
                .ConfigureAwait(false);
            return;
        }

        var data = encrypted
            ? VmmCredentialDeriver.UnwrapEncryptedPayload(packet.Payload, credential)
            : packet.Payload;
        if (pending.Data.Length + data.Length > pending.ExpectedLength)
        {
            pending.Data.Dispose();
            pendingRequests.Remove(key);
            await SendAsync(VmmPacket.Complete(ToVmmDevice(kind), 1, packet.CommandId), lifetime.Token)
                .ConfigureAwait(false);
            return;
        }

        pending.Data.Write(data);
        if (!isEnd && pending.Data.Length != pending.ExpectedLength)
        {
            return;
        }

        pendingRequests.Remove(key);
        var dataOut = pending.Data.ToArray();
        pending.Data.Dispose();
        await scsiWork.Writer.WriteAsync(new(kind, packet.CommandId, pending.Command, dataOut), lifetime.Token)
            .ConfigureAwait(false);
    }

    private async Task RunScsiLoopAsync()
    {
        try
        {
            await foreach (var work in scsiWork.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (!TryGetProcessor(work.Kind, out var processor))
                {
                    await SendAsync(VmmPacket.Complete(ToVmmDevice(work.Kind), 1, work.CommandId), lifetime.Token)
                        .ConfigureAwait(false);
                    continue;
                }

                var response = await processor.ProcessAsync(work.Command, work.DataOut, lifetime.Token)
                    .ConfigureAwait(false);
                await SendScsiResponseAsync(work.Kind, work.CommandId, response).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetFailure(exception);
        }
    }

    private async Task SendScsiResponseAsync(MediaDeviceKind kind, byte commandId, ScsiResponse response)
    {
        var chunkSize = kind == MediaDeviceKind.Floppy ? 4096 : 32768;
        var offset = 0;
        while (offset < response.Data.Length)
        {
            var count = Math.Min(chunkSize, response.Data.Length - offset);
            var state = offset + count == response.Data.Length
                ? VmmTransferState.End
                : VmmTransferState.Continue;
            var payload = encrypted
                ? VmmCredentialDeriver.WrapEncryptedPayload(response.Data.AsSpan(offset, count), credential)
                : response.Data.AsSpan(offset, count).ToArray();
            await SendAsync(
                VmmPacket.Data(ToVmmDevice(kind), VmmTransferKind.Data, state, commandId, payload),
                lifetime.Token).ConfigureAwait(false);
            offset += count;
        }

        await SendAsync(
            VmmPacket.Complete(ToVmmDevice(kind), response.Success ? (byte)0 : (byte)1, commandId),
            lifetime.Token).ConfigureAwait(false);
    }

    private async Task RunHeartbeatLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (Environment.TickCount64 - Interlocked.Read(ref lastReceiveTick) > 40_000)
                {
                    throw new TimeoutException("The iBMC virtual-media heartbeat timed out.");
                }

                await SendAsync(VmmPacket.Heartbeat(), lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetFailure(exception);
        }
    }

    private bool TryGetProcessor(MediaDeviceKind kind, out IScsiCommandProcessor processor)
    {
        lock (processors)
        {
            return processors.TryGetValue(kind, out processor!);
        }
    }

    private void HandleRemoteClose(VmmPacket packet)
    {
        var kind = (packet.Field1 >> 4) switch
        {
            1 => MediaDeviceKind.Floppy,
            2 => MediaDeviceKind.Optical,
            _ => (MediaDeviceKind?)null,
        };
        if (kind is null)
        {
            throw new IOException($"The iBMC closed the virtual-media link (reason {packet.Field2}).");
        }

        lock (processors)
        {
            processors.Remove(kind.Value);
        }

        RemovePending(kind.Value);
    }

    private void RemovePending(MediaDeviceKind kind)
    {
        foreach (var key in commandFragments.Keys.Where(key => key.Kind == kind).ToArray())
        {
            commandFragments[key].Dispose();
            commandFragments.Remove(key);
        }

        foreach (var key in pendingRequests.Keys.Where(key => key.Kind == kind).ToArray())
        {
            pendingRequests[key].Data.Dispose();
            pendingRequests.Remove(key);
        }
    }

    private void SetFailure(Exception exception)
    {
        Failure ??= exception;
        State = VirtualMediaSessionState.Faulted;
        lifetime.Cancel();
        acknowledgements.Writer.TryComplete(exception);
        outbound.Writer.TryComplete(exception);
        scsiWork.Writer.TryComplete(exception);
    }

    private static VmmDeviceType ToVmmDevice(MediaDeviceKind kind) => kind switch
    {
        MediaDeviceKind.Floppy => VmmDeviceType.Floppy,
        MediaDeviceKind.Optical => VmmDeviceType.Optical,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private const int CommandLengthCapacity = 12;

    private sealed record ScsiWork(MediaDeviceKind Kind, byte CommandId, byte[] Command, byte[] DataOut);

    private sealed class PendingRequest(byte[] command, int expectedLength)
    {
        public byte[] Command { get; } = command;
        public int ExpectedLength { get; } = expectedLength;
        public MemoryStream Data { get; } = new(expectedLength);
    }
}
