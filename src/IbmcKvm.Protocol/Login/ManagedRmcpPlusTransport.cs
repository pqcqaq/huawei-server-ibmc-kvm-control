using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace IbmcKvm.Protocol.Login;

#pragma warning disable CA5350 // RMCP+ RAKP and integrity algorithms are mandated by IPMI 2.0.

public sealed class ManagedRmcpPlusTransport : IRmcpOemTransport
{
    private readonly TimeSpan timeout;
    private readonly int retryCount;

    public ManagedRmcpPlusTransport(TimeSpan? timeout = null, int retryCount = 2)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (this.timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(retryCount, 1);
        this.retryCount = retryCount;
    }

    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(
        string host,
        int port,
        string userName,
        ReadOnlyMemory<char> password,
        RmcpOemCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(command);
        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var userNameBytes = Encoding.ASCII.GetBytes(userName);
        var passwordBytes = new byte[Encoding.UTF8.GetByteCount(password.Span)];
        Encoding.UTF8.GetBytes(password.Span, passwordBytes);
        try
        {
            if (userNameBytes.Length is 0 or > 16)
            {
                throw new ArgumentException("An IPMI 2.0 user name must contain 1 to 16 ASCII bytes.", nameof(userName));
            }

            if (passwordBytes.Length is 0 or > 20)
            {
                throw new ArgumentException("An IPMI 2.0 password must contain 1 to 20 UTF-8 bytes.", nameof(password));
            }

            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(static item =>
                    item.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                ?? throw new SocketException((int)SocketError.HostNotFound);
            using var udp = new UdpClient(address.AddressFamily);
            udp.Connect(new IPEndPoint(address, port));
            using var session = await RmcpPlusSession.ConnectAsync(
                udp,
                userNameBytes,
                passwordBytes,
                timeout,
                retryCount,
                cancellationToken).ConfigureAwait(false);
            return await session.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(userNameBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private sealed class RmcpPlusSession : IDisposable
    {
        private const byte AdministratorRole = 0x14;
        private readonly UdpClient udp;
        private readonly byte[] userName;
        private readonly byte[] k1;
        private readonly byte[] k2;
        private readonly uint consoleSessionId;
        private readonly uint managedSessionId;
        private readonly TimeSpan timeout;
        private readonly int retryCount;
        private uint outboundSequence = 1;
        private byte requestSequence;
        private int disposed;

        private RmcpPlusSession(
            UdpClient udp,
            ReadOnlySpan<byte> userName,
            ReadOnlySpan<byte> k1,
            ReadOnlySpan<byte> k2,
            uint consoleSessionId,
            uint managedSessionId,
            TimeSpan timeout,
            int retryCount)
        {
            this.udp = udp;
            this.userName = userName.ToArray();
            this.k1 = k1.ToArray();
            this.k2 = k2.ToArray();
            this.consoleSessionId = consoleSessionId;
            this.managedSessionId = managedSessionId;
            this.timeout = timeout;
            this.retryCount = retryCount;
        }

        public static async Task<RmcpPlusSession> ConnectAsync(
            UdpClient udp,
            ReadOnlyMemory<byte> userName,
            ReadOnlyMemory<byte> password,
            TimeSpan timeout,
            int retryCount,
            CancellationToken cancellationToken)
        {
            var tag = RandomByte();
            var consoleSessionId = RandomUInt32();
            var openRequest = RmcpPlusCodec.BuildOpenSessionRequest(tag, consoleSessionId);
            var openResponse = await ExchangeAsync(
                udp,
                openRequest,
                RmcpPlusPayloadType.OpenSessionResponse,
                timeout,
                retryCount,
                cancellationToken).ConfigureAwait(false);
            var open = RmcpPlusCodec.ParseOpenSessionResponse(openResponse.Span, tag, consoleSessionId);

            var consoleRandom = RandomNumberGenerator.GetBytes(16);
            byte[]? sik = null;
            byte[]? k1 = null;
            byte[]? k2 = null;
            try
            {
                var rakp1 = RmcpPlusCodec.BuildRakpMessage1(
                    tag,
                    open.ManagedSessionId,
                    consoleRandom,
                    AdministratorRole,
                    userName.Span);
                var rakp2Packet = await ExchangeAsync(
                    udp,
                    rakp1,
                    RmcpPlusPayloadType.RakpMessage2,
                    timeout,
                    retryCount,
                    cancellationToken).ConfigureAwait(false);
                var rakp2 = RmcpPlusCodec.ParseRakpMessage2(rakp2Packet.Span, tag, consoleSessionId);
                RmcpPlusCodec.ValidateRakpMessage2(
                    rakp2,
                    password.Span,
                    consoleSessionId,
                    open.ManagedSessionId,
                    consoleRandom,
                    AdministratorRole,
                    userName.Span);

                sik = RmcpPlusCodec.DeriveSik(
                    password.Span,
                    consoleRandom,
                    rakp2.ManagedRandom,
                    AdministratorRole,
                    userName.Span);
                k1 = RmcpPlusCodec.DeriveSessionKey(sik, 0x01);
                k2 = RmcpPlusCodec.DeriveSessionKey(sik, 0x02);
                var rakp3 = RmcpPlusCodec.BuildRakpMessage3(
                    tag,
                    open.ManagedSessionId,
                    password.Span,
                    rakp2.ManagedRandom,
                    consoleSessionId,
                    AdministratorRole,
                    userName.Span);
                var rakp4Packet = await ExchangeAsync(
                    udp,
                    rakp3,
                    RmcpPlusPayloadType.RakpMessage4,
                    timeout,
                    retryCount,
                    cancellationToken).ConfigureAwait(false);
                RmcpPlusCodec.ValidateRakpMessage4(
                    rakp4Packet.Span,
                    tag,
                    consoleSessionId,
                    sik,
                    consoleRandom,
                    open.ManagedSessionId,
                    rakp2.ManagedGuid);

                return new RmcpPlusSession(
                    udp,
                    userName.Span,
                    k1,
                    k2,
                    consoleSessionId,
                    open.ManagedSessionId,
                    timeout,
                    retryCount);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(consoleRandom);
                if (sik is not null)
                {
                    CryptographicOperations.ZeroMemory(sik);
                }

                if (k1 is not null)
                {
                    CryptographicOperations.ZeroMemory(k1);
                }

                if (k2 is not null)
                {
                    CryptographicOperations.ZeroMemory(k2);
                }
            }
        }

        public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(
            RmcpOemCommand command,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var ipmi = RmcpPlusCodec.BuildIpmiRequest(
                command,
                requestSequence++);
            var packet = RmcpPlusCodec.BuildSecureIpmiPacket(
                managedSessionId,
                outboundSequence++,
                ipmi,
                k1,
                k2);
            try
            {
                var response = await ExchangeAsync(
                    udp,
                    packet,
                    RmcpPlusPayloadType.Ipmi,
                    timeout,
                    retryCount,
                    cancellationToken).ConfigureAwait(false);
                return RmcpPlusCodec.ParseSecureIpmiResponse(
                    response.Span,
                    consoleSessionId,
                    command,
                    k1,
                    k2);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ipmi);
                CryptographicOperations.ZeroMemory(packet);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(userName);
            CryptographicOperations.ZeroMemory(k1);
            CryptographicOperations.ZeroMemory(k2);
        }

        private static async Task<ReadOnlyMemory<byte>> ExchangeAsync(
            UdpClient udp,
            ReadOnlyMemory<byte> request,
            RmcpPlusPayloadType expectedType,
            TimeSpan timeout,
            int retryCount,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < retryCount; attempt++)
            {
                await udp.SendAsync(request, cancellationToken).ConfigureAwait(false);
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);
                try
                {
                    while (true)
                    {
                        var response = await udp.ReceiveAsync(timeoutSource.Token).ConfigureAwait(false);
                        if (RmcpPlusCodec.GetPayloadType(response.Buffer) == expectedType)
                        {
                            return response.Buffer;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt + 1 < retryCount)
                {
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The RMCP+ endpoint did not return the expected response in time.");
        }

        private static byte RandomByte() => RandomNumberGenerator.GetBytes(1)[0];

        private static uint RandomUInt32()
        {
            Span<byte> bytes = stackalloc byte[4];
            RandomNumberGenerator.Fill(bytes);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return value == 0 ? 1u : value;
        }
    }
}

public enum RmcpPlusPayloadType : byte
{
    Ipmi = 0x00,
    OpenSessionRequest = 0x10,
    OpenSessionResponse = 0x11,
    RakpMessage1 = 0x12,
    RakpMessage2 = 0x13,
    RakpMessage3 = 0x14,
    RakpMessage4 = 0x15,
}

public sealed record RmcpPlusOpenSession(uint ManagedSessionId);

public sealed record RmcpPlusRakpMessage2(byte[] ManagedRandom, byte[] ManagedGuid, byte[] AuthenticationCode);

public static class RmcpPlusCodec
{
    private static ReadOnlySpan<byte> RmcpHeader => [0x06, 0x00, 0xFF, 0x07];

    public static RmcpPlusPayloadType GetPayloadType(ReadOnlySpan<byte> packet)
    {
        ValidatePacketHeader(packet);
        return (RmcpPlusPayloadType)(packet[5] & 0x3F);
    }

    public static byte[] BuildOpenSessionRequest(byte tag, uint consoleSessionId)
    {
        var payload = new byte[32];
        payload[0] = tag;
        payload[1] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), consoleSessionId);
        WriteAlgorithm(payload.AsSpan(8, 8), 0, 1);
        WriteAlgorithm(payload.AsSpan(16, 8), 1, 1);
        WriteAlgorithm(payload.AsSpan(24, 8), 2, 1);
        return BuildSessionPacket(RmcpPlusPayloadType.OpenSessionRequest, 0, 0, payload);
    }

    public static RmcpPlusOpenSession ParseOpenSessionResponse(
        ReadOnlySpan<byte> packet,
        byte expectedTag,
        uint expectedConsoleSessionId)
    {
        var payload = GetPayload(packet, RmcpPlusPayloadType.OpenSessionResponse);
        if (payload.Length < 36 || payload[0] != expectedTag)
        {
            throw new InvalidDataException("The RMCP+ open-session response is malformed.");
        }

        ThrowForStatus(payload[1], "open session");
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)) != expectedConsoleSessionId)
        {
            throw new InvalidDataException("The RMCP+ open-session response has the wrong console session ID.");
        }

        ValidateAlgorithm(payload.Slice(12, 8), 0, 1);
        ValidateAlgorithm(payload.Slice(20, 8), 1, 1);
        ValidateAlgorithm(payload.Slice(28, 8), 2, 1);
        var managedSessionId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        if (managedSessionId == 0)
        {
            throw new InvalidDataException("The RMCP+ managed session ID is zero.");
        }

        return new RmcpPlusOpenSession(managedSessionId);
    }

    public static byte[] BuildRakpMessage1(
        byte tag,
        uint managedSessionId,
        ReadOnlySpan<byte> consoleRandom,
        byte role,
        ReadOnlySpan<byte> userName)
    {
        if (consoleRandom.Length != 16 || userName.Length is 0 or > 16)
        {
            throw new ArgumentException("The RAKP message-1 random or user name length is invalid.");
        }

        var payload = new byte[28 + userName.Length];
        payload[0] = tag;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), managedSessionId);
        consoleRandom.CopyTo(payload.AsSpan(8));
        payload[24] = role;
        payload[27] = checked((byte)userName.Length);
        userName.CopyTo(payload.AsSpan(28));
        return BuildSessionPacket(RmcpPlusPayloadType.RakpMessage1, 0, 0, payload);
    }

    public static RmcpPlusRakpMessage2 ParseRakpMessage2(
        ReadOnlySpan<byte> packet,
        byte expectedTag,
        uint expectedConsoleSessionId)
    {
        var payload = GetPayload(packet, RmcpPlusPayloadType.RakpMessage2);
        if (payload.Length != 60 || payload[0] != expectedTag)
        {
            throw new InvalidDataException("The RAKP message 2 response is malformed.");
        }

        ThrowForStatus(payload[1], "RAKP message 2");
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)) != expectedConsoleSessionId)
        {
            throw new InvalidDataException("RAKP message 2 has the wrong console session ID.");
        }

        return new RmcpPlusRakpMessage2(
            payload.Slice(8, 16).ToArray(),
            payload.Slice(24, 16).ToArray(),
            payload.Slice(40, 20).ToArray());
    }

    public static void ValidateRakpMessage2(
        RmcpPlusRakpMessage2 message,
        ReadOnlySpan<byte> password,
        uint consoleSessionId,
        uint managedSessionId,
        ReadOnlySpan<byte> consoleRandom,
        byte role,
        ReadOnlySpan<byte> userName)
    {
        var input = new byte[4 + 4 + 16 + 16 + 16 + 1 + 1 + userName.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(input, consoleSessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), managedSessionId);
        consoleRandom.CopyTo(input.AsSpan(8));
        message.ManagedRandom.CopyTo(input.AsSpan(24));
        message.ManagedGuid.CopyTo(input.AsSpan(40));
        input[56] = role;
        input[57] = checked((byte)userName.Length);
        userName.CopyTo(input.AsSpan(58));
        var expected = HmacSha1(password, input);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expected, message.AuthenticationCode))
            {
                throw new UnauthorizedAccessException("The RAKP message 2 authentication code is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    public static byte[] DeriveSik(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> consoleRandom,
        ReadOnlySpan<byte> managedRandom,
        byte role,
        ReadOnlySpan<byte> userName)
    {
        var input = new byte[16 + 16 + 1 + 1 + userName.Length];
        consoleRandom.CopyTo(input);
        managedRandom.CopyTo(input.AsSpan(16));
        input[32] = role;
        input[33] = checked((byte)userName.Length);
        userName.CopyTo(input.AsSpan(34));
        try
        {
            return HmacSha1(password, input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public static byte[] DeriveSessionKey(ReadOnlySpan<byte> sik, byte constant)
    {
        Span<byte> input = stackalloc byte[20];
        input.Fill(constant);
        return HmacSha1(sik, input);
    }

    public static byte[] BuildRakpMessage3(
        byte tag,
        uint managedSessionId,
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> managedRandom,
        uint consoleSessionId,
        byte role,
        ReadOnlySpan<byte> userName)
    {
        var input = new byte[16 + 4 + 1 + 1 + userName.Length];
        managedRandom.CopyTo(input);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(16), consoleSessionId);
        input[20] = role;
        input[21] = checked((byte)userName.Length);
        userName.CopyTo(input.AsSpan(22));
        var auth = HmacSha1(password, input);
        try
        {
            var payload = new byte[28];
            payload[0] = tag;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), managedSessionId);
            auth.CopyTo(payload.AsSpan(8));
            return BuildSessionPacket(RmcpPlusPayloadType.RakpMessage3, 0, 0, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(auth);
        }
    }

    public static void ValidateRakpMessage4(
        ReadOnlySpan<byte> packet,
        byte expectedTag,
        uint expectedConsoleSessionId,
        ReadOnlySpan<byte> sik,
        ReadOnlySpan<byte> consoleRandom,
        uint managedSessionId,
        ReadOnlySpan<byte> managedGuid)
    {
        var payload = GetPayload(packet, RmcpPlusPayloadType.RakpMessage4);
        if (payload.Length != 20 || payload[0] != expectedTag)
        {
            throw new InvalidDataException("The RAKP message 4 response is malformed.");
        }

        ThrowForStatus(payload[1], "RAKP message 4");
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)) != expectedConsoleSessionId)
        {
            throw new InvalidDataException("RAKP message 4 has the wrong console session ID.");
        }

        var input = new byte[16 + 4 + 16];
        consoleRandom.CopyTo(input);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(16), managedSessionId);
        managedGuid.CopyTo(input.AsSpan(20));
        var expected = HmacSha1(sik, input);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 12), payload.Slice(8, 12)))
            {
                throw new UnauthorizedAccessException("The RAKP message 4 integrity code is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    public static byte[] BuildIpmiRequest(RmcpOemCommand command, byte requestSequence)
    {
        var data = command.Data.Span;
        var message = new byte[7 + data.Length];
        message[0] = 0x20;
        message[1] = checked((byte)(command.NetFunction << 2));
        message[2] = Checksum(message.AsSpan(0, 2));
        message[3] = 0x81;
        message[4] = checked((byte)((requestSequence & 0x3F) << 2));
        message[5] = command.Command;
        data.CopyTo(message.AsSpan(6));
        message[^1] = Checksum(message.AsSpan(3, message.Length - 4));
        return message;
    }

    public static byte[] BuildSecureIpmiPacket(
        uint managedSessionId,
        uint sequence,
        ReadOnlySpan<byte> ipmiPayload,
        ReadOnlySpan<byte> k1,
        ReadOnlySpan<byte> k2)
    {
        var confidentialityPadding = (16 - ((ipmiPayload.Length + 2) % 16)) % 16;
        var plaintext = new byte[ipmiPayload.Length + confidentialityPadding + 2];
        ipmiPayload.CopyTo(plaintext);
        for (var index = 0; index < confidentialityPadding; index++)
        {
            plaintext[ipmiPayload.Length + index] = checked((byte)(index + 1));
        }

        plaintext[^2] = checked((byte)confidentialityPadding);
        plaintext[^1] = 0x07;
        var iv = RandomNumberGenerator.GetBytes(16);
        var ciphertext = AesTransform(plaintext, k2[..16], iv, encrypt: true);
        try
        {
            var encryptedPayload = new byte[iv.Length + ciphertext.Length];
            iv.CopyTo(encryptedPayload, 0);
            ciphertext.CopyTo(encryptedPayload, iv.Length);
            var packet = BuildSessionPacket(
                RmcpPlusPayloadType.Ipmi,
                managedSessionId,
                sequence,
                encryptedPayload,
                authenticated: true,
                encrypted: true,
                k1);
            CryptographicOperations.ZeroMemory(encryptedPayload);
            return packet;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public static byte[] ParseSecureIpmiResponse(
        ReadOnlySpan<byte> packet,
        uint expectedConsoleSessionId,
        RmcpOemCommand request,
        ReadOnlySpan<byte> k1,
        ReadOnlySpan<byte> k2)
    {
        ValidatePacketHeader(packet);
        if ((packet[5] & 0xC0) != 0xC0 || GetPayloadType(packet) != RmcpPlusPayloadType.Ipmi)
        {
            throw new InvalidDataException("The RMCP+ response is not an authenticated encrypted IPMI payload.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(6, 4)) != expectedConsoleSessionId)
        {
            throw new InvalidDataException("The RMCP+ response has the wrong console session ID.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(14, 2));
        var payloadEnd = 16 + payloadLength;
        if (payloadLength < 32 || payloadEnd + 14 > packet.Length)
        {
            throw new InvalidDataException("The RMCP+ encrypted payload length is invalid.");
        }

        var authenticationCode = packet[^12..];
        var expectedAuth = HmacSha1(k1, packet.Slice(4, packet.Length - 4 - 12));
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedAuth.AsSpan(0, 12), authenticationCode))
            {
                throw new UnauthorizedAccessException("The RMCP+ response integrity code is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedAuth);
        }

        var encryptedPayload = packet.Slice(16, payloadLength);
        var plaintext = AesTransform(encryptedPayload[16..], k2[..16], encryptedPayload[..16], encrypt: false);
        try
        {
            if (plaintext.Length < 2 || plaintext[^1] != 0x07 || plaintext[^2] > plaintext.Length - 2)
            {
                throw new InvalidDataException("The RMCP+ confidentiality padding is invalid.");
            }

            var ipmi = plaintext.AsSpan(0, plaintext.Length - plaintext[^2] - 2);
            return ParseIpmiResponse(ipmi, request);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] ParseIpmiResponse(ReadOnlySpan<byte> message, RmcpOemCommand request)
    {
        if (message.Length < 8 || message[5] != request.Command ||
            message[1] != checked((byte)((request.NetFunction + 1) << 2)) ||
            Checksum(message[..2]) != message[2] ||
            Checksum(message[3..^1]) != message[^1])
        {
            throw new InvalidDataException("The IPMI response payload is malformed.");
        }

        if (message[6] != 0)
        {
            throw new InvalidOperationException($"The IPMI command failed with completion code 0x{message[6]:X2}.");
        }

        return message[7..^1].ToArray();
    }

    private static byte[] BuildSessionPacket(
        RmcpPlusPayloadType payloadType,
        uint sessionId,
        uint sequence,
        ReadOnlySpan<byte> payload,
        bool authenticated = false,
        bool encrypted = false,
        ReadOnlySpan<byte> k1 = default)
    {
        if (payload.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        var trailerLength = authenticated ? IntegrityTrailerLength(12 + payload.Length) + 12 : 0;
        var packet = new byte[16 + payload.Length + trailerLength];
        RmcpHeader.CopyTo(packet);
        packet[4] = 0x06;
        packet[5] = (byte)payloadType;
        if (authenticated)
        {
            packet[5] |= 0x40;
        }

        if (encrypted)
        {
            packet[5] |= 0x80;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), checked((ushort)payload.Length));
        payload.CopyTo(packet.AsSpan(16));
        if (authenticated)
        {
            var paddingLength = trailerLength - 14;
            packet.AsSpan(16 + payload.Length, paddingLength).Fill(0xFF);
            packet[16 + payload.Length + paddingLength] = checked((byte)paddingLength);
            packet[17 + payload.Length + paddingLength] = 0x07;
            var auth = HmacSha1(k1, packet.AsSpan(4, packet.Length - 4 - 12));
            try
            {
                auth.AsSpan(0, 12).CopyTo(packet.AsSpan(packet.Length - 12));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(auth);
            }
        }

        return packet;
    }

    private static ReadOnlySpan<byte> GetPayload(ReadOnlySpan<byte> packet, RmcpPlusPayloadType expectedType)
    {
        ValidatePacketHeader(packet);
        if (GetPayloadType(packet) != expectedType)
        {
            throw new InvalidDataException($"Expected RMCP+ payload {expectedType}.");
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(14, 2));
        if (packet.Length != 16 + length)
        {
            throw new InvalidDataException("The RMCP+ payload length does not match the datagram.");
        }

        return packet.Slice(16, length);
    }

    private static void ValidatePacketHeader(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 16 || !packet[..4].SequenceEqual(RmcpHeader) || packet[4] != 0x06)
        {
            throw new InvalidDataException("The RMCP+ packet header is malformed.");
        }
    }

    private static void WriteAlgorithm(Span<byte> destination, byte type, byte algorithm)
    {
        destination.Clear();
        destination[0] = type;
        destination[3] = 8;
        destination[4] = algorithm;
    }

    private static void ValidateAlgorithm(ReadOnlySpan<byte> value, byte type, byte algorithm)
    {
        if (value.Length != 8 || value[0] != type || value[3] != 8 || value[4] != algorithm)
        {
            throw new NotSupportedException("The RMCP+ endpoint selected an unsupported cipher suite.");
        }
    }

    private static int IntegrityTrailerLength(int authenticatedLength)
    {
        var paddingLength = (4 - ((authenticatedLength + 2) % 4)) % 4;
        return paddingLength + 2;
    }

    private static byte Checksum(ReadOnlySpan<byte> value)
    {
        byte sum = 0;
        foreach (var item in value)
        {
            sum += item;
        }

        return unchecked((byte)-sum);
    }

    private static byte[] HmacSha1(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input)
    {
        var keyBytes = key.ToArray();
        var inputBytes = input.ToArray();
        try
        {
            using var hmac = new HMACSHA1(keyBytes);
            return hmac.ComputeHash(inputBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(inputBytes);
        }
    }

    private static byte[] AesTransform(
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        bool encrypt)
    {
        if (input.IsEmpty || input.Length % 16 != 0 || key.Length != 16 || iv.Length != 16)
        {
            throw new InvalidDataException("The RMCP+ AES input, key, or IV length is invalid.");
        }

        var inputBytes = input.ToArray();
        var keyBytes = key.ToArray();
        var ivBytes = iv.ToArray();
        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = keyBytes;
            aes.IV = ivBytes;
            using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
            return transform.TransformFinalBlock(inputBytes, 0, inputBytes.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes);
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(ivBytes);
        }
    }

    private static void ThrowForStatus(byte status, string operation)
    {
        if (status == 0)
        {
            return;
        }

        throw status is 0x0D or 0x0E or 0x0F
            ? new UnauthorizedAccessException($"RMCP+ {operation} was rejected with status 0x{status:X2}.")
            : new InvalidOperationException($"RMCP+ {operation} failed with status 0x{status:X2}.");
    }
}

#pragma warning restore CA5350
