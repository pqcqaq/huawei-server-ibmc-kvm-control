using System.Buffers.Binary;
using System.Text;
using IbmcKvm.Protocol.Profiles;

namespace IbmcKvm.Protocol.Login;

public sealed class RmcpOemCommand
{
    public RmcpOemCommand(byte netFunction, byte command, ReadOnlySpan<byte> data)
    {
        NetFunction = netFunction;
        Command = command;
        Data = data.ToArray();
    }

    public byte NetFunction { get; }

    public byte Command { get; }

    public ReadOnlyMemory<byte> Data { get; }
}

public sealed record RmcpOemLoginRequest(int CodeKey, RmcpOemCommand Command);

public static class RmcpOemCommandCodec
{
    private const byte OemNetFunction = 0x30;

    public static RmcpOemLoginRequest BuildLogin(
        string userName,
        ConnectionMode mode,
        int codeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        if (codeKey is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(codeKey), "The RMCP+ session key must be in the 0..9 range.");
        }

        if (!userName.All(static character => character is > '\0' and <= '\x7F'))
        {
            throw new ArgumentException("RMCP+ local user names must contain ASCII characters.", nameof(userName));
        }

        if (Encoding.ASCII.GetByteCount(userName) > 32)
        {
            throw new ArgumentException("An RMCP+ local user name cannot exceed 32 bytes.", nameof(userName));
        }

        var userNameBytes = Encoding.ASCII.GetBytes(userName);
        var data = new byte[10 + 4 + 32 + userNameBytes.Length + 12 + 1];
        ReadOnlySpan<byte> prefix = [0xDB, 0x07, 0x00, 0x21, 0x06, 0x00, 0x00, 0x37, 0x00, 0x24];
        prefix.CopyTo(data);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(10, 4), codeKey);
        userNameBytes.CopyTo(data.AsSpan(46));
        data[^1] = mode switch
        {
            ConnectionMode.Shared => 0,
            ConnectionMode.Exclusive => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return new RmcpOemLoginRequest(codeKey, new RmcpOemCommand(OemNetFunction, 0x94, data));
    }

    public static RmcpOemCommand DeviceId() => new(0x06, 0x01, []);

    public static RmcpOemCommand KvmPort(KvmProtocolKind kind) =>
        new(OemNetFunction, 0x93, kind switch
        {
            KvmProtocolKind.Imana => [0xDB, 0x07, 0x00, 0x10, 0x04, 0x02, 0x01, 0x00, 0x00],
            KvmProtocolKind.LegacyIbmc => [0xDB, 0x07, 0x00, 0x38, 0x0A, 0x00, 0x01, 0xFF, 0x00, 0x00, 0x01, 0x00],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });

    public static RmcpOemCommand VirtualMediaPort(KvmProtocolKind kind) =>
        new(OemNetFunction, 0x93, kind switch
        {
            KvmProtocolKind.Imana => [0xDB, 0x07, 0x00, 0x10, 0x04, 0x02, 0x02, 0x00, 0x00],
            KvmProtocolKind.LegacyIbmc => [0xDB, 0x07, 0x00, 0x38, 0x0B, 0x00, 0x01, 0xFF, 0x00, 0x00, 0x01, 0x00],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });

    public static RmcpOemCommand EncryptionInfo(bool virtualMedia) =>
        new(OemNetFunction, 0x94, [0xDB, 0x07, 0x00, 0x20, virtualMedia ? (byte)0x03 : (byte)0x02, 0x00, 0xFF]);

    public static int ParseFirmwareRevision(ReadOnlySpan<byte> response)
    {
        if (response.Length < 14)
        {
            throw new InvalidDataException("The IPMI device-ID response is too short.");
        }

        return response[13] >> 4;
    }

    public static int ParsePort(ReadOnlySpan<byte> response, KvmProtocolKind kind)
    {
        var byteCount = kind switch
        {
            KvmProtocolKind.Imana => 2,
            KvmProtocolKind.LegacyIbmc => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (response.Length < byteCount)
        {
            throw new InvalidDataException("The OEM port response is too short.");
        }

        uint port = 0;
        for (var index = 0; index < byteCount; index++)
        {
            port |= (uint)response[^(index + 1)] << (8 * (byteCount - index - 1));
        }

        if (port is < 1 or > ushort.MaxValue)
        {
            throw new InvalidDataException($"The OEM port value {port} is out of range.");
        }

        return checked((int)port);
    }

    public static bool ParseEncryptionFlag(ReadOnlySpan<byte> response)
    {
        if (response.IsEmpty || response[^1] > 1)
        {
            throw new InvalidDataException("The OEM encryption flag is malformed.");
        }

        return response[^1] == 1;
    }
}
