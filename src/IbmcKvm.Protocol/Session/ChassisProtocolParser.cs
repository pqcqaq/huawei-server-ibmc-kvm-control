using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Text;

namespace IbmcKvm.Protocol.Session;

public enum ChassisBladeStatus
{
    Available,
    Absent,
    BmcResetting,
    KvmUnsupported,
    KvmBusy,
    ConnectionLimitReached,
    SolActive,
    FirmwareLoading,
    Unavailable,
}

public sealed record ChassisPresence(ImmutableArray<byte> PresentBladeNumbers)
{
    public bool IsPresent(byte bladeNumber) => PresentBladeNumbers.Contains(bladeNumber);
}

public sealed record ChassisBladeState(
    byte BladeNumber,
    ChassisBladeStatus Status,
    byte RawFlags,
    byte ActiveKvmConnections,
    IPAddress? Address,
    int? Port,
    bool UsesManagementAddress,
    string? BusyUserName,
    bool UsesSharedCodeKey)
{
    public bool IsPresent => Status != ChassisBladeStatus.Absent;

    public bool CanControl => Status == ChassisBladeStatus.Available;

    public bool CanMonitor => Status is ChassisBladeStatus.Available or ChassisBladeStatus.KvmBusy;
}

public static class ChassisProtocolParser
{
    public const byte PresenceResponseCommand = 0x01;
    public const byte StateResponseCommand = 0x15;
    public const int MaximumBladeCount = KvmCommandBuilder.MaximumBladeNumber;

    public static ChassisPresence ParsePresence(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 3 || payload[0] != PresenceResponseCommand)
        {
            throw new InvalidDataException("A chassis-presence response must be command 0x01 followed by two bitmap bytes.");
        }

        var blades = ImmutableArray.CreateBuilder<byte>(MaximumBladeCount);
        // Response byte 2 bits 1..7 map to blades 1..7; response byte 1
        // bits 0..6 map to blades 8..14. The outer bits are reserved.
        for (byte blade = 1; blade <= 7; blade++)
        {
            if ((payload[2] & (1 << blade)) != 0)
            {
                blades.Add(blade);
            }
        }

        for (byte blade = 8; blade <= MaximumBladeCount; blade++)
        {
            if ((payload[1] & (1 << (blade - 8))) != 0)
            {
                blades.Add(blade);
            }
        }

        return new ChassisPresence(blades.ToImmutable());
    }

    public static ChassisBladeState ParseState(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3 || payload[0] != StateResponseCommand)
        {
            throw new InvalidDataException("A blade-state response must start with command 0x15 and include a blade and flags.");
        }

        var bladeNumber = payload[1];
        if (bladeNumber is 0 or > KvmCommandBuilder.MaximumBladeNumber)
        {
            throw new InvalidDataException($"The blade-state response contains invalid blade number {bladeNumber}.");
        }

        var flags = payload[2];
        var data = payload.ToArray();
        var present = (flags & 0x80) != 0;
        var kvmSupported = (flags & 0x20) != 0;
        var directChannel = (flags & 0x10) != 0;
        var directChannelBusy = (flags & 0x08) != 0;
        var lowState = flags & 0x07;
        var activeConnections = payload.Length > 3 ? payload[3] : (byte)0;
        var usesSharedCodeKey = payload.Length <= 10 ||
                                (payload[^1] & 0x80) == 0 ||
                                (payload[^1] & 0x01) != 0;

        if (!present)
        {
            return Create(ChassisBladeStatus.Absent);
        }

        if ((flags & 0x04) != 0)
        {
            return Create(ChassisBladeStatus.BmcResetting);
        }

        if (!kvmSupported)
        {
            return Create(ChassisBladeStatus.KvmUnsupported);
        }

        if (directChannel)
        {
            var endpoint = ParseEndpoint(payload);
            return Create(
                directChannelBusy ? ChassisBladeStatus.KvmBusy : ChassisBladeStatus.Available,
                endpoint.Address,
                endpoint.Port,
                usesManagementAddress: false);
        }

        return lowState switch
        {
            0x00 => ParseRelayedAvailable(),
            0x01 when activeConnections >= 4 => Create(ChassisBladeStatus.ConnectionLimitReached),
            0x01 => ParseBusy(),
            0x02 => Create(ChassisBladeStatus.SolActive),
            0x03 => Create(ChassisBladeStatus.FirmwareLoading),
            _ => Create(ChassisBladeStatus.Unavailable),
        };

        ChassisBladeState ParseRelayedAvailable()
        {
            var port = ParsePort(data);
            return Create(ChassisBladeStatus.Available, port: port, usesManagementAddress: true);
        }

        ChassisBladeState ParseBusy()
        {
            IPAddress? address = null;
            string? userName = null;
            if (data.Length >= 8)
            {
                address = new IPAddress(data.AsSpan(4, 4));
            }

            if (data.Length >= 24)
            {
                userName = Encoding.Latin1.GetString(data.AsSpan(8, 16)).TrimEnd('\0', ' ');
                if (userName.Length == 0)
                {
                    userName = null;
                }
            }

            return Create(
                ChassisBladeStatus.KvmBusy,
                address,
                usesManagementAddress: true,
                busyUserName: userName);
        }

        ChassisBladeState Create(
            ChassisBladeStatus status,
            IPAddress? address = null,
            int? port = null,
            bool usesManagementAddress = false,
            string? busyUserName = null) =>
            new(
                bladeNumber,
                status,
                flags,
                activeConnections,
                address,
                port,
                usesManagementAddress,
                busyUserName,
                usesSharedCodeKey);
    }

    private static (IPAddress Address, int Port) ParseEndpoint(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
        {
            throw new InvalidDataException("A direct blade-state response is missing its IPv4 address or KVM port.");
        }

        return (new IPAddress(payload.Slice(4, 4)), ParsePort(payload));
    }

    private static int ParsePort(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
        {
            throw new InvalidDataException("A blade-state response is missing its KVM port.");
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2));
        if (port == 0)
        {
            throw new InvalidDataException("A blade-state response contains port zero.");
        }

        return port;
    }
}
