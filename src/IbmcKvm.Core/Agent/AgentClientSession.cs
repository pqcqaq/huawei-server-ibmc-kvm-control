using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Core.Agent;

public sealed class AgentClientSession : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly IAsyncDisposable? owner;
    private readonly AgentConnectionOptions options;
    private readonly byte[] token;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Channel<AgentVideoFrame> frames = Channel.CreateBounded<AgentVideoFrame>(
        new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    private readonly Task receiveLoop;
    private readonly Task heartbeatLoop;
    private long lastPongTick;
    private int lastMousePosition;
    private int hasMousePosition;
    private int disposed;

    private AgentClientSession(
        Stream stream,
        IAsyncDisposable? owner,
        AgentConnectionOptions options,
        byte[] token,
        AgentServerHello serverHello)
    {
        this.stream = stream;
        this.owner = owner;
        this.options = options;
        this.token = token;
        ServerHello = serverHello;
        State = AgentSessionState.Connected;
        lastPongTick = Environment.TickCount64;
        receiveLoop = RunReceiveLoopAsync();
        heartbeatLoop = RunHeartbeatLoopAsync();
    }

    public AgentServerHello ServerHello { get; }

    public AgentSessionState State { get; private set; } = AgentSessionState.Connecting;

    public Exception? Failure { get; private set; }

    public static async Task<ServerCertificateDetails> ProbeCertificateAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(host, port);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return await ProbeCertificateAsync(client.GetStream(), host, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<AgentClientSession> ConnectAsync(
        AgentConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateEndpoint(options.Host, options.Port);
        var fingerprint = CertificateFingerprint.Normalize(options.ServerCertificateFingerprint);
        var token = DecodeToken(options.PairingToken);
        var client = new TcpClient { NoDelay = true };
        SslStream? ssl = null;
        try
        {
            await client.ConnectAsync(options.Host, options.Port, cancellationToken).ConfigureAwait(false);
            ssl = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, _, _) => certificate is not null && CertificateFingerprint.Matches(certificate, fingerprint));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, cancellationToken).ConfigureAwait(false);
            var owner = new NetworkOwner(client, ssl);
            var session = await ConnectStreamAsync(ssl, owner, options, token, cancellationToken).ConfigureAwait(false);
            ssl = null;
            return session;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(token);
            if (ssl is not null)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
            }
            client.Dispose();
            throw;
        }
    }

    internal static async Task<AgentClientSession> ConnectStreamAsync(
        Stream stream,
        IAsyncDisposable? owner,
        AgentConnectionOptions options,
        ReadOnlyMemory<byte> pairingToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        var token = pairingToken.ToArray();
        try
        {
            await AgentProtocol.WriteAsync(stream, AgentMessageCodec.ClientHello(token), cancellationToken)
                .ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var response = await AgentProtocol.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (response.Kind == AgentMessageKind.Error)
            {
                var error = AgentMessageCodec.ParseError(response);
                throw new AuthenticationException($"Linux Agent authentication failed ({error.Code}): {error.Message}");
            }
            var hello = AgentMessageCodec.ParseServerHello(response);
            return new AgentClientSession(stream, owner, options, token, hello);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(token);
            throw;
        }
    }

    public async IAsyncEnumerable<AgentVideoFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public Task SendKeyboardAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default) =>
        SendAsync(AgentMessageCodec.Keyboard(report.Span), cancellationToken);

    public async Task SendMouseAsync(
        byte buttons,
        ushort x,
        ushort y,
        sbyte wheel,
        CancellationToken cancellationToken = default)
    {
        await SendAsync(
            AgentMessageCodec.Mouse(new AgentMouseReport(buttons, x, y, wheel)),
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref lastMousePosition, x | (y << 16));
        Volatile.Write(ref hasMousePosition, 1);
    }

    public Task ReleaseMouseButtonsAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref hasMousePosition) == 0)
        {
            return Task.CompletedTask;
        }
        var position = Volatile.Read(ref lastMousePosition);
        return SendMouseAsync(
            0,
            unchecked((ushort)position),
            unchecked((ushort)((uint)position >> 16)),
            0,
            cancellationToken);
    }

    public Task RequestKeyframeAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new AgentEnvelope(AgentMessageKind.KeyframeRequest, []), cancellationToken);

    public Task<AgentClientSession> ReconnectAsync(CancellationToken cancellationToken = default) =>
        ConnectAsync(options, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        try
        {
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await sendGate.WaitAsync(releaseTimeout.Token).ConfigureAwait(false);
            try
            {
                await AgentProtocol.WriteAsync(
                    stream,
                    AgentMessageCodec.Keyboard(new byte[8]),
                    releaseTimeout.Token).ConfigureAwait(false);
                if (Volatile.Read(ref hasMousePosition) != 0)
                {
                    var position = Volatile.Read(ref lastMousePosition);
                    await AgentProtocol.WriteAsync(
                        stream,
                        AgentMessageCodec.Mouse(new AgentMouseReport(
                            0,
                            unchecked((ushort)position),
                            unchecked((ushort)((uint)position >> 16)),
                            0)),
                        releaseTimeout.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                sendGate.Release();
            }
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(receiveLoop, heartbeatLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            frames.Writer.TryComplete();
            if (owner is not null)
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            CryptographicOperations.ZeroMemory(token);
            sendGate.Dispose();
            lifetime.Dispose();
            State = AgentSessionState.Closed;
        }
    }

    private async Task RunReceiveLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var envelope = await AgentProtocol.ReadAsync(stream, lifetime.Token).ConfigureAwait(false);
                switch (envelope.Kind)
                {
                    case AgentMessageKind.Frame:
                        var frame = AgentMessageCodec.ParseFrame(envelope);
                        State = AgentSessionState.Streaming;
                        await frames.Writer.WriteAsync(frame, lifetime.Token).ConfigureAwait(false);
                        break;
                    case AgentMessageKind.Pong when envelope.Payload.Length == 8:
                        Interlocked.Exchange(ref lastPongTick, Environment.TickCount64);
                        break;
                    case AgentMessageKind.Error:
                        var error = AgentMessageCodec.ParseError(envelope);
                        throw new InvalidOperationException($"Linux Agent error ({error.Code}): {error.Message}");
                    default:
                        throw new InvalidDataException($"Linux Agent sent an unexpected {envelope.Kind} message.");
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

    private async Task RunHeartbeatLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (Environment.TickCount64 - Interlocked.Read(ref lastPongTick) > 15_000)
                {
                    throw new TimeoutException("Linux Agent heartbeat timed out.");
                }
                await SendAsync(AgentMessageCodec.Ping(unchecked((ulong)Environment.TickCount64)), lifetime.Token)
                    .ConfigureAwait(false);
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

    private async Task SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await AgentProtocol.WriteAsync(stream, envelope, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private void SetFailure(Exception exception)
    {
        if (Failure is null)
        {
            Failure = exception;
            State = AgentSessionState.Faulted;
        }
        lifetime.Cancel();
    }

    private static byte[] DecodeToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            var token = Convert.FromBase64String(value.Trim());
            if (token.Length != 32)
            {
                CryptographicOperations.ZeroMemory(token);
                throw new FormatException("The Linux Agent pairing token must decode to exactly 32 bytes.");
            }
            return token;
        }
        catch (FormatException exception) when (!exception.Message.StartsWith("The Linux Agent", StringComparison.Ordinal))
        {
            throw new FormatException("The Linux Agent pairing token is not valid Base64.", exception);
        }
    }

    private static void ValidateEndpoint(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    private static async Task<ServerCertificateDetails> ProbeCertificateAsync(
        Stream stream,
        string host,
        CancellationToken cancellationToken)
    {
        X509Certificate2? captured = null;
        var policyErrors = SslPolicyErrors.None;
        await using var ssl = new SslStream(stream, false, (_, certificate, _, errors) =>
        {
            if (certificate is not null)
            {
                captured = new X509Certificate2(certificate);
            }
            policyErrors = errors;
            return certificate is not null;
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, cancellationToken).ConfigureAwait(false);
        using (captured)
        {
            if (captured is null)
            {
                throw new AuthenticationException("The Linux Agent did not present a server certificate.");
            }
            return new ServerCertificateDetails(
                captured.Subject,
                captured.Issuer,
                captured.NotBefore,
                captured.NotAfter,
                CertificateFingerprint.GetSha256(captured),
                policyErrors,
                captured.Export(X509ContentType.Cert));
        }
    }

    private sealed class NetworkOwner(TcpClient client, SslStream stream) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
        }
    }
}
