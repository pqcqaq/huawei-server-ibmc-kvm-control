using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Threading.Channels;
using IbmcKvm.Core.Input;
using IbmcKvm.Core.Video;
using IbmcKvm.Protocol.Profiles;
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

[Flags]
public enum RemoteLockKeys : byte
{
    None = 0,
    NumLock = 1 << 0,
    CapsLock = 1 << 1,
    ScrollLock = 1 << 2,
}

public sealed record KvmConnectionOptions(
    string Host,
    int Port,
    int CodeKey,
    byte BladeNumber = 1,
    byte ColorDepth = 3,
    bool Encrypted = false,
    string? ExtendedVerifyValue = null,
    string? VerificationValue = null,
    string? LoginDecryptionKey = null,
    byte VirtualMediaBladeNumber = 0,
    bool VirtualMediaEncrypted = true,
    int Privilege = (int)KvmPrivilegeLevel.Administrator,
    KvmKeyboardEncoding KeyboardEncoding = KvmKeyboardEncoding.CodeKeyAes,
    IKvmProtocolProfile? ProtocolProfile = null,
    int? KnownVirtualMediaPort = null,
    ReadOnlyMemory<byte> ReconnectToken = default,
    KvmMouseMode MouseMode = KvmMouseMode.Absolute,
    KvmBladeSessionMode SessionMode = KvmBladeSessionMode.Control);

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
    private readonly IKvmPacketConnection connection;
    private readonly KvmConnectionOptions options;
    private readonly IKvmProtocolProfile profile;
    private readonly IKvmPayloadCryptography? cryptography;
    private readonly LegacyVideoFrameAssembler assembler = new();
    private readonly Channel<EncodedVideoFrame> frames;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task receiveLoop;
    private readonly Task heartbeatLoop;
    private readonly TaskCompletionSource<IReadOnlyList<KvmCipherSuite>> cipherSuites =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource sessionMaterial =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<byte> connectionState =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object mouseModeGate = new();
    private TaskCompletionSource<KvmMouseMode> mouseModeState =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object videoQualityGate = new();
    private TaskCompletionSource<byte> videoQualityState =
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
    private readonly SemaphoreSlim chassisPresenceQueryGate = new(1, 1);
    private readonly SemaphoreSlim keyboardSendGate = new(1, 1);
    private readonly object chassisPresenceGate = new();
    private readonly object bladeStateQueryGate = new();
    private readonly Dictionary<byte, TaskCompletionSource<ChassisBladeState>> bladeStateQueries = [];
    private TaskCompletionSource<ChassisPresence>? chassisPresenceQuery;
    private long lastFullFrameRequest;
    private long packetsReceived;
    private long videoPacketsReceived;
    private long crcErrors;
    private long frameErrors;
    private int lastCommand;
    private string? lastFrameError;
    private byte[]? reconnectToken;
    private int powerDenied;
    private int virtualMediaAccessDenied;
    private int disposed;

    private KvmClientSession(
        IKvmPacketConnection connection,
        KvmConnectionOptions options,
        IKvmPayloadCryptography? cryptography)
    {
        this.connection = connection;
        this.options = options;
        profile = options.ProtocolProfile ?? ModernKvmProtocolProfile.Instance;
        this.cryptography = cryptography;
        frames = Channel.CreateBounded<EncodedVideoFrame>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        SelectedCipherSuite = new KvmCipherSuite(1, 5000);
        CurrentMouseMode = KvmMouseMode.Absolute;
        CurrentColorDepth = options.ColorDepth;
        CurrentVideoQuality = 70;
        State = KvmSessionState.Connecting;
        receiveLoop = RunReceiveLoopAsync();
        heartbeatLoop = RunHeartbeatLoopAsync();
    }

    public KvmSessionState State { get; private set; }

    public byte BladeNumber => options.BladeNumber;

    public KvmBladeSessionMode SessionMode => options.SessionMode;

    public Exception? Failure { get; private set; }

    public KvmCipherSuite SelectedCipherSuite { get; private set; }

    public ReadOnlyMemory<byte> ReconnectToken =>
        Volatile.Read(ref reconnectToken) is { } token ? token.ToArray() : ReadOnlyMemory<byte>.Empty;

    public byte[] CopyReconnectToken() =>
        Volatile.Read(ref reconnectToken) is { } token ? token.ToArray() : [];

    public KvmMouseMode CurrentMouseMode { get; private set; }

    public byte CurrentColorDepth { get; private set; }

    public byte CurrentVideoQuality { get; private set; }

    public KvmProtocolCapabilities Capabilities => profile.Capabilities;

    public event EventHandler? VideoSettingsChanged;

    public RemoteLockKeys RemoteLockKeys { get; private set; }

    public event EventHandler? RemoteLockKeysChanged;

    public KvmSessionPermissions Permissions => KvmSessionPermissions.Create(
        options.Privilege,
        Capabilities,
        Volatile.Read(ref powerDenied) != 0,
        Volatile.Read(ref virtualMediaAccessDenied) != 0,
        options.SessionMode == KvmBladeSessionMode.Monitor);

    public event EventHandler? PermissionsChanged;

    public event EventHandler<KvmPrivilegeDeniedEventArgs>? PrivilegeDenied;

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
        var profile = options.ProtocolProfile ?? ModernKvmProtocolProfile.Instance;
        if (options.Encrypted && !profile.Capabilities.SupportsEncryptedKvm)
        {
            throw new NotSupportedException($"The {profile.Kind} profile does not support encrypted KVM.");
        }

        if (options.Encrypted && profile.WireFormat == KvmWireFormat.ModernCodeKey &&
            string.IsNullOrWhiteSpace(options.ExtendedVerifyValue) &&
            string.IsNullOrWhiteSpace(options.VerificationValue))
        {
            throw new ArgumentException(
                "An encrypted KVM session requires a raw or extended verification value.",
                nameof(options));
        }

        if (options.Encrypted && profile.WireFormat == KvmWireFormat.ModernCodeKey &&
            string.IsNullOrWhiteSpace(options.LoginDecryptionKey))
        {
            throw new ArgumentException(
                "An encrypted KVM session requires the login decryption key.",
                nameof(options));
        }

        if (options.ReconnectToken.Length is not (0 or 128))
        {
            throw new ArgumentException("A KVM reconnect token must contain exactly 128 bytes.", nameof(options));
        }

        if (options.Privilege is < (int)KvmPrivilegeLevel.Callback or > (int)KvmPrivilegeLevel.Administrator)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The KVM privilege must be in the 1..4 range.");
        }

        if (!Enum.IsDefined(options.SessionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The blade session mode is invalid.");
        }

        if (options.SessionMode == KvmBladeSessionMode.Monitor && options.Encrypted)
        {
            throw new NotSupportedException(
                "The original monitor command uses a code-key packet and does not negotiate encrypted KVM payloads.");
        }

        IKvmPayloadCryptography? cryptography = options.Encrypted
            ? profile.WireFormat == KvmWireFormat.ImanaSessionId
                ? ImanaSessionCryptography.FromCodeKey(options.CodeKey)
                : KvmSessionCryptography.FromLoginKey(options.LoginDecryptionKey!)
            : null;
        IKvmPacketConnection? connection = null;
        KvmClientSession? session = null;
        try
        {
            connection = profile.WireFormat == KvmWireFormat.ImanaSessionId
                ? await ImanaTcpConnection.ConnectAsync(
                    options.Host,
                    options.Port,
                    options.CodeKey,
                    options.Encrypted,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await LegacyTcpConnection.ConnectAsync(
                    options.Host,
                    options.Port,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            session = new KvmClientSession(connection, options, cryptography);
            cryptography = null;
            if (options.SessionMode == KvmBladeSessionMode.Monitor)
            {
                await session.SendAsync(KvmCommandBuilder.MonitorBlade(options.BladeNumber), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (options.Encrypted && profile.WireFormat == KvmWireFormat.ModernCodeKey)
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

                await session.SendEncryptedConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await session.SendAsync(
                    session.profile.BuildConnectPayload(
                        options.BladeNumber,
                        options.ColorDepth,
                        reconnect: !options.ReconnectToken.IsEmpty,
                        options.ReconnectToken.Span),
                    cancellationToken).ConfigureAwait(false);
            }

            if (options.SessionMode == KvmBladeSessionMode.Control)
            {
                await session.SendAsync(KvmCommandBuilder.SetFrameRate(35), cancellationToken).ConfigureAwait(false);
                await session.SendAsync(KvmCommandBuilder.SetMouseMode(options.MouseMode), cancellationToken)
                    .ConfigureAwait(false);
                if (options.Encrypted && profile.WireFormat == KvmWireFormat.ModernCodeKey)
                {
                    await session.WaitForSessionMaterialAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await session.WaitForHandshakeAsync(cancellationToken).ConfigureAwait(false);
            if (options.SessionMode == KvmBladeSessionMode.Control)
            {
                await session.WaitForMouseModeAsync(options.MouseMode, cancellationToken).ConfigureAwait(false);
            }
            session.State = KvmSessionState.Connected;
            return session;
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            cryptography?.Dispose();
        }
    }

    public async Task<KvmClientSession> ReconnectAsync(
        ReadOnlyMemory<byte> reconnectToken,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SupportsReconnect)
        {
            throw new NotSupportedException("The selected protocol profile does not support KVM reconnect.");
        }

        var quality = CurrentVideoQuality;
        var reconnected = await ConnectAsync(
            options with
            {
                ColorDepth = CurrentColorDepth,
                MouseMode = CurrentMouseMode,
                ReconnectToken = reconnectToken,
            },
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (quality != 70 && reconnected.Capabilities.SupportsVideoQuality)
            {
                await reconnected.SetVideoQualityAsync(quality, committed: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            return reconnected;
        }
        catch
        {
            await reconnected.DisposeAsync().ConfigureAwait(false);
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

    public async Task<ChassisPresence> GetChassisPresenceAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        EnsureChassisSupported();
        var effectiveTimeout = ValidateQueryTimeout(timeout);
        await chassisPresenceQueryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new TaskCompletionSource<ChassisPresence>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (chassisPresenceGate)
            {
                chassisPresenceQuery = pending;
            }

            await SendAsync(KvmCommandBuilder.RequestBladePresent(), cancellationToken).ConfigureAwait(false);
            try
            {
                return await pending.Task.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException("The iBMC did not return the chassis presence bitmap.", exception);
            }
            finally
            {
                lock (chassisPresenceGate)
                {
                    if (ReferenceEquals(chassisPresenceQuery, pending))
                    {
                        chassisPresenceQuery = null;
                    }
                }
            }
        }
        finally
        {
            chassisPresenceQueryGate.Release();
        }
    }

    public async Task<ChassisBladeState> GetBladeStateAsync(
        byte bladeNumber,
        bool exclusive,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        EnsureChassisSupported();
        var request = KvmCommandBuilder.RequestBladeState(bladeNumber, exclusive);
        var effectiveTimeout = ValidateQueryTimeout(timeout);
        var pending = new TaskCompletionSource<ChassisBladeState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (bladeStateQueryGate)
        {
            if (!bladeStateQueries.TryAdd(bladeNumber, pending))
            {
                throw new InvalidOperationException($"Blade {bladeNumber} already has a pending state query.");
            }
        }

        try
        {
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                return await pending.Task.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"The iBMC did not return state for blade {bladeNumber}.", exception);
            }
        }
        finally
        {
            lock (bladeStateQueryGate)
            {
                bladeStateQueries.Remove(bladeNumber);
            }
        }
    }

    public async Task<ChassisSnapshot> RefreshChassisAsync(
        bool exclusive,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var presence = await GetChassisPresenceAsync(timeout, cancellationToken).ConfigureAwait(false);
        var stateTasks = presence.PresentBladeNumbers
            .Select(blade => GetBladeStateAsync(blade, exclusive, timeout, cancellationToken))
            .ToArray();
        var presentStates = await Task.WhenAll(stateTasks).ConfigureAwait(false);
        var statesByBlade = presentStates.ToDictionary(state => state.BladeNumber);
        var allStates = ImmutableArray.CreateBuilder<ChassisBladeState>(ChassisProtocolParser.MaximumBladeCount);
        for (byte blade = 1; blade <= ChassisProtocolParser.MaximumBladeCount; blade++)
        {
            allStates.Add(statesByBlade.TryGetValue(blade, out var state)
                ? state
                : new ChassisBladeState(
                    blade,
                    ChassisBladeStatus.Absent,
                    0,
                    0,
                    null,
                    null,
                    false,
                    null,
                    true));
        }

        return new ChassisSnapshot(DateTimeOffset.UtcNow, allStates.MoveToImmutable());
    }

    public Task<KvmClientSession> ConnectRelatedBladeAsync(
        ChassisBladeState blade,
        KvmBladeSessionMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blade);
        EnsureChassisSupported();
        if (mode == KvmBladeSessionMode.Control && !blade.CanControl)
        {
            throw new InvalidOperationException($"Blade {blade.BladeNumber} is not available for KVM control ({blade.Status}).");
        }

        if (mode == KvmBladeSessionMode.Monitor && !blade.CanMonitor)
        {
            throw new InvalidOperationException($"Blade {blade.BladeNumber} is not available for monitoring ({blade.Status}).");
        }

        var host = blade.UsesManagementAddress
            ? options.Host
            : blade.Address?.ToString() ?? throw new InvalidDataException("The blade state does not contain a direct KVM address.");
        var port = blade.Port ?? (blade.UsesManagementAddress
            ? options.Port
            : throw new InvalidDataException("The blade state does not contain a direct KVM port."));
        return ConnectAsync(
            options with
            {
                Host = host,
                Port = port,
                CodeKey = blade.UsesSharedCodeKey ? options.CodeKey : 0,
                BladeNumber = blade.BladeNumber,
                Encrypted = mode == KvmBladeSessionMode.Control && options.Encrypted,
                VirtualMediaBladeNumber = blade.BladeNumber,
                KnownVirtualMediaPort = null,
                ReconnectToken = ReadOnlyMemory<byte>.Empty,
                SessionMode = mode,
            },
            cancellationToken);
    }

    public async ValueTask SendKeyboardAsync(
        ReadOnlyMemory<byte> report,
        CancellationToken cancellationToken = default)
    {
        EnsureKvmControlAllowed();
        if (report.Length != 8)
        {
            throw new ArgumentException("A boot-protocol keyboard report contains 8 bytes", nameof(report));
        }

        await keyboardSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendKeyboardCoreAsync(report, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            keyboardSendGate.Release();
        }
    }

    public async Task SendKeyPulseAsync(
        HidModifiers modifiers,
        byte usage,
        TimeSpan? holdDuration = null,
        CancellationToken cancellationToken = default)
    {
        EnsureKvmControlAllowed();
        if (usage is 0 or 1)
        {
            throw new ArgumentOutOfRangeException(nameof(usage));
        }

        var duration = ValidateKeyboardHoldDuration(holdDuration, TimeSpan.FromMilliseconds(20));
        var pressed = new byte[8];
        pressed[0] = (byte)modifiers;
        pressed[2] = usage;
        var released = new byte[8];
        released[0] = (byte)modifiers;

        await keyboardSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendKeyboardPressAndReleaseAsync(pressed, released, duration, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            keyboardSendGate.Release();
        }
    }

    private async ValueTask SendKeyboardCoreAsync(
        ReadOnlyMemory<byte> report,
        CancellationToken cancellationToken)
    {
        if (!options.Encrypted)
        {
            await SendAsync(
                KvmCommandBuilder.Keyboard(options.BladeNumber, report.Span, options.CodeKey, options.KeyboardEncoding),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendEncryptedInputAsync(0x03, report, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendKeyCombinationAsync(
        HidKeyCombination combination,
        TimeSpan? holdDuration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(combination);
        EnsureKvmControlAllowed();
        var duration = ValidateKeyboardHoldDuration(holdDuration, TimeSpan.FromMilliseconds(100));

        var pressed = combination.CreateReport();
        await keyboardSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendKeyboardPressAndReleaseAsync(pressed, new byte[8], duration, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            keyboardSendGate.Release();
        }
    }

    private async Task SendKeyboardPressAndReleaseAsync(
        ReadOnlyMemory<byte> pressed,
        ReadOnlyMemory<byte> released,
        TimeSpan holdDuration,
        CancellationToken cancellationToken)
    {
        var sent = false;
        try
        {
            await SendKeyboardCoreAsync(pressed, cancellationToken).ConfigureAwait(false);
            sent = true;
            if (holdDuration > TimeSpan.Zero)
            {
                await Task.Delay(holdDuration, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (sent)
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendKeyboardCoreAsync(released, releaseTimeout.Token).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan ValidateKeyboardHoldDuration(TimeSpan? holdDuration, TimeSpan defaultDuration)
    {
        var duration = holdDuration ?? defaultDuration;
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(holdDuration));
        }

        return duration;
    }

    public ValueTask RequestKeyboardStateAsync(CancellationToken cancellationToken = default) =>
        SendAsync(KvmCommandBuilder.QueryKeyboardState(options.BladeNumber), cancellationToken);

    public async ValueTask SendAbsoluteMouseAsync(
        byte buttons,
        ushort x,
        ushort y,
        sbyte wheel = 0,
        CancellationToken cancellationToken = default)
    {
        EnsureKvmControlAllowed();
        if (CurrentMouseMode != KvmMouseMode.Absolute)
        {
            throw new InvalidOperationException("The KVM session is not using absolute mouse mode.");
        }

        if (!options.Encrypted)
        {
            await SendAsync(
                KvmCommandBuilder.AbsoluteMouse(options.BladeNumber, buttons, x, y, wheel),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var report = KvmCommandBuilder.AbsoluteMouseReport(buttons, x, y, wheel);
        try
        {
            await SendEncryptedInputAsync(0x05, report, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(report);
        }
    }

    public async ValueTask SendRelativeMouseAsync(
        byte buttons,
        sbyte deltaX,
        sbyte deltaY,
        sbyte wheel = 0,
        CancellationToken cancellationToken = default)
    {
        EnsureKvmControlAllowed();
        if (CurrentMouseMode != KvmMouseMode.Relative)
        {
            throw new InvalidOperationException("The KVM session is not using relative mouse mode.");
        }

        if (!options.Encrypted)
        {
            await SendAsync(
                KvmCommandBuilder.RelativeMouse(options.BladeNumber, buttons, deltaX, deltaY, wheel),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] report = [buttons, unchecked((byte)deltaX), unchecked((byte)deltaY), unchecked((byte)wheel)];
        try
        {
            await SendEncryptedInputAsync(0x05, report, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(report);
        }
    }

    public async Task SynchronizeMouseAsync(CancellationToken cancellationToken = default)
    {
        EnsureKvmControlAllowed();
        if (CurrentMouseMode == KvmMouseMode.Relative)
        {
            for (var index = 0; index < 15; index++)
            {
                await SendRelativeMouseAsync(0, -127, -127, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (!options.Encrypted)
        {
            await SendAsync(
                KvmCommandBuilder.SynchronizeAbsoluteMouse(options.BladeNumber),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var report = KvmCommandBuilder.SynchronizeAbsoluteMouseReport();
        try
        {
            await SendEncryptedInputAsync(0x05, report, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(report);
        }
    }

    public async Task SetMouseModeAsync(
        KvmMouseMode mode,
        CancellationToken cancellationToken = default)
    {
        EnsureKvmControlAllowed();
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        TaskCompletionSource<KvmMouseMode> acknowledgement;
        lock (mouseModeGate)
        {
            if (CurrentMouseMode == mode)
            {
                return;
            }

            mouseModeState = new(TaskCreationOptions.RunContinuationsAsynchronously);
            acknowledgement = mouseModeState;
        }

        await SendAsync(KvmCommandBuilder.SetMouseMode(mode), cancellationToken).ConfigureAwait(false);
        try
        {
            var confirmed = await acknowledgement.Task
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
            if (confirmed != mode)
            {
                throw new InvalidOperationException($"The iBMC selected mouse mode {(byte)confirmed} instead of {(byte)mode}.");
            }
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("The iBMC did not confirm the requested mouse mode.", exception);
        }
    }

    public async ValueTask SendPowerAsync(
        KvmPowerAction action,
        CancellationToken cancellationToken = default)
    {
        if (!Permissions.CanControlPower)
        {
            throw new UnauthorizedAccessException("The current iBMC privilege does not allow power control.");
        }

        if (!options.Encrypted)
        {
            await SendAsync(KvmCommandBuilder.Power(action), cancellationToken).ConfigureAwait(false);
            return;
        }

        var encrypted = cryptography!.EncryptPower(action);
        try
        {
            var payload = KvmCommandBuilder.EncryptedPower(encrypted);
            try
            {
                await SendAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public ValueTask RequestFullFrameAsync(CancellationToken cancellationToken = default) =>
        SendAsync(KvmCommandBuilder.RequestFullFrame(options.BladeNumber), cancellationToken);

    public ValueTask StartRecordingAsync(CancellationToken cancellationToken = default) =>
        Permissions.CanControlKvm
            ? SendAsync(KvmCommandBuilder.StartRecording(), cancellationToken)
            : ValueTask.FromException(new UnauthorizedAccessException("The current iBMC privilege does not allow recording control."));

    public ValueTask StopRecordingAsync(CancellationToken cancellationToken = default) =>
        Permissions.CanControlKvm
            ? SendAsync(KvmCommandBuilder.StopRecording(), cancellationToken)
            : ValueTask.FromException(new UnauthorizedAccessException("The current iBMC privilege does not allow recording control."));

    public async ValueTask SetColorDepthAsync(
        byte colorDepth,
        CancellationToken cancellationToken = default)
    {
        EnsureKvmControlAllowed();
        if (!Capabilities.ColorDepths.Contains(colorDepth))
        {
            throw new ArgumentOutOfRangeException(nameof(colorDepth));
        }

        await SendAsync(
            KvmCommandBuilder.SetColorDepth(options.BladeNumber, colorDepth),
            cancellationToken).ConfigureAwait(false);
        CurrentColorDepth = colorDepth;
        VideoSettingsChanged?.Invoke(this, EventArgs.Empty);
        assembler.Reset();
        await RequestFullFrameAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetVideoQualityAsync(
        byte clarity,
        bool committed,
        CancellationToken cancellationToken = default)
    {
        EnsureKvmControlAllowed();
        if (!Capabilities.SupportsVideoQuality)
        {
            throw new NotSupportedException("The selected protocol profile does not support video quality control.");
        }

        KvmCommandBuilder.SetVideoQuality(clarity, committed);
        TaskCompletionSource<byte> acknowledgement;
        lock (videoQualityGate)
        {
            videoQualityState = new(TaskCreationOptions.RunContinuationsAsynchronously);
            acknowledgement = videoQualityState;
        }

        await SendAsync(KvmCommandBuilder.SetVideoQuality(clarity, committed), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await acknowledgement.Task
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("The iBMC did not confirm the requested video quality.", exception);
        }

        if (committed)
        {
            assembler.Reset();
            await RequestFullFrameAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryDecodeVideoQuality(byte wireValue, out byte quality)
    {
        quality = wireValue >= 70 ? (byte)(wireValue - 10) : wireValue;
        return quality is >= 40 and <= 90 && quality % 10 == 0;
    }

    public async Task<KvmVirtualMediaEndpoint> GetVirtualMediaEndpointAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Permissions.CanUseVirtualMedia)
        {
            throw new UnauthorizedAccessException("The current iBMC privilege does not allow virtual-media access.");
        }
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
            if (options.KnownVirtualMediaPort is { } knownPort)
            {
                if (knownPort is < 1 or > ushort.MaxValue)
                {
                    throw new InvalidOperationException("The discovered virtual-media port is out of range.");
                }

                virtualMediaPort.TrySetResult(knownPort);
            }
            else
            {
                await SendAsync(
                    KvmCommandBuilder.RequestVirtualMediaPort(options.VirtualMediaBladeNumber),
                    cancellationToken).ConfigureAwait(false);
            }

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
            var disconnect = options.SessionMode == KvmBladeSessionMode.Monitor
                ? KvmCommandBuilder.StopMonitoringBlade(options.BladeNumber)
                : KvmCommandBuilder.DisconnectBlade(options.BladeNumber);
            await SendAsync(disconnect, CancellationToken.None)
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
            cryptography?.Dispose();
            var token = Interlocked.Exchange(ref reconnectToken, null);
            if (token is not null)
            {
                CryptographicOperations.ZeroMemory(token);
            }
            virtualMediaQueryGate.Dispose();
            chassisPresenceQueryGate.Dispose();
            keyboardSendGate.Dispose();
            lifetime.Dispose();
        }
    }

    private ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        connection.SendPacketAsync(options.CodeKey, payload, cancellationToken);

    private async ValueTask SendEncryptedConnectAsync(CancellationToken cancellationToken)
    {
        var payload = profile.BuildConnectPayload(
            options.BladeNumber,
            options.ColorDepth,
            reconnect: !options.ReconnectToken.IsEmpty,
            options.ReconnectToken.Span);
        var authenticator = ((KvmSessionCryptography)cryptography!).DeriveConnectAuthenticator(
            string.IsNullOrWhiteSpace(options.ExtendedVerifyValue)
                ? options.VerificationValue!
                : options.ExtendedVerifyValue,
            SelectedCipherSuite);
        byte[]? encoded = null;
        try
        {
            encoded = LegacyPacketEncoder.EncodeExtendedAuthenticator(authenticator, payload);
            await connection.SendAsync(encoded, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authenticator);
            if (encoded is not null)
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
    }

    private async ValueTask SendEncryptedInputAsync(
        byte command,
        ReadOnlyMemory<byte> report,
        CancellationToken cancellationToken)
    {
        var encrypted = cryptography!.EncryptInput(report.Span);
        try
        {
            var payload = KvmCommandBuilder.EncryptedInput(command, options.BladeNumber, encrypted);
            try
            {
                await SendAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

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

                if (packet.Command == ChassisProtocolParser.PresenceResponseCommand)
                {
                    ProcessChassisPresence(packet.Payload.Span);
                    continue;
                }

                if (packet.Command == ChassisProtocolParser.StateResponseCommand)
                {
                    ProcessBladeState(packet.Payload.Span);
                    continue;
                }

                if (packet.Command == 0x40 &&
                    packet.Payload.Length == 130 &&
                    packet.Payload.Span[1] == options.BladeNumber)
                {
                    var token = packet.Payload.Span[2..].ToArray();
                    if (cryptography is KvmSessionCryptography loginCryptography)
                    {
                        var decrypted = loginCryptography.DecryptLoginData(token);
                        CryptographicOperations.ZeroMemory(token);
                        token = decrypted;
                    }

                    if (token.Length != 128)
                    {
                        CryptographicOperations.ZeroMemory(token);
                        throw new InvalidDataException("The KVM reconnect token has an invalid length.");
                    }

                    var previous = Interlocked.Exchange(ref reconnectToken, token);
                    if (previous is not null)
                    {
                        CryptographicOperations.ZeroMemory(previous);
                    }

                    continue;
                }

                if (packet.Command == 0x40 &&
                    cryptography is KvmSessionCryptography modernCryptography &&
                    !sessionMaterial.Task.IsCompleted)
                {
                    KvmEncryptedPayloadCodec.EstablishSession(
                        packet.Payload.Span,
                        options.BladeNumber,
                        SelectedCipherSuite,
                        modernCryptography);
                    sessionMaterial.TrySetResult();
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
                        lock (mouseModeGate)
                        {
                            CurrentMouseMode = (KvmMouseMode)rawMode;
                            mouseModeState.TrySetResult(CurrentMouseMode);
                        }
                    }
                    else
                    {
                        lock (mouseModeGate)
                        {
                            mouseModeState.TrySetException(
                                new InvalidDataException($"The iBMC returned an invalid mouse mode ({rawMode})."));
                        }
                    }
                    continue;
                }

                if (packet.Command == 0x04 &&
                    packet.Payload.Length >= 3 &&
                    packet.Payload.Span[1] == options.BladeNumber)
                {
                    RemoteLockKeys = (RemoteLockKeys)(packet.Payload.Span[2] & 0x07);
                    RemoteLockKeysChanged?.Invoke(this, EventArgs.Empty);
                    continue;
                }

                if (packet.Command == 0x28 && packet.Payload.Length >= 3)
                {
                    var wireQuality = packet.Payload.Span[2];
                    if (!TryDecodeVideoQuality(wireQuality, out var quality))
                    {
                        lock (videoQualityGate)
                        {
                            videoQualityState.TrySetException(
                                new InvalidDataException($"The iBMC returned an invalid video quality ({wireQuality})."));
                        }
                        continue;
                    }

                    lock (videoQualityGate)
                    {
                        CurrentVideoQuality = quality;
                        videoQualityState.TrySetResult(quality);
                    }

                    VideoSettingsChanged?.Invoke(this, EventArgs.Empty);
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
                byte[]? normalized = null;
                try
                {
                    var chunk = packet.Payload.Span[2..];
                    if (options.Encrypted)
                    {
                        normalized = KvmEncryptedPayloadCodec.NormalizeVideoChunk(chunk, cryptography!);
                        chunk = normalized;
                    }

                    if (chunk.Length >= 8 && chunk[0] == 0 && chunk[1] == 0 && (chunk[7] & 0x80) != 0)
                    {
                        // New-compression firmware emits a zero-length marker when the
                        // desktop is unchanged. KVM DrawThread drops it before assembly.
                        continue;
                    }

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
                finally
                {
                    if (normalized is not null)
                    {
                        CryptographicOperations.ZeroMemory(normalized);
                    }
                }
            }

            if (!lifetime.IsCancellationRequested)
            {
                SetFailure(new EndOfStreamException("The KVM connection closed unexpectedly."));
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

    private async Task WaitForSessionMaterialAsync(CancellationToken cancellationToken)
    {
        try
        {
            await sessionMaterial.Task
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("The iBMC did not return encrypted KVM session key material.", exception);
        }
    }

    private async Task WaitForMouseModeAsync(KvmMouseMode expectedMode, CancellationToken cancellationToken)
    {
        KvmMouseMode mode;
        try
        {
            Task<KvmMouseMode> acknowledgement;
            lock (mouseModeGate)
            {
                acknowledgement = mouseModeState.Task;
            }

            mode = await acknowledgement
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"The iBMC did not confirm {expectedMode.ToString().ToLowerInvariant()} mouse mode.", exception);
        }

        if (mode != expectedMode)
        {
            throw new InvalidOperationException(
                $"The iBMC did not enter {expectedMode.ToString().ToLowerInvariant()} mouse mode (mode {(byte)mode}).");
        }
    }

    private void SetFailure(Exception exception)
    {
        Failure ??= exception;
        State = KvmSessionState.Faulted;
        cipherSuites.TrySetException(exception);
        sessionMaterial.TrySetException(exception);
        connectionState.TrySetException(exception);
        lock (mouseModeGate)
        {
            mouseModeState.TrySetException(exception);
        }
        virtualMediaCredential.TrySetException(exception);
        virtualMediaPort.TrySetException(exception);
        lock (chassisPresenceGate)
        {
            chassisPresenceQuery?.TrySetException(exception);
        }
        lock (bladeStateQueryGate)
        {
            foreach (var pending in bladeStateQueries.Values)
            {
                pending.TrySetException(exception);
            }
        }
        lifetime.Cancel();
    }

    private void ProcessChassisPresence(ReadOnlySpan<byte> payload)
    {
        TaskCompletionSource<ChassisPresence>? pending;
        lock (chassisPresenceGate)
        {
            pending = chassisPresenceQuery;
        }

        if (pending is null)
        {
            return;
        }

        try
        {
            pending.TrySetResult(ChassisProtocolParser.ParsePresence(payload));
        }
        catch (InvalidDataException exception)
        {
            pending.TrySetException(exception);
        }
    }

    private void ProcessBladeState(ReadOnlySpan<byte> payload)
    {
        TaskCompletionSource<ChassisBladeState>? pending = null;
        if (payload.Length >= 2)
        {
            lock (bladeStateQueryGate)
            {
                bladeStateQueries.TryGetValue(payload[1], out pending);
            }
        }

        if (pending is null)
        {
            return;
        }

        try
        {
            pending.TrySetResult(ChassisProtocolParser.ParseState(payload));
        }
        catch (InvalidDataException exception)
        {
            pending.TrySetException(exception);
        }
    }

    private static TimeSpan ValidateQueryTimeout(TimeSpan? timeout)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        if (effectiveTimeout <= TimeSpan.Zero && effectiveTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return effectiveTimeout;
    }

    private void EnsureChassisSupported()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Capabilities.SupportsChassis)
        {
            throw new NotSupportedException("The selected protocol profile does not support chassis discovery.");
        }
    }

    private void ProcessVirtualMediaCredential(ReadOnlySpan<byte> payload)
    {
        byte[]? normalized = null;
        try
        {
            if (options.Encrypted)
            {
                normalized = KvmEncryptedPayloadCodec.NormalizeVirtualMediaCredential(payload, cryptography!);
                payload = normalized;
            }

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
        finally
        {
            if (normalized is not null)
            {
                CryptographicOperations.ZeroMemory(normalized);
            }
        }
    }

    private void ProcessVirtualMediaPort(ReadOnlySpan<byte> payload)
    {
        byte[]? normalized = null;
        try
        {
            if (options.Encrypted)
            {
                normalized = KvmEncryptedPayloadCodec.NormalizeVirtualMediaPort(payload, cryptography!);
                payload = normalized;
            }

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
        finally
        {
            if (normalized is not null)
            {
                CryptographicOperations.ZeroMemory(normalized);
            }
        }
    }

    private void ProcessVirtualMediaPrivilege(ReadOnlySpan<byte> payload)
    {
        try
        {
            var response = KvmVirtualMediaNegotiationParser.ParsePrivilege(payload);
            switch (KvmVirtualMediaNegotiationParser.GetPrivilegeDenial(response.State))
            {
                case KvmPrivilegeDenial.Power:
                    Volatile.Write(ref powerDenied, 1);
                    PermissionsChanged?.Invoke(this, EventArgs.Empty);
                    PrivilegeDenied?.Invoke(
                        this,
                        new KvmPrivilegeDeniedEventArgs(KvmPrivilegeOperation.Power, response.State));
                    break;
                case KvmPrivilegeDenial.VirtualMedia:
                    Volatile.Write(ref virtualMediaAccessDenied, 1);
                    virtualMediaDenied.TrySetResult(response.State);
                    PermissionsChanged?.Invoke(this, EventArgs.Empty);
                    PrivilegeDenied?.Invoke(
                        this,
                        new KvmPrivilegeDeniedEventArgs(KvmPrivilegeOperation.VirtualMedia, response.State));
                    break;
            }
        }
        catch (InvalidDataException exception)
        {
            virtualMediaCredential.TrySetException(exception);
            virtualMediaPort.TrySetException(exception);
        }
    }

    private void EnsureKvmControlAllowed()
    {
        if (!Permissions.CanControlKvm)
        {
            throw new UnauthorizedAccessException("The current iBMC privilege does not allow KVM input or console control.");
        }
    }
}
