using System.Text.Json;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using IbmcKvm.Core.Input;
using IbmcKvm.Core.Session;
using IbmcKvm.Core.Video;
using IbmcKvm.Protocol.Login;
using IbmcKvm.Protocol.Session;
using SkiaSharp;

namespace IbmcKvm.DesktopSmoke;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (GetOption(args, "--rep=") is { } repPath)
        {
            return await ReplayRepAsync(
                repPath,
                GetOption(args, "--frame="),
                GetOption(args, "--ppm="));
        }

        if (GetOption(args, "--device-host=") is { } deviceHost)
        {
            return await RunDeviceSmokeAsync(deviceHost, GetOption(args, "--device-user=") ?? string.Empty);
        }

        var readyPath = GetOption(args, "--ready=") ?? Path.Combine(Path.GetTempPath(), "ibmc-linux-smoke-ready");
        var statePath = GetOption(args, "--state=") ?? Path.Combine(Path.GetTempPath(), "ibmc-linux-smoke-state.json");
        var triggerPath = GetOption(args, "--trigger=") ?? Path.Combine(Path.GetTempPath(), "ibmc-linux-smoke-trigger");
        var failureMode = string.Equals(GetOption(args, "--reconnect="), "true", StringComparison.OrdinalIgnoreCase)
            ? LoopbackFailureMode.ReconnectSucceeds
            : LoopbackFailureMode.None;
        var duration = int.TryParse(GetOption(args, "--duration="), out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 600))
            : TimeSpan.FromMinutes(2);

        File.Delete(readyPath);
        File.Delete(statePath);
        File.Delete(triggerPath);
        await using var server = new LoopbackKvmServer(failureMode);
        await File.WriteAllTextAsync(readyPath, server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine(server.Port);
        var started = DateTimeOffset.UtcNow;
        var failureTriggered = false;
        while (DateTimeOffset.UtcNow - started < duration)
        {
            if (!failureTriggered && failureMode != LoopbackFailureMode.None && File.Exists(triggerPath))
            {
                failureTriggered = true;
                await server.TriggerFailureAsync();
            }

            server.ThrowIfFailed();
            await WriteStateAsync(statePath, server, failureTriggered);
            await Task.Delay(100);
        }

        await WriteStateAsync(statePath, server, failureTriggered);
        return 0;
    }

    private static async Task<int> ReplayRepAsync(string repPath, string? frameValue, string? ppmPath)
    {
        if (!int.TryParse(frameValue, out var targetFrame) || targetFrame < 1 || string.IsNullOrWhiteSpace(ppmPath))
        {
            Console.Error.WriteLine("--rep requires --frame=<sequence> and --ppm=<path>.");
            return 2;
        }

        var recording = await File.ReadAllBytesAsync(repPath);
        var decoder = new BlockVideoDecoder();
        var offset = 0;
        while (offset + 7 <= recording.Length)
        {
            if (recording[offset] != 0xFE || recording[offset + 1] != 0xF6)
            {
                throw new InvalidDataException($"Invalid REP record marker at offset {offset}.");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(recording.AsSpan(offset + 2, 4));
            if (length < 7 || offset > recording.Length - length)
            {
                throw new InvalidDataException($"Invalid REP record length at offset {offset}.");
            }

            if (recording[offset + 6] == 3)
            {
                if (length < 26)
                {
                    throw new InvalidDataException($"Invalid REP frame at offset {offset}.");
                }

                var sequence = BinaryPrimitives.ReadInt32BigEndian(recording.AsSpan(offset + 9, 4));
                var frame = new EncodedVideoFrame(
                    unchecked((byte)sequence),
                    (recording[offset + 7] & 1) != 0,
                    BinaryPrimitives.ReadUInt16BigEndian(recording.AsSpan(offset + 13, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(recording.AsSpan(offset + 15, 2)),
                    0,
                    0,
                    0,
                    (recording[offset + 7] >> 4) & 0x0F,
                    recording[(offset + 25)..(offset + length)]);
                var pixels = decoder.Decode(frame);
                if (sequence == targetFrame)
                {
                    if (Path.GetExtension(ppmPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        await WritePngAsync(ppmPath, frame, pixels, CancellationToken.None);
                    }
                    else
                    {
                        await WritePpmAsync(ppmPath, frame, pixels, CancellationToken.None);
                    }
                    Console.WriteLine($"REP_FRAME_SAVED sequence={sequence} path={ppmPath}");
                    return 0;
                }
            }

            offset += length;
        }

        Console.Error.WriteLine($"REP frame {targetFrame} was not found.");
        return 3;
    }

    private static async Task<int> RunDeviceSmokeAsync(string host, string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            Console.Error.WriteLine("--device-user is required.");
            return 2;
        }

        var password = await Console.In.ReadLineAsync() ?? string.Empty;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        KvmClientSession? session = null;
        try
        {
            var endpoint = IbmcEndpoint.Parse(host);
            var certificate = await ServerCertificateProbe.ProbeAsync(endpoint, timeout.Token);
            using var httpClient = IbmcLoginClient.CreateHttpClient(
                ServerCertificatePolicy.PinForSession,
                certificate.Sha256Fingerprint);
            var login = await new IbmcLoginClient(httpClient, TimeSpan.FromSeconds(20)).LoginAsync(
                endpoint,
                new LoginRequest(userName, password, ConnectionMode.Shared),
                timeout.Token);
            if (!login.IsSuccess)
            {
                Console.WriteLine($"LOGIN_FAILED code={login.RawErrorCode} kind={login.Error}");
                return 3;
            }

            Console.WriteLine("LOGIN_OK");
            var verificationKey = SessionVerificationKey.Parse(
                login.VerifyValue ?? throw new FormatException("Missing KVM verification value."));
            session = await KvmClientSession.ConnectAsync(new(
                endpoint.Host,
                login.KvmPort ?? throw new FormatException("Missing KVM port."),
                verificationKey.WireValue,
                Encrypted: login.KvmEncrypted,
                ExtendedVerifyValue: login.ExtendedVerifyValue,
                VerificationValue: login.VerifyValue,
                LoginDecryptionKey: login.DecryptionKey,
                VirtualMediaEncrypted: login.VirtualMediaEncrypted,
                Privilege: login.Privilege ?? throw new FormatException("Missing privilege.")), timeout.Token);
            Console.WriteLine("KVM_CONNECTED");
            var decoder = new BlockVideoDecoder();
            var frameIndex = 0;
            var testArrow = string.Equals(
                Environment.GetEnvironmentVariable("IBMC_SMOKE_TEST_ARROW"),
                "1",
                StringComparison.Ordinal);
            await foreach (var frame in session.ReadFramesAsync(timeout.Token))
            {
                frameIndex++;
                Console.WriteLine(
                    $"VIDEO_FRAME width={frame.Width} height={frame.Height} depth={frame.ColorDepth} difference={frame.IsDifference}");
                if (Environment.GetEnvironmentVariable("IBMC_SMOKE_FRAME_PATH") is { Length: > 0 } framePath)
                {
                    await WritePpmAsync(
                        frameIndex == 1 ? framePath : framePath + ".after.ppm",
                        frame,
                        decoder,
                        timeout.Token);
                }

                if (testArrow && frameIndex == 1)
                {
                    await session.SendKeyPulseAsync(HidModifiers.None, 0x51, cancellationToken: timeout.Token);
                    Console.WriteLine("KEYBOARD_ARROW_DOWN_SENT");
                    await Task.Delay(500, timeout.Token);
                    await session.RequestFullFrameAsync(timeout.Token);
                    continue;
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("IBMC_SMOKE_TEST_KEYBOARD"),
                        "1",
                        StringComparison.Ordinal))
                {
                    await VerifyKeyboardAsync(session, timeout.Token);
                }

                if (!testArrow || frameIndex >= 2)
                {
                    return 0;
                }
            }

            Console.Error.WriteLine("The KVM stream ended before the first frame.");
            return 4;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"DEVICE_SMOKE_FAILED {exception.GetType().Name}: {exception.Message}");
            return 5;
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    private static async Task WritePpmAsync(
        string path,
        EncodedVideoFrame frame,
        BlockVideoDecoder decoder,
        CancellationToken cancellationToken)
    {
        var pixels = decoder.Decode(frame);
        await WritePpmAsync(path, frame, pixels, cancellationToken);
    }

    private static async Task WritePpmAsync(
        string path,
        EncodedVideoFrame frame,
        byte[] pixels,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, true);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{frame.Width} {frame.Height}\n255\n");
        await stream.WriteAsync(header, cancellationToken);
        var row = new byte[checked(frame.Width * 3)];
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var source = (y * frame.Width + x) * 4;
                var destination = x * 3;
                row[destination] = pixels[source + 2];
                row[destination + 1] = pixels[source + 1];
                row[destination + 2] = pixels[source];
            }

            await stream.WriteAsync(row, cancellationToken);
        }

        Console.WriteLine($"FRAME_SAVED path={path}");
    }

    private static async Task WritePngAsync(
        string path,
        EncodedVideoFrame frame,
        byte[] pixels,
        CancellationToken cancellationToken)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            frame.Width,
            frame.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Unpremul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, true);
        await stream.WriteAsync(encoded.AsSpan().ToArray(), cancellationToken);
        Console.WriteLine($"FRAME_SAVED path={path}");
    }

    private static async Task VerifyKeyboardAsync(KvmClientSession session, CancellationToken cancellationToken)
    {
        var initial = await QueryRemoteLockKeysAsync(session, cancellationToken);
        await session.SendKeyPulseAsync(HidModifiers.None, 0x39, cancellationToken: cancellationToken);
        await Task.Delay(300, cancellationToken);
        var changed = await QueryRemoteLockKeysAsync(session, cancellationToken);
        try
        {
            if (((initial ^ changed) & RemoteLockKeys.CapsLock) == RemoteLockKeys.None)
            {
                throw new InvalidOperationException(
                    $"CapsLock did not change (initial={initial}, after={changed}).");
            }

            Console.WriteLine($"KEYBOARD_CAPS_CHANGED initial={initial} after={changed}");
        }
        finally
        {
            await session.SendKeyPulseAsync(HidModifiers.None, 0x39, cancellationToken: cancellationToken);
            await Task.Delay(300, cancellationToken);
            var restored = await QueryRemoteLockKeysAsync(session, cancellationToken);
            Console.WriteLine($"KEYBOARD_CAPS_RESTORED state={restored}");
        }
    }

    private static async Task<RemoteLockKeys> QueryRemoteLockKeysAsync(
        KvmClientSession session,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<RemoteLockKeys>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, EventArgs args) => completion.TrySetResult(session.RemoteLockKeys);
        session.RemoteLockKeysChanged += Handler;
        try
        {
            await session.RequestKeyboardStateAsync(cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        finally
        {
            session.RemoteLockKeysChanged -= Handler;
        }
    }

    private static async Task WriteStateAsync(string path, LoopbackKvmServer server, bool failureTriggered)
    {
        var commands = server.Commands;
        var state = new SmokeState(
            server.Port,
            server.ConnectionCount,
            commands.Count,
            commands.Count(command => command.Length > 0 && command[0] == 0x03),
            commands.Count(command => command.Length > 0 && command[0] == 0x05),
            commands.Count(command => command.Length > 0 && command[0] is >= 0x20 and <= 0x25),
            failureTriggered,
            commands.TakeLast(20).Select(Convert.ToHexString).ToArray());
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, path, overwrite: true);
    }

    private static string? GetOption(IEnumerable<string> args, string prefix) => args
        .FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?
        [prefix.Length..];

    private sealed record SmokeState(
        int Port,
        int ConnectionCount,
        int CommandCount,
        int KeyboardReports,
        int MouseReports,
        int PowerCommands,
        bool FailureTriggered,
        string[] RecentCommands);
}
