namespace IbmcKvm.Protocol.VirtualMedia;

public enum VmmPacketType : byte
{
    Acknowledgement = 0x00,
    Authenticate = 0x01,
    CreateDevice = 0x02,
    FloppyData = 0x03,
    OpticalData = 0x04,
    Close = 0x05,
    Heartbeat = 0x06,
    Shutdown = 0x07,
    FloppyCommandComplete = 0xFE,
    OpticalCommandComplete = 0xFF,
}

public enum VmmDeviceType : byte
{
    Link = 0,
    Floppy = 1,
    Optical = 2,
}

public enum VmmTransferState : byte
{
    Continue = 1,
    End = 3,
}

public enum VmmTransferKind : byte
{
    Command = 0,
    Data = 1,
}

public sealed record VmmPacket(
    VmmPacketType Type,
    byte Field1,
    byte Field2,
    byte CommandId,
    byte[] Payload,
    uint Metadata = 0)
{
    public static VmmPacket Authenticate(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> localAddress)
    {
        if (sessionId.Length != 24)
        {
            throw new ArgumentException("A negotiated VMM session ID contains exactly 24 bytes.", nameof(sessionId));
        }

        if (localAddress.Length is not (4 or 16))
        {
            throw new ArgumentException("The local address must be IPv4 or IPv6.", nameof(localAddress));
        }

        var payload = new byte[sessionId.Length + 1 + localAddress.Length];
        sessionId.CopyTo(payload);
        payload[sessionId.Length] = localAddress.Length == 4 ? (byte)0 : (byte)1;
        localAddress.CopyTo(payload.AsSpan(sessionId.Length + 1));
        return new VmmPacket(AuthenticateType, 0, 0, 0, payload, 0x03010101);
    }

    public static VmmPacket CreateDevice(VmmDeviceType deviceType)
    {
        if (deviceType is not (VmmDeviceType.Floppy or VmmDeviceType.Optical))
        {
            throw new ArgumentOutOfRangeException(nameof(deviceType));
        }

        return new VmmPacket(VmmPacketType.CreateDevice, (byte)deviceType, 0, 0, []);
    }

    public static VmmPacket Data(
        VmmDeviceType deviceType,
        VmmTransferKind kind,
        VmmTransferState state,
        byte commandId,
        ReadOnlySpan<byte> payload) =>
        new(
            deviceType switch
            {
                VmmDeviceType.Floppy => VmmPacketType.FloppyData,
                VmmDeviceType.Optical => VmmPacketType.OpticalData,
                _ => throw new ArgumentOutOfRangeException(nameof(deviceType)),
            },
            (byte)(((byte)state << 4) | ((byte)kind & 0x0F)),
            0,
            commandId,
            payload.ToArray());

    public static VmmPacket Complete(VmmDeviceType deviceType, byte result, byte commandId) =>
        new(
            deviceType switch
            {
                VmmDeviceType.Floppy => VmmPacketType.FloppyCommandComplete,
                VmmDeviceType.Optical => VmmPacketType.OpticalCommandComplete,
                _ => throw new ArgumentOutOfRangeException(nameof(deviceType)),
            },
            (byte)(result & 0x0F),
            0,
            commandId,
            []);

    public static VmmPacket Close(VmmDeviceType deviceType, byte reason = 0) =>
        new(VmmPacketType.Close, (byte)deviceType, reason, 0, []);

    public static VmmPacket Heartbeat() => new(VmmPacketType.Heartbeat, 0, 0, 0, []);

    private const VmmPacketType AuthenticateType = VmmPacketType.Authenticate;
}
