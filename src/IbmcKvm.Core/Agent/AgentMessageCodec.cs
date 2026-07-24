using System.Buffers.Binary;
using System.Text;

namespace IbmcKvm.Core.Agent;

public static class AgentMessageCodec
{
    private const byte JpegCodec = 1;
    private const int MaximumTokenLength = 512;
    private const int MaximumErrorLength = 1024;

    public static AgentEnvelope ClientHello(ReadOnlySpan<byte> token)
    {
        if (token.Length is < 32 or > MaximumTokenLength)
        {
            throw new ArgumentException("An Agent pairing token must contain 32 to 512 bytes.", nameof(token));
        }
        var payload = new byte[2 + token.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, checked((ushort)token.Length));
        token.CopyTo(payload.AsSpan(2));
        return new AgentEnvelope(AgentMessageKind.ClientHello, payload);
    }

    public static AgentServerHello ParseServerHello(AgentEnvelope envelope)
    {
        RequireKind(envelope, AgentMessageKind.ServerHello);
        if (envelope.Payload.Length != 8)
        {
            throw new InvalidDataException("The Agent server hello must contain eight bytes.");
        }
        var width = BinaryPrimitives.ReadUInt16BigEndian(envelope.Payload.AsSpan(0, 2));
        var height = BinaryPrimitives.ReadUInt16BigEndian(envelope.Payload.AsSpan(2, 2));
        var fps = envelope.Payload[4];
        var tileSize = envelope.Payload[5];
        var capabilities = (AgentCapabilities)BinaryPrimitives.ReadUInt16BigEndian(envelope.Payload.AsSpan(6, 2));
        if (width == 0 || height == 0 ||
            (long)width * height > AgentProtocol.MaximumFramePixelCount ||
            fps is 0 or > 30 || tileSize == 0 ||
            (capabilities & ~(AgentCapabilities.Keyboard | AgentCapabilities.Mouse | AgentCapabilities.AbsoluteMouse)) != 0)
        {
            throw new InvalidDataException("The Agent server hello contains invalid capabilities.");
        }
        return new AgentServerHello(width, height, fps, tileSize, capabilities);
    }

    public static AgentVideoFrame ParseFrame(AgentEnvelope envelope)
    {
        RequireKind(envelope, AgentMessageKind.Frame);
        var payload = envelope.Payload.AsSpan();
        if (payload.Length < 12)
        {
            throw new InvalidDataException("The Agent frame header is truncated.");
        }
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var width = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
        var height = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2));
        var flags = payload[8];
        var tileSize = payload[9];
        var tileCount = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(10, 2));
        if (width == 0 || height == 0 ||
            (long)width * height > AgentProtocol.MaximumFramePixelCount ||
            tileSize == 0 || (flags & ~1) != 0)
        {
            throw new InvalidDataException("The Agent frame metadata is invalid.");
        }

        var offset = 12;
        var tiles = new List<AgentTile>(tileCount);
        for (var index = 0; index < tileCount; index++)
        {
            if (offset > payload.Length - 14)
            {
                throw new InvalidDataException("An Agent tile header is truncated.");
            }
            var x = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            var y = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 2, 2));
            var tileWidth = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 4, 2));
            var tileHeight = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 6, 2));
            var codec = payload[offset + 8];
            var reserved = payload[offset + 9];
            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset + 10, 4));
            offset += 14;
            if (dataLength > int.MaxValue || offset > payload.Length - (int)dataLength)
            {
                throw new InvalidDataException("An Agent tile payload is truncated.");
            }
            if (codec != JpegCodec || reserved != 0 || tileWidth == 0 || tileHeight == 0 || dataLength == 0 ||
                (uint)x + tileWidth > width || (uint)y + tileHeight > height)
            {
                throw new InvalidDataException("An Agent tile contains invalid metadata.");
            }
            tiles.Add(new AgentTile(x, y, tileWidth, tileHeight, payload.Slice(offset, (int)dataLength).ToArray()));
            offset += (int)dataLength;
        }
        if (offset != payload.Length)
        {
            throw new InvalidDataException("The Agent frame contains trailing bytes.");
        }
        ValidateKeyframeCoverage(width, height, tileSize, (flags & 1) != 0, tiles);
        return new AgentVideoFrame(sequence, width, height, (flags & 1) != 0, tileSize, tiles);
    }

    public static AgentEnvelope Keyboard(ReadOnlySpan<byte> report)
    {
        if (report.Length != 8)
        {
            throw new ArgumentException("An Agent keyboard report must contain eight bytes.", nameof(report));
        }
        return new AgentEnvelope(AgentMessageKind.Keyboard, report.ToArray());
    }

    public static AgentEnvelope Mouse(AgentMouseReport report)
    {
        var payload = new byte[6];
        payload[0] = (byte)(report.Buttons & 0x07);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1, 2), report.X);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3, 2), report.Y);
        payload[5] = unchecked((byte)report.Wheel);
        return new AgentEnvelope(AgentMessageKind.Mouse, payload);
    }

    public static AgentEnvelope Ping(ulong nonce)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(payload, nonce);
        return new AgentEnvelope(AgentMessageKind.Ping, payload);
    }

    public static (ushort Code, string Message) ParseError(AgentEnvelope envelope)
    {
        RequireKind(envelope, AgentMessageKind.Error);
        if (envelope.Payload.Length is < 3 or > MaximumErrorLength + 2)
        {
            throw new InvalidDataException("The Agent error payload length is invalid.");
        }
        var code = BinaryPrimitives.ReadUInt16BigEndian(envelope.Payload.AsSpan(0, 2));
        string message;
        try
        {
            message = new UTF8Encoding(false, true).GetString(envelope.Payload, 2, envelope.Payload.Length - 2);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The Agent error message is not valid UTF-8.", exception);
        }
        return (code, message);
    }

    private static void ValidateKeyframeCoverage(
        ushort width,
        ushort height,
        byte tileSize,
        bool keyframe,
        List<AgentTile> tiles)
    {
        if (!keyframe)
        {
            return;
        }
        var columns = (width + tileSize - 1) / tileSize;
        var rows = (height + tileSize - 1) / tileSize;
        if ((long)columns * rows > ushort.MaxValue || tiles.Count != columns * rows)
        {
            throw new InvalidDataException("An Agent keyframe does not cover every tile.");
        }
        var positions = new HashSet<(ushort X, ushort Y)>();
        foreach (var tile in tiles)
        {
            if (tile.X % tileSize != 0 || tile.Y % tileSize != 0 ||
                tile.Width != Math.Min(tileSize, width - tile.X) ||
                tile.Height != Math.Min(tileSize, height - tile.Y) ||
                !positions.Add((tile.X, tile.Y)))
            {
                throw new InvalidDataException("An Agent keyframe tile layout is invalid.");
            }
        }
    }

    private static void RequireKind(AgentEnvelope envelope, AgentMessageKind expected)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Kind != expected)
        {
            throw new InvalidDataException($"Expected Agent message {expected}, received {envelope.Kind}.");
        }
    }
}
