using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IbmcKvm.Core.Video;
using IbmcKvm.Protocol.Session;
using IbmcKvm.Protocol.Transport;
using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Core.Session;

public enum KvmSessionState
{
    Connecting,
    Connected,
    Streaming,
    Faulted,
    Closed,
}

public sealed record KvmConnectionOptions(
    string Host,
    int Port,
    int CodeKey,
    byte BladeNumber = 1,
    byte ColorDepth = 3,
    bool Encrypted = false,
    string? ExtendedVerifyValue = null,
    byte VirtualMediaBladeNumber = 0,
    bool VirtualMediaEncrypted = true,
    KvmKeyboardEncoding KeyboardEncoding = KvmKeyboardEncoding.CodeKeyAes);

public sealed class KvmVirtualMediaEndpoint
{
    public KvmVirtualMediaEndpoint(
        string host,
        int port,
        ReadOnlySpan<byte> credential,
        ReadOnlySpan<byte> salt,
        KvmCipherSuite cipherSuite,
        bool encrypted = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(cipherSuite);
        if (credential.Length != KvmVirtualMediaNegotiationParser.CredentialLength)
        {
            throw new ArgumentException("A VMM credential contains exactly 20 bytes.", nameof(credential));
        }

        if (salt.Length != KvmVirtualMediaNegotiationParser.SaltLength)
        {
            throw new ArgumentException("A VMM salt contains exactly 16 bytes.", nameof(salt));
        }

        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Host = host;
        Port = port;
        Credential = credential.ToArray();
        Salt = salt.ToArray();
        CipherSuite = cipherSuite;
        Encrypted = encrypted;
    }

    public string Host { get; }

    public int Port { get; }

    public ReadOnlyMemory<byte> Credential { get; }

    public ReadOnlyMemory<byte> Salt { get; }

    public KvmCipherSuite CipherSuite { get; }

    public bool Encrypted { get; }
}

public sealed record KvmSessionDiagnostics(
    KvmSessionState State,
    long PacketsReceived,
    long VideoPacketsReceived,
    long CrcErrors,
    long FrameErrors,
    byte LastCommand,
    string? LastFrameError);

public sealed class KvmClientSession : IAsyncDisposable
{
    private readonly LegacyTcpConnection connection;
    private readonly KvmConnectionOptions options;
    private readonly LegacyVideoFrameAssembler assembler = new();
    private readonly Channel<EncodedVideoFrame> frames;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task receiveLoop;
    private readonly Task heartbeatLoop;
    private readonly TaskCompletionSource<IReadOnlyList<KvmCipherSuite>> cipherSuites =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<byte> connectionState =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<KvmMouseMode> mouseModeState =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource firstVideoPacket =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<KvmVirtualMediaCredential> virtualMediaCredential =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> virtualMediaPort =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<byte> virtualMediaDenied =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim virtualMediaQueryGate = new(1, 1);
    private long lastFullFrameRequest;
    private long packetsReceived;
    private long videoPacketsReceived;
    private long crcErrors;
    private long frameErrors;
    private int lastCommand;
    private string? lastFrameError;
    private int disposed;

    private KvmClientSession(LegacyTcpConnection connection, KvmConnectionOptions options)
    {
        this.connection = connection;
        this.options = options;
        frames = Channel.CreateBounded<EncodedVideoFrame>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        SelectedCipherSuite = new KvmCipherSuite(1, 5000);
        State = KvmSessionState.Connecting;
        receiveLoop = RunReceiveLoopAsync();
        heartbeatLoop = RunHeartbeatLoopAsync();
    }

    public KvmSessionState State { get; private set; }

    public Exception? Failure { get; private set; }

    public KvmCipherSuite SelectedCipherSuite { get; private set; }

    public KvmSessionDiagnostics GetDiagnostics() => new(
        State,
        Interlocked.Read(ref packetsReceived),
        Interlocked.Read(ref videoPacketsReceived),
        Interlocked.Read(ref crcErrors),
        Interlocked.Read(ref frameErrors),
        checked((byte)Volatile.Read(ref lastCommand)),
        Volatile.Read(ref lastFrameError));

    public static async Task<KvmClientSession> ConnectAsync(
        KvmConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Encrypted)
        {
            throw new NotSupportedException("Encrypted KVM negotiation requires the extended session path.");
        }

        var connection = await LegacyTcpConnection.ConnectAsync(
            options.Host,
            options.Port,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var session = new KvmClientSession(connection, options);
            if (!string.IsNullOrEmpty(options.ExtendedVerifyValue))
            {
                await session.SendAsync(KvmCommandBuilder.GetCipherSuites(options.BladeNumber), cancellationToken)
                    .ConfigureAwait(false);
                var offers = await session.cipherSuites.Task
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
                var selected = KvmCipherSuiteParser.SelectPreferred(offers);
                session.SelectedCipherSuite = selected;
                await session.SendAsync(
                    KvmCommandBuilder.SelectCipherSuite(options.BladeNumber, selected.Algorithm, selected.Iterations),
                    cancellationToken).ConfigureAwait(false);
            }

            await session.SendAsync(KvmCommandBuilder.ConnectBlade(options.BladeNumber, options.ColorDepth), cancellationToken)
                .ConfigureAwait(false);
            await session.SendAsync(KvmCommandBuilder.SetFrameRate(35), cancellationToken).ConfigureAwait(false);
            await session.SendAsync(KvmCommandBuilder.SetMouseMode(KvmMouseMode.Absolute), cancellationToken)
                .ConfigureAwait(false);
            await session.WaitForHandshakeAsync(cancellationToken).ConfigureAwait(false);
            await session.WaitForAbsoluteMouseModeAsync(cancellationToken).ConfigureAwait(false);
            session.State = KvmSessionState.Connected;
            return session;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async IAsyncEnumerable<EncodedVideoFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public ValueTask SendKeyboardAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default) =>
        SendAsync(
            KvmCommandBuilder.Keyboard(options.BladeNumber, report.Span, options.CodeKey, options.KeyboardEncoding),
            cancellationToken);

    public ValueTask SendAbsoluteMouseAsync(
        byte buttons,
        ushort x,
        ushort y,
        sbyte wheel = 0,
        CancellationToken cancellationToken = default) =>
        SendAsync(KvmCommandBuilder.AbsoluteMouse(options.BladeNumber, buttons, x, y, wheel), cancellationToken);

    public ValueTask SendPowerAsync(KvmPowerAction action, CancellationToken cancellationToken = default) =>
        SendAsync(KvmCommandBuilder.Power(action), cancellationToken);

    public ValueTask RequestFullFrameAsync(CancellationToken cancellationToken = default) =>
        SendAsync(KvmCommandBuilder.RequestFullFrame(options.BladeNumber), cancellationToken);

    public async Task<KvmVirtualMediaEndpoint> GetVirtualMediaEndpointAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        if (effectiveTimeout <= TimeSpan.Zero && effectiveTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        await virtualMediaQueryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAsync(
                KvmCommandBuilder.RequestVirtualMediaCredential(options.VirtualMediaBladeNumber),
                cancellationToken).ConfigureAwait(false);
            await SendAsync(
                KvmCommandBuilder.RequestVirtualMediaPort(options.VirtualMediaBladeNumber),
                cancellationToken).ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(effectiveTimeout);
            var negotiation = Task.WhenAll(virtualMediaCredential.Task, virtualMediaPort.Task);
            var completed = await Task.WhenAny(negotiation, virtualMediaDenied.Task)
                .WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            if (completed == virtualMediaDenied.Task)
            {
                var state = await virtualMediaDenied.Task.ConfigureAwait(false);
                throw new UnauthorizedAccessException($"The iBMC denied virtual-media access (state {state}).");
            }

            await negotiation.ConfigureAwait(false);
            var credential = await virtualMediaCredential.Task.ConfigureAwait(false);
            var port = await virtualMediaPort.Task.ConfigureAwait(false);
            return new KvmVirtualMediaEndpoint(
                options.Host,
                port,
                credential.Credential,
                credential.Salt,
                SelectedCipherSuite,
                options.VirtualMediaEncrypted);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The iBMC did not return virtual-media negotiation data in time.");
        }
        finally
        {
            virtualMediaQueryGate.Release();
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
            await SendAsync(KvmCommandBuilder.DisconnectBlade(options.BladeNumber), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort disconnect; closing the socket is authoritative.
        }

        lifetime.Cancel();
        await connection.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(receiveLoop, heartbeatLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            State = KvmSessionState.Closed;
            frames.Writer.TryComplete();
            virtualMediaQueryGate.Dispose();
            lifetime.Dispose();
        }
    }

    private ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        connection.SendPacketAsync(options.CodeKey, payload, cancellationToken);

    private async Task RunHeartbeatLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                await SendAsync(KvmCommandBuilder.Heartbeat(options.BladeNumber), lifetime.Token).ConfigureAwait(false);
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
            await foreach (var packet in connection.ReadPacketsAsync(lifetime.Token).ConfigureAwait(false))
            {
                Interlocked.Increment(ref packetsReceived);
                Volatile.Write(ref lastCommand, packet.Command);
                if (!packet.IsCrcValid)
                {
                    Interlocked.Increment(ref crcErrors);
                }

                if (packet.Command == 0x43)
                {
                    cipherSuites.TrySetResult(KvmCipherSuiteParser.Parse(packet.Payload.Span));
                    continue;
                }

                if (packet.Command == 0x08 && packet.Payload.Length >= 3)
                {
                    connectionState.TrySetResult(packet.Payload.Span[2]);
                    continue;
                }

                if (packet.Command == 0x25 && packet.Payload.Length >= 3)
                {
                    var rawMode = packet.Payload.Span[2];
                    if (rawMode <= (byte)KvmMouseMode.Absolute)
                    {
                        mouseModeState.TrySetResult((KvmMouseMode)rawMode);
                    }
                    else
                    {
                        mouseModeState.TrySetException(
                            new InvalidDataException($"The iBMC returned an invalid mouse mode ({rawMode})."));
                    }
                    continue;
                }

                if (packet.Command == 0x32)
                {
                    ProcessVirtualMediaCredential(packet.Payload.Span);
                    continue;
                }

                if (packet.Command == 0x36)
                {
                    ProcessVirtualMediaPort(packet.Payload.Span);
                    continue;
                }

                if (packet.Command == 0x51)
                {
                    ProcessVirtualMediaPrivilege(packet.Payload.Span);
                    continue;
                }

                if (packet.Command != 0x02 || packet.Payload.Length <= 2)
                {
                    continue;
                }

                firstVideoPacket.TrySetResult();
                Interlocked.Increment(ref videoPacketsReceived);
                var chunk = packet.Payload.Span[2..];
                if (chunk.Length >= 8 && chunk[0] == 0 && chunk[1] == 0 && (chunk[7] & 0x80) != 0)
                {
                    // New-compression firmware emits a zero-length marker when the
                    // desktop is unchanged. KVM DrawThread drops it before assembly.
                    continue;
                }

                try
                {
                    if (assembler.TryAddChunk(chunk, out var encodedFrame))
                    {
                        State = KvmSessionState.Streaming;
                        await frames.Writer.WriteAsync(encodedFrame!, lifetime.Token).ConfigureAwait(false);
                    }
                }
                catch (InvalidDataException exception)
                {
                    Interlocked.Increment(ref frameErrors);
                    Volatile.Write(ref lastFrameError, exception.Message);
                    assembler.Reset();
                    await RequestFullFrameThrottledAsync().ConfigureAwait(false);
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
        finally
        {
            frames.Writer.TryComplete(Failure);
        }
    }

    private async ValueTask RequestFullFrameThrottledAsync()
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref lastFullFrameRequest);
        if (now - previous < 500 || Interlocked.CompareExchange(ref lastFullFrameRequest, now, previous) != previous)
        {
            return;
        }

        await RequestFullFrameAsync(lifetime.Token).ConfigureAwait(false);
    }

    private async Task WaitForHandshakeAsync(CancellationToken cancellationToken)
    {
        var signal = await Task.WhenAny(
            connectionState.Task,
            firstVideoPacket.Task,
            Task.Delay(TimeSpan.FromSeconds(6), cancellationToken)).ConfigureAwait(false);
        if (signal == connectionState.Task)
        {
            var state = await connectionState.Task.ConfigureAwait(false);
            if (state >= 2)
            {
                throw new InvalidOperationException(state switch
                {
                    2 => "The iBMC rejected the session because the user limit was reached.",
                    3 => "The iBMC rejected the KVM authentication value.",
                    4 => "The remote video signal is out of range.",
                    5 => "The iBMC KVM service is disabled.",
                    6 => "The iBMC user was removed during login.",
                    7 => "The iBMC session timed out.",
                    _ => $"The iBMC rejected the KVM connection (state {state}).",
                });
            }

            return;
        }

        if (signal == firstVideoPacket.Task)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("The KVM TCP connection opened, but the iBMC did not acknowledge the video handshake.");
    }

    private async Task WaitForAbsoluteMouseModeAsync(CancellationToken cancellationToken)
    {
        KvmMouseMode mode;
        try
        {
            mode = await mouseModeState.Task
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("The iBMC did not confirm absolute mouse mode.", exception);
        }

        if (mode != KvmMouseMode.Absolute)
        {
            throw new InvalidOperationException($"The iBMC did not enter absolute mouse mode (mode {(byte)mode}).");
        }
    }

    private void SetFailure(Exception exception)
    {
        Failure ??= exception;
        State = KvmSessionState.Faulted;
        mouseModeState.TrySetException(exception);
        virtualMediaCredential.TrySetException(exception);
        virtualMediaPort.TrySetException(exception);
        lifetime.Cancel();
    }

    private void ProcessVirtualMediaCredential(ReadOnlySpan<byte> payload)
    {
        try
        {
            var response = KvmVirtualMediaNegotiationParser.ParseCredential(payload);
            if (response.BladeNumber == options.VirtualMediaBladeNumber)
            {
                virtualMediaCredential.TrySetResult(response);
            }
        }
        catch (InvalidDataException exception)
        {
            virtualMediaCredential.TrySetException(exception);
        }
    }

    private void ProcessVirtualMediaPort(ReadOnlySpan<byte> payload)
    {
        try
        {
            var response = KvmVirtualMediaNegotiationParser.ParsePort(payload);
            if (response.BladeNumber == options.VirtualMediaBladeNumber)
            {
                virtualMediaPort.TrySetResult(response.Port);
            }
        }
        catch (InvalidDataException exception)
        {
            virtualMediaPort.TrySetException(exception);
        }
    }

    private void ProcessVirtualMediaPrivilege(ReadOnlySpan<byte> payload)
    {
        try
        {
            var response = KvmVirtualMediaNegotiationParser.ParsePrivilege(payload);
            if (response.BladeNumber == options.VirtualMediaBladeNumber &&
                KvmVirtualMediaNegotiationParser.IsDeniedPrivilege(response.State))
            {
                virtualMediaDenied.TrySetResult(response.State);
            }
        }
        catch (InvalidDataException exception)
        {
            virtualMediaCredential.TrySetException(exception);
            virtualMediaPort.TrySetException(exception);
        }
    }
}
