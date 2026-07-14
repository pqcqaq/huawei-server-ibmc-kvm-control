using System.Buffers.Binary;

namespace IbmcKvm.Protocol.Session;

public enum KvmPowerAction : byte
{
    PowerOff = 0x20,
    PowerOn = 0x21,
    Restart = 0x22,
    SafeRestart = 0x23,
    GracefulPowerOff = 0x25,
    UsbReset = 0x30,
}

public static class KvmCommandBuilder
{
    public static byte[] RequestBladePresent() => [0x0B];

    public static byte[] RequestBladeState(byte bladeNumber, bool exclusive) =>
        [exclusive ? (byte)0x21 : (byte)0x14, ValidateBlade(bladeNumber)];

    public static byte[] ConnectBlade(byte bladeNumber, byte colorDepth, ReadOnlySpan<byte> reconnectKey = default)
    {
        if (reconnectKey.Length is not (0 or 128))
        {
            throw new ArgumentException("The reconnect key must contain exactly 128 bytes", nameof(reconnectKey));
        }

        var payload = new byte[5 + reconnectKey.Length];
        payload[0] = 0x06;
        payload[1] = ValidateBlade(bladeNumber);
        payload[2] = colorDepth;
        payload[3] = 1;
        payload[4] = 1;
        reconnectKey.CopyTo(payload.AsSpan(5));
        return payload;
    }

    public static byte[] DisconnectBlade(byte bladeNumber) => [0x07, ValidateBlade(bladeNumber)];

    public static byte[] RequestFullFrame(byte bladeNumber) => [0x08, ValidateBlade(bladeNumber)];

    public static byte[] Heartbeat(byte bladeNumber) => [0x09, ValidateBlade(bladeNumber)];

    public static byte[] SetFrameRate(byte framesPerSecond) => [0x1C, framesPerSecond];

    public static byte[] SetMouseMode(bool absolute) => [0x24, 0, absolute ? (byte)2 : (byte)1, 0, 0];

    public static byte[] Keyboard(byte bladeNumber, ReadOnlySpan<byte> hidReport)
    {
        if (hidReport.Length != 8)
        {
            throw new ArgumentException("A boot-protocol keyboard report contains 8 bytes", nameof(hidReport));
        }

        var payload = new byte[10];
        payload[0] = 0x03;
        payload[1] = ValidateBlade(bladeNumber);
        hidReport.CopyTo(payload.AsSpan(2));
        return payload;
    }

    public static byte[] AbsoluteMouse(
        byte bladeNumber,
        byte buttonMask,
        ushort x,
        ushort y,
        sbyte wheel)
    {
        if (x > 3000 || y > 3000)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Absolute coordinates use the 0..3000 iBMC range");
        }

        var payload = new byte[8];
        payload[0] = 0x05;
        payload[1] = ValidateBlade(bladeNumber);
        payload[2] = buttonMask;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3, 2), x);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(5, 2), y);
        payload[7] = unchecked((byte)wheel);
        return payload;
    }

    public static byte[] RelativeMouse(
        byte bladeNumber,
        byte buttonMask,
        sbyte deltaX,
        sbyte deltaY,
        sbyte wheel) =>
        [0x05, ValidateBlade(bladeNumber), buttonMask, unchecked((byte)deltaX), unchecked((byte)deltaY), unchecked((byte)wheel)];

    public static byte[] Power(KvmPowerAction action) => [(byte)action, 0];

    public static byte[] GetCipherSuites(byte bladeNumber) => [0x42, ValidateBlade(bladeNumber)];

    public static byte[] SelectCipherSuite(byte bladeNumber, byte hmac, int iterations)
    {
        var payload = new byte[7];
        payload[0] = 0x44;
        payload[1] = ValidateBlade(bladeNumber);
        payload[2] = hmac;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(3), iterations);
        return payload;
    }

    private static byte ValidateBlade(byte bladeNumber)
    {
        if (bladeNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bladeNumber));
        }

        return bladeNumber;
    }
}
