using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using IbmcKvm.Core.Session;
using IbmcKvm.Core.Input;
using IbmcKvm.Protocol.Profiles;
using IbmcKvm.Protocol.Session;
using IbmcKvm.Protocol.Wire;

namespace IbmcKvm.Core.Tests.Session;

public sealed class KvmClientSessionTests
{
    private const string LoginKey =
        "000102030405060708090A0B0C0D0E0F" +
        "101112131415161718191A1B1C1D1E1F";

    [Fact]
    public async Task RejectsEncryptedSessionWithoutVerificationBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                1,
                1,
                Encrypted: true,
                LoginDecryptionKey: LoginKey)));
    }

    [Fact]
    public async Task RejectsEncryptedSessionWithoutLoginKeyBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                1,
                1,
                Encrypted: true,
                VerificationValue: "123456")));
    }

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
    public async Task TreatsUnexpectedGracefulEofAsSessionFailure()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var closeConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            await closeConnection.Task.WaitAsync(cancellationToken);
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", GetPort(listener), 7),
            timeout.Token);
        closeConnection.TrySetResult();
        await serverTask;

        var exception = await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            await foreach (var _ in session.ReadFramesAsync(timeout.Token))
            {
            }
        });

        Assert.Equal("The KVM connection closed unexpectedly.", exception.Message);
        Assert.Same(exception, session.Failure);
        Assert.Equal(KvmSessionState.Faulted, session.State);
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
    public async Task ConnectsAndSendsRelativeMouseWithoutAbsoluteAcknowledgement()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(
                stream,
                cancellationToken,
                confirmedMouseMode: KvmMouseMode.Relative,
                requestedMouseMode: KvmMouseMode.Relative);
            var relative = await ReadUntilCommandPayloadAsync(stream, 0x05, cancellationToken);
            Assert.Equal(new byte[] { 0x05, 1, 3, 0xFB, 7, 0xFF }, relative);
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                GetPort(listener),
                7,
                MouseMode: KvmMouseMode.Relative),
            timeout.Token);
        await session.SendRelativeMouseAsync(3, -5, 7, -1, timeout.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.SendAbsoluteMouseAsync(0, 1, 1, cancellationToken: timeout.Token));
        await serverTask;
    }

    [Fact]
    public async Task ChangesMouseModeOnlyAfterServerAcknowledgement()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            var request = await ReadUntilCommandPayloadAsync(stream, 0x24, cancellationToken);
            Assert.Equal(new byte[] { 0x24, 0, 0, 0, 0 }, request);
            await stream.WriteAsync(BuildIncoming(0x25, 1, 0), cancellationToken);
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", GetPort(listener), 7),
            timeout.Token);
        await session.SetMouseModeAsync(KvmMouseMode.Relative, timeout.Token);
        Assert.Equal(KvmMouseMode.Relative, session.CurrentMouseMode);
        await serverTask;
    }

    [Fact]
    public async Task SynchronizesAbsoluteMouseWithOriginalSentinelCoordinates()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            Assert.Equal(
                new byte[] { 0x05, 1, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0 },
                await ReadUntilCommandPayloadAsync(stream, 0x05, cancellationToken));
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", GetPort(listener), 7),
            timeout.Token);
        await session.SynchronizeMouseAsync(timeout.Token);
        await serverTask;
    }

    [Fact]
    public async Task SynchronizesRelativeMouseWithFifteenSourceCompatibleReports()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(
                stream,
                cancellationToken,
                confirmedMouseMode: KvmMouseMode.Relative,
                requestedMouseMode: KvmMouseMode.Relative);
            for (var index = 0; index < 15; index++)
            {
                Assert.Equal(
                    new byte[] { 0x05, 1, 0, 0x81, 0x81, 0 },
                    await ReadUntilCommandPayloadAsync(stream, 0x05, cancellationToken));
            }
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                GetPort(listener),
                7,
                MouseMode: KvmMouseMode.Relative),
            timeout.Token);
        await session.SynchronizeMouseAsync(timeout.Token);
        await serverTask;
    }

    [Fact]
    public async Task AppliesConfirmedVideoQualityAndColorDepthChanges()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);

            var quality = await ReadUntilCommandPayloadAsync(stream, 0x27, cancellationToken);
            Assert.Equal(new byte[] { 0x27, 0, 70, 1, 0 }, quality);
            await stream.WriteAsync(BuildIncoming(0x28, 0, 70), cancellationToken);
            Assert.Equal(
                new byte[] { 0x08, 1 },
                await ReadUntilCommandPayloadAsync(stream, 0x08, cancellationToken));

            var colorDepth = await ReadUntilCommandPayloadAsync(stream, 0x1B, cancellationToken);
            Assert.Equal(new byte[] { 0x1B, 1, 2 }, colorDepth);
            Assert.Equal(
                new byte[] { 0x08, 1 },
                await ReadUntilCommandPayloadAsync(stream, 0x08, cancellationToken));
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", GetPort(listener), 7),
            timeout.Token);

        await session.SetVideoQualityAsync(60, committed: true, timeout.Token);
        Assert.Equal(60, session.CurrentVideoQuality);
        await session.SetColorDepthAsync(2, timeout.Token);
        Assert.Equal(2, session.CurrentColorDepth);
        await serverTask;
    }

    [Fact]
    public async Task SendsCombinationReleaseAndProcessesRemoteLockState()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);

            Assert.Equal(
                new byte[] { 0x04, 1, 1 },
                await ReadUntilCommandPayloadAsync(stream, 0x04, cancellationToken));
            await stream.WriteAsync(BuildIncoming(0x04, 1, 0x05), cancellationToken);

            Assert.Equal(
                new byte[] { 0x03, 1, 5, 0, 0x4C, 0, 0, 0, 0, 0 },
                await ReadUntilCommandPayloadAsync(stream, 0x03, cancellationToken));
            Assert.Equal(
                new byte[] { 0x03, 1, 0, 0, 0, 0, 0, 0, 0, 0 },
                await ReadUntilCommandPayloadAsync(stream, 0x03, cancellationToken));
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                GetPort(listener),
                7,
                KeyboardEncoding: KvmKeyboardEncoding.LegacyPlain),
            timeout.Token);
        var lockState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RemoteLockKeysChanged += (_, _) => lockState.TrySetResult();

        await session.RequestKeyboardStateAsync(timeout.Token);
        await lockState.Task.WaitAsync(timeout.Token);
        Assert.Equal(RemoteLockKeys.NumLock | RemoteLockKeys.ScrollLock, session.RemoteLockKeys);

        await session.SendKeyCombinationAsync(
            HidKeyCombination.CtrlAltDelete,
            TimeSpan.Zero,
            timeout.Token);
        await serverTask;
    }

    [Theory]
    [InlineData(KvmKeyboardEncoding.LegacyPlain)]
    [InlineData(KvmKeyboardEncoding.CodeKeyAes)]
    public async Task SerializesConcurrentKeyboardPulses(KvmKeyboardEncoding encoding)
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstPressReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<byte[]>();
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            while (commands.Count < 4)
            {
                var payload = await ReadUntilCommandPayloadAsync(stream, 0x03, cancellationToken);
                commands.Add(payload);
                if (commands.Count == 1)
                {
                    firstPressReceived.TrySetResult();
                }
            }
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                GetPort(listener),
                0x01020304,
                KeyboardEncoding: encoding),
            timeout.Token);

        var firstPulse = session.SendKeyPulseAsync(
            HidModifiers.None,
            0x04,
            TimeSpan.FromMilliseconds(100),
            timeout.Token);
        await firstPressReceived.Task.WaitAsync(timeout.Token);
        var secondPulse = session.SendKeyPulseAsync(
            HidModifiers.None,
            0x05,
            TimeSpan.Zero,
            timeout.Token);
        await Task.WhenAll(firstPulse, secondPulse);
        await serverTask;

        var reports = new[]
        {
            Convert.FromHexString("0000040000000000"),
            new byte[8],
            Convert.FromHexString("0000050000000000"),
            new byte[8],
        };
        Assert.Equal(
            reports.Select(report => KvmCommandBuilder.Keyboard(1, report, 0x01020304, encoding)),
            commands);
    }

    [Fact]
    public async Task DoesNotInterleavePhysicalPulseWithKeyCombination()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var combinationPressReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new List<byte[]>();
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            while (reports.Count < 4)
            {
                var payload = await ReadUntilCommandPayloadAsync(stream, 0x03, cancellationToken);
                reports.Add(payload[2..]);
                if (reports.Count == 1)
                {
                    combinationPressReceived.TrySetResult();
                }
            }
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                GetPort(listener),
                7,
                KeyboardEncoding: KvmKeyboardEncoding.LegacyPlain),
            timeout.Token);

        var combination = session.SendKeyCombinationAsync(
            HidKeyCombination.CtrlAltDelete,
            TimeSpan.FromMilliseconds(100),
            timeout.Token);
        await combinationPressReceived.Task.WaitAsync(timeout.Token);
        var physicalPulse = session.SendKeyPulseAsync(
            HidModifiers.LeftShift,
            0x04,
            TimeSpan.Zero,
            timeout.Token);
        await Task.WhenAll(combination, physicalPulse);
        await serverTask;

        Assert.Equal(
            new[]
            {
                Convert.FromHexString("05004C0000000000"),
                new byte[8],
                Convert.FromHexString("0200040000000000"),
                Convert.FromHexString("0200000000000000"),
            },
            reports);
    }

    [Fact]
    public async Task NegotiatesEncryptedSessionAndNormalizesEncryptedData()
    {
        const string materialCipher =
            "9BE37DD9A6322852855FD577ED0B73A0" +
            "AC624FE33E8DF0407F98AC07BF769C79" +
            "093417894576C00D9ECC936328DB2170";
        var reconnectToken = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();

        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            Assert.Equal(new byte[] { 0x42, 1 }, await ReadUntilCommandPayloadAsync(stream, 0x42, cancellationToken));
            await stream.WriteAsync(
                BuildIncoming(0x43, 1, 1, 3, 0, 0, 0x27, 0x10),
                cancellationToken);

            Assert.Equal(
                new byte[] { 0x44, 1, 3, 0, 0, 0x27, 0x10 },
                await ReadUntilCommandPayloadAsync(stream, 0x44, cancellationToken));

            var extended = await ReadExtendedOutgoingPacketAsync(stream, cancellationToken);
            Assert.Equal(
                Convert.FromHexString("0C829FAA0BD699D5A2A413E9BCA114061BE027FD07682FDC"),
                extended.Authenticator);
            Assert.Equal(new byte[] { 0x06, 1, 3, 1, 1 }, extended.Payload);

            var material = BuildIncoming(Convert.FromHexString("40" + "01" + materialCipher));
            await stream.WriteAsync(material.AsMemory(0, 9), cancellationToken);
            await stream.WriteAsync(material.AsMemory(9), cancellationToken);

            byte[]? mouseMode = null;
            while (mouseMode is null)
            {
                var payload = await ReadOutgoingPayloadAsync(stream, cancellationToken);
                if (payload[0] == 0x24)
                {
                    mouseMode = payload;
                }
            }

            Assert.Equal(new byte[] { 0x24, 0, 1, 0, 0 }, mouseMode);
            await stream.WriteAsync(BuildIncoming(0x08, 1, 0), cancellationToken);
            await stream.WriteAsync(BuildIncoming(0x25, 1, 1), cancellationToken);
            var encryptedReconnectToken = EncryptLoginData(reconnectToken);
            var reconnectResponse = new byte[130];
            reconnectResponse[0] = 0x40;
            reconnectResponse[1] = 1;
            encryptedReconnectToken.CopyTo(reconnectResponse.AsSpan(2));
            await stream.WriteAsync(BuildIncoming(reconnectResponse), cancellationToken);

            var metadata = new byte[17];
            metadata[2] = 7;
            BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(3, 4), 13);
            metadata[7] = 1280 >> 8;
            metadata[8] = 1280 & 0xFF;
            BinaryPrimitives.WriteUInt16BigEndian(metadata.AsSpan(9, 2), 720);
            BinaryPrimitives.WriteUInt16BigEndian(metadata.AsSpan(12, 2), 120);
            BinaryPrimitives.WriteUInt16BigEndian(metadata.AsSpan(14, 2), 240);
            metadata[16] = 3;
            var plainVideo = new byte[2 + metadata.Length];
            plainVideo[0] = 0x02;
            plainVideo[1] = 1;
            metadata.CopyTo(plainVideo.AsSpan(2));
            await stream.WriteAsync(BuildIncoming(plainVideo), cancellationToken);
            await stream.WriteAsync(
                BuildIncoming(Convert.FromHexString(
                    "02010001070D47DE27D0AD3BBE09C20BD15981481452")),
                cancellationToken);

            var seen = new HashSet<byte>();
            while (seen.Count < 5)
            {
                var payload = await ReadOutgoingPayloadAsync(stream, cancellationToken);
                switch (payload[0])
                {
                    case 0x03:
                        Assert.Equal(
                            Convert.FromHexString("03011CE62A1C88E9C99EF265C6C1B3F44016"),
                            payload);
                        seen.Add(0x03);
                        break;
                    case 0x05:
                        Assert.Equal(
                            Convert.FromHexString("0501025685722DA2D7A823B424BAA23AA22F"),
                            payload);
                        seen.Add(0x05);
                        break;
                    case 0x33:
                        Assert.Equal(
                            Convert.FromHexString("33008DA322A5E47D81800AA0FF60CB4E5E37"),
                            payload);
                        seen.Add(0x33);
                        break;
                    case 0x31:
                        Assert.Equal(new byte[] { 0x31, 1 }, payload);
                        seen.Add(0x31);
                        break;
                    case 0x35:
                        Assert.Equal(new byte[] { 0x35, 1 }, payload);
                        seen.Add(0x35);
                        break;
                }
            }

            await stream.WriteAsync(
                BuildIncoming(Convert.FromHexString(
                    "3201D95A2BC881C87396CC4D67F2EB18990590B17F9464FE2A49AF2B02293D2BF249CDEEC792D045FAFB453D9B671E6A2826")),
                cancellationToken);
            await stream.WriteAsync(
                BuildIncoming(Convert.FromHexString("36013C364617493F8B2E28EDA852D5C9CE92")),
                cancellationToken);
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                GetPort(listener),
                0x01020304,
                Encrypted: true,
                VerificationValue: "987654321",
                LoginDecryptionKey: LoginKey,
                VirtualMediaBladeNumber: 1,
                VirtualMediaEncrypted: false),
            timeout.Token);

        await session.SendKeyboardAsync(Convert.FromHexString("05004C0000000000"), timeout.Token);
        await session.SendAbsoluteMouseAsync(3, 3000, 1500, -1, timeout.Token);
        await session.SendPowerAsync(KvmPowerAction.SafeRestart, timeout.Token);
        var endpointTask = session.GetVirtualMediaEndpointAsync(cancellationToken: timeout.Token);

        await using var frames = session.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal(Enumerable.Range(1, 13).Select(static value => (byte)value), frames.Current.EncodedData[1..]);

        var endpoint = await endpointTask;
        Assert.Equal(0x1234, endpoint.Port);
        Assert.Equal(Enumerable.Range(0x20, 20).Select(static value => (byte)value), endpoint.Credential.ToArray());
        Assert.Equal(Enumerable.Range(0x34, 16).Select(static value => (byte)value), endpoint.Salt.ToArray());
        Assert.False(endpoint.Encrypted);
        Assert.Equal(reconnectToken, session.CopyReconnectToken());
        await serverTask;
    }

    [Fact]
    public async Task ConnectsEncryptedImanaSessionWithSessionIdFraming()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            var connect = await ReadImanaOutgoingPacketAsync(stream, encrypted: true, cancellationToken);
            Assert.Equal(
                Convert.FromHexString("D1484EA544E9DA91F5326203BDE774B02361DBDAE9A85F35"),
                connect.SessionId);
            Assert.Equal(new byte[] { 0x06, 1, 3, 0 }, connect.Payload);

            byte[]? mouseMode = null;
            while (mouseMode is null)
            {
                var packet = await ReadImanaOutgoingPacketAsync(stream, encrypted: true, cancellationToken);
                if (packet.Payload[0] == 0x24)
                {
                    mouseMode = packet.Payload;
                }
            }

            Assert.Equal(new byte[] { 0x24, 0, 1, 0, 0 }, mouseMode);
            await stream.WriteAsync(BuildImanaIncoming(0x08, 1, 0), cancellationToken);
            await stream.WriteAsync(BuildImanaIncoming(0x25, 1, 1), cancellationToken);

            var metadata = new byte[17];
            metadata[2] = 7;
            BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(3, 4), 13);
            metadata[7] = 1280 >> 8;
            metadata[8] = 1280 & 0xFF;
            BinaryPrimitives.WriteUInt16BigEndian(metadata.AsSpan(9, 2), 720);
            metadata[16] = 3;
            var firstVideo = new byte[2 + metadata.Length];
            firstVideo[0] = 0x02;
            firstVideo[1] = 1;
            metadata.CopyTo(firstVideo.AsSpan(2));
            await stream.WriteAsync(BuildImanaIncoming(firstVideo), cancellationToken);
            await stream.WriteAsync(
                BuildImanaIncoming(Convert.FromHexString(
                    "02010001070DA1B84B9632F900CCD66A52A4A771DB1D")),
                cancellationToken);

            while (true)
            {
                var packet = await ReadImanaOutgoingPacketAsync(stream, encrypted: true, cancellationToken);
                if (packet.Payload[0] != 0x03)
                {
                    continue;
                }

                Assert.Equal(
                    Convert.FromHexString("030198CE1197C011EAF8088BB24D6A784B74"),
                    packet.Payload);
                break;
            }
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                GetPort(listener),
                7,
                Encrypted: true,
                ProtocolProfile: ImanaKvmProtocolProfile.Instance),
            timeout.Token);

        await session.SendKeyboardAsync(Convert.FromHexString("05004C0000000000"), timeout.Token);
        await using var frames = session.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal(Enumerable.Range(1, 13).Select(static value => (byte)value), frames.Current.EncodedData[1..]);
        await serverTask;
    }

    [Fact]
    public async Task CapturesAndReusesReconnectToken()
    {
        var token = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            byte[]? connect = null;
            byte[]? mouse = null;
            while (connect is null || mouse is null)
            {
                var payload = await ReadOutgoingPayloadAsync(stream, cancellationToken);
                if (payload[0] == 0x06)
                {
                    connect = payload;
                }
                else if (payload[0] == 0x24)
                {
                    mouse = payload;
                }
            }

            var response = new byte[130];
            response[0] = 0x40;
            response[1] = 1;
            token.CopyTo(response.AsSpan(2));
            await stream.WriteAsync(BuildIncoming(response), cancellationToken);
            await stream.WriteAsync(BuildIncoming(0x08, 1, 0), cancellationToken);
            await stream.WriteAsync(BuildIncoming(0x25, 1, 1), cancellationToken);
        }, timeout.Token);

        ReadOnlyMemory<byte> captured;
        await using (var session = await KvmClientSession.ConnectAsync(
                         new KvmConnectionOptions("127.0.0.1", GetPort(listener), 7),
                         timeout.Token))
        {
            captured = session.ReconnectToken;
            Assert.Equal(token, captured.ToArray());
        }

        await serverTask;

        var reconnectListener = StartListener();
        var reconnectServer = RunServerAsync(reconnectListener, async (stream, cancellationToken) =>
        {
            byte[]? connect = null;
            byte[]? mouse = null;
            while (connect is null || mouse is null)
            {
                var payload = await ReadOutgoingPayloadAsync(stream, cancellationToken);
                if (payload[0] == 0x06)
                {
                    connect = payload;
                }
                else if (payload[0] == 0x24)
                {
                    mouse = payload;
                }
            }

            Assert.Equal(133, connect.Length);
            Assert.Equal(token, connect[5..]);
            await stream.WriteAsync(BuildIncoming(0x08, 1, 0), cancellationToken);
            await stream.WriteAsync(BuildIncoming(0x25, 1, 1), cancellationToken);
        }, timeout.Token);

        await using var reconnected = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                GetPort(reconnectListener),
                7,
                ReconnectToken: captured),
            timeout.Token);
        await reconnectServer;
    }

    [Fact]
    public async Task ReconnectMethodPreservesColorDepthAndMouseMode()
    {
        var token = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstListener = StartListener();
        var port = GetPort(firstListener);
        var firstServer = RunServerAsync(
            firstListener,
            (stream, cancellationToken) => CompleteHandshakeAsync(
                stream,
                cancellationToken,
                confirmedMouseMode: KvmMouseMode.Relative,
                requestedMouseMode: KvmMouseMode.Relative),
            timeout.Token);

        await using var original = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                port,
                7,
                ColorDepth: 2,
                MouseMode: KvmMouseMode.Relative),
            timeout.Token);
        await firstServer;

        var reconnectListener = new TcpListener(IPAddress.Loopback, port);
        reconnectListener.Start();
        var reconnectServer = RunServerAsync(reconnectListener, async (stream, cancellationToken) =>
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

            Assert.Equal(2, connect[2]);
            Assert.Equal(token, connect[5..]);
            Assert.Equal(new byte[] { 0x24, 0, 0, 0, 0 }, mouseMode);
            await stream.WriteAsync(BuildIncoming(0x08, 1, 0), cancellationToken);
            await stream.WriteAsync(BuildIncoming(0x25, 1, 0), cancellationToken);
        }, timeout.Token);

        await using var reconnected = await original.ReconnectAsync(token, timeout.Token);

        Assert.Equal(2, reconnected.CurrentColorDepth);
        Assert.Equal(KvmMouseMode.Relative, reconnected.CurrentMouseMode);
        await reconnectServer;
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
        Assert.False(session.Permissions.CanUseVirtualMedia);
        Assert.True(session.Permissions.CanControlPower);
        await serverTask;
    }

    [Fact]
    public async Task RejectsPowerControlForUserPrivilege()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(
            listener,
            (stream, cancellationToken) => CompleteHandshakeAsync(stream, cancellationToken),
            timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                GetPort(listener),
                7,
                Privilege: (int)KvmPrivilegeLevel.User),
            timeout.Token);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await session.SendPowerAsync(KvmPowerAction.PowerOff, timeout.Token));
        Assert.True(session.Permissions.CanControlKvm);
        Assert.False(session.Permissions.CanControlPower);
        await serverTask;
    }

    [Fact]
    public async Task ProcessesPowerPrivilegeDenialAndRaisesOperationEvent()
    {
        var listener = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            await ReadUntilCommandAsync(stream, 0x04, cancellationToken);
            await stream.WriteAsync(BuildIncoming(0x51, 0, 1), cancellationToken);
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", GetPort(listener), 7),
            timeout.Token);
        var denied = new TaskCompletionSource<KvmPrivilegeDeniedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PrivilegeDenied += (_, args) => denied.TrySetResult(args);

        await session.RequestKeyboardStateAsync(timeout.Token);
        var result = await denied.Task.WaitAsync(timeout.Token);

        Assert.Equal(KvmPrivilegeOperation.Power, result.Operation);
        Assert.Equal(1, result.State);
        Assert.False(session.Permissions.CanControlPower);
        Assert.True(session.Permissions.CanUseVirtualMedia);
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
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", GetPort(listener), 11),
            timeout.Token);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            session.GetVirtualMediaEndpointAsync(TimeSpan.FromMilliseconds(50), timeout.Token));
        Assert.Equal(KvmSessionState.Connected, session.State);
        await serverTask;
    }

    [Fact]
    public async Task RefreshesFourteenSlotChassisThroughBoundedSessionQueries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = StartListener();
        var port = GetPort(listener);
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            await CompleteHandshakeAsync(stream, cancellationToken);
            Assert.Equal(new byte[] { 0x0B }, await ReadUntilCommandPayloadAsync(stream, 0x0B, cancellationToken));
            await stream.WriteAsync(BuildIncoming(0x01, 0x00, 0x06), cancellationToken);

            for (var count = 0; count < 2; count++)
            {
                var request = await ReadUntilCommandPayloadAsync(stream, 0x14, cancellationToken);
                Assert.True(request[1] is 1 or 2);
                await stream.WriteAsync(
                    BuildIncoming(
                        0x15,
                        request[1],
                        0xB0,
                        0,
                        127,
                        0,
                        0,
                        1,
                        checked((byte)(port >> 8)),
                        checked((byte)(port & 0xFF))),
                    cancellationToken);
            }
        }, timeout.Token);

        await using var session = await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions("127.0.0.1", port, 7),
            timeout.Token);
        var snapshot = await session.RefreshChassisAsync(
            exclusive: false,
            TimeSpan.FromSeconds(2),
            timeout.Token);

        Assert.Equal(14, snapshot.Blades.Length);
        Assert.Equal(ChassisBladeStatus.Available, snapshot[1].Status);
        Assert.Equal(ChassisBladeStatus.Available, snapshot[2].Status);
        Assert.Equal(ChassisBladeStatus.Absent, snapshot[3].Status);
        await serverTask;
    }

    [Fact]
    public async Task MonitorHandshakeUsesOriginalCommandsAndRemainsReadOnly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = StartListener();
        var serverTask = RunServerAsync(listener, async (stream, cancellationToken) =>
        {
            Assert.Equal(
                new byte[] { 0x17, 2, 1 },
                await ReadUntilCommandPayloadAsync(stream, 0x17, cancellationToken));
            await stream.WriteAsync(BuildIncoming(0x08, 2, 0), cancellationToken);
            Assert.Equal(
                new byte[] { 0x18, 2, 1 },
                await ReadUntilCommandPayloadAsync(stream, 0x18, cancellationToken));
        }, timeout.Token);

        await using (var session = await KvmClientSession.ConnectAsync(
                         new KvmConnectionOptions(
                             "127.0.0.1",
                             GetPort(listener),
                             7,
                             BladeNumber: 2,
                             SessionMode: KvmBladeSessionMode.Monitor),
                         timeout.Token))
        {
            Assert.Equal(KvmBladeSessionMode.Monitor, session.SessionMode);
            Assert.False(session.Permissions.CanControlKvm);
            Assert.False(session.Permissions.CanControlPower);
            Assert.False(session.Permissions.CanUseVirtualMedia);
        }

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
        KvmMouseMode confirmedMouseMode = KvmMouseMode.Absolute,
        KvmMouseMode requestedMouseMode = KvmMouseMode.Absolute)
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

        Assert.Equal(new byte[] { 0x24, 0, (byte)requestedMouseMode, 0, 0 }, mouseMode);
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

    private static async Task<byte[]> ReadUntilCommandPayloadAsync(
        NetworkStream stream,
        byte command,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var payload = await ReadOutgoingPayloadAsync(stream, cancellationToken);
            if (payload[0] == command)
            {
                return payload;
            }
        }
    }

    private static async Task<(byte[] Authenticator, byte[] Payload)> ReadExtendedOutgoingPacketAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var header = await ReadExactlyAsync(stream, 4, cancellationToken);
        Assert.Equal(new byte[] { 0xFE, 0xF6 }, header[..2]);
        var wireLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        Assert.True((wireLength & 0x8000) != 0);
        var bodyLength = wireLength & 0x7FFF;
        var body = await ReadExactlyAsync(stream, 24 + bodyLength, cancellationToken);
        var authenticator = body[..24];
        var payload = body[26..];
        Assert.Equal(
            Crc16High.Compute(payload),
            BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(24, 2)));
        return (authenticator, payload);
    }

    private static async Task<(byte[] SessionId, byte[] Payload)> ReadImanaOutgoingPacketAsync(
        NetworkStream stream,
        bool encrypted,
        CancellationToken cancellationToken)
    {
        var header = await ReadExactlyAsync(stream, 4, cancellationToken);
        Assert.Equal(new byte[] { 0xFE, 0xF6 }, header[..2]);
        Assert.Equal(encrypted ? 0x80 : 0, header[2]);
        var bodyLength = header[3];
        var sessionIdLength = encrypted ? 24 : 4;
        var body = await ReadExactlyAsync(stream, sessionIdLength + bodyLength, cancellationToken);
        var sessionId = body[..sessionIdLength];
        var payload = body[(sessionIdLength + 2)..];
        Assert.Equal(
            Crc16High.Compute(payload),
            BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(sessionIdLength, 2)));
        return (sessionId, payload);
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

    private static byte[] BuildImanaIncoming(params byte[] payload)
    {
        var result = new byte[payload.Length + 6];
        result[0] = 0xFE;
        result[1] = 0xF6;
        result[3] = checked((byte)(payload.Length + 2));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), Crc16High.Compute(payload));
        payload.CopyTo(result.AsSpan(6));
        return result;
    }

    private static byte[] EncryptLoginData(ReadOnlySpan<byte> plaintext)
    {
        var loginKey = Convert.FromHexString(LoginKey);
        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = loginKey[..16];
            aes.IV = loginKey[16..];
            using var encryptor = aes.CreateEncryptor();
            var bytes = plaintext.ToArray();
            try
            {
                return encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(loginKey);
        }
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
