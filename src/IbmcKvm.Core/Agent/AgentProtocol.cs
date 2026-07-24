using System.Buffers.Binary;

namespace IbmcKvm.Core.Agent;

public enum AgentMessageKind : byte
{
    ClientHello = 1,
    ServerHello = 2,
    Frame = 3,
    Keyboard = 4,
    Mouse = 5,
    KeyframeRequest = 6,
    Ping = 7,
    Pong = 8,
    Error = 9,
}

public sealed record AgentEnvelope(AgentMessageKind Kind, byte[] Payload);

public static class AgentProtocol
{
    public const byte Version = 1;
    public const int HeaderLength = 12;
    public const int MaximumPayloadLength = 16 * 1024 * 1024;
    public const long MaximumFramePixelCount = 32L * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "IKAG"u8;

    public static byte[] Encode(AgentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Payload);
        ValidateKind(envelope.Kind);
        ValidatePayloadLength(envelope.Payload.Length);

        var bytes = new byte[checked(HeaderLength + envelope.Payload.Length)];
        Magic.CopyTo(bytes);
        bytes[4] = Version;
        bytes[5] = (byte)envelope.Kind;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), checked((uint)envelope.Payload.Length));
        envelope.Payload.CopyTo(bytes.AsSpan(HeaderLength));
        return bytes;
    }

    public static async ValueTask<AgentEnvelope> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The Agent protocol magic is invalid.");
        }
        if (header[4] != Version)
        {
            throw new InvalidDataException($"Agent protocol version {header[4]} is unsupported.");
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2)) != 0)
        {
            throw new InvalidDataException("Agent protocol reserved flags must be zero.");
        }

        var kind = (AgentMessageKind)header[5];
        ValidateKind(kind);
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (payloadLength > int.MaxValue)
        {
            throw new InvalidDataException("The Agent payload length exceeds the supported range.");
        }
        ValidatePayloadLength((int)payloadLength);

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new AgentEnvelope(kind, payload);
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        AgentEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = Encode(envelope);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateKind(AgentMessageKind kind)
    {
        if (kind is < AgentMessageKind.ClientHello or > AgentMessageKind.Error)
        {
            throw new InvalidDataException($"Agent protocol message kind {(byte)kind} is unknown.");
        }
    }

    private static void ValidatePayloadLength(int length)
    {
        if (length is < 0 or > MaximumPayloadLength)
        {
            throw new InvalidDataException(
                $"Agent protocol payload length {length} exceeds the configured maximum.");
        }
    }
}
