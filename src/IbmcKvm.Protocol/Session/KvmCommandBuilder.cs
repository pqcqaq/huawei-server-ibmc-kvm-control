using System.Buffers.Binary;

namespace IbmcKvm.Protocol.Session;

public enum KvmPowerAction : byte
{
    PowerOff = 0x20,
    PowerOn = 0x21,
    Restart = 0x22,
    ForcedPowerCycle = 0x23,
    SafeRestart = ForcedPowerCycle,
    GracefulPowerOff = 0x25,
    UsbReset = 0x30,
}

public static class KvmCommandBuilder
{
    public const byte MaximumBladeNumber = 14;

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

    public static byte[] MonitorBlade(byte bladeNumber) => [0x17, ValidateBlade(bladeNumber), 1];

    public static byte[] StopMonitoringBlade(byte bladeNumber) => [0x18, ValidateBlade(bladeNumber), 1];

    public static byte[] RequestFullFrame(byte bladeNumber) => [0x08, ValidateBlade(bladeNumber)];

    public static byte[] Heartbeat(byte bladeNumber) => [0x09, ValidateBlade(bladeNumber)];

    public static byte[] SetFrameRate(byte framesPerSecond) => [0x1C, framesPerSecond];

    public static byte[] StartRecording() => [0x40, 0];

    public static byte[] StopRecording() => [0x41, 0];

    public static byte[] SetColorDepth(byte bladeNumber, byte colorDepth) =>
        [0x1B, ValidateBlade(bladeNumber), ValidateColorDepth(colorDepth)];

    public static byte[] SetVideoQuality(byte clarity, bool committed)
    {
        if (clarity is < 40 or > 90 || clarity % 10 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clarity), "Video clarity uses 40..90 in steps of 10.");
        }

        var wireValue = clarity >= 60 ? clarity + 10 : clarity;
        return [0x27, 0, checked((byte)wireValue), committed ? (byte)1 : (byte)2, 0];
    }

    public static byte[] QueryMouseMode() => [0x24, 0, 2, 0, 0];

    public static byte[] QueryKeyboardState(byte bladeNumber) => [0x04, ValidateBlade(bladeNumber), 1];

    public static byte[] SetMouseMode(KvmMouseMode mode) => [0x24, 0, (byte)mode, 0, 0];

    public static byte[] Keyboard(
        byte bladeNumber,
        ReadOnlySpan<byte> hidReport,
        int codeKey,
        KvmKeyboardEncoding encoding)
    {
        if (hidReport.Length != 8)
        {
            throw new ArgumentException("A boot-protocol keyboard report contains 8 bytes", nameof(hidReport));
        }

        var encodedReport = encoding switch
        {
            KvmKeyboardEncoding.LegacyPlain => hidReport.ToArray(),
            KvmKeyboardEncoding.CodeKeyAes => KvmInputCipher.EncryptKeyboardReport(hidReport, codeKey),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };
        var payload = new byte[2 + encodedReport.Length];
        payload[0] = 0x03;
        payload[1] = ValidateBlade(bladeNumber);
        encodedReport.CopyTo(payload.AsSpan(2));
        return payload;
    }

    public static byte[] AbsoluteMouse(
        byte bladeNumber,
        byte buttonMask,
        ushort x,
        ushort y,
        sbyte wheel)
    {
        var report = AbsoluteMouseReport(buttonMask, x, y, wheel);
        var payload = new byte[2 + report.Length];
        payload[0] = 0x05;
        payload[1] = ValidateBlade(bladeNumber);
        report.CopyTo(payload.AsSpan(2));
        return payload;
    }

    public static byte[] AbsoluteMouseReport(
        byte buttonMask,
        ushort x,
        ushort y,
        sbyte wheel)
    {
        if (x > 3000 || y > 3000)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Absolute coordinates use the 0..3000 iBMC range");
        }

        var report = new byte[6];
        report[0] = buttonMask;
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(1, 2), x);
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(3, 2), y);
        report[5] = unchecked((byte)wheel);
        return report;
    }

    public static byte[] SynchronizeAbsoluteMouse(byte bladeNumber) =>
        [0x05, ValidateBlade(bladeNumber), 0, 0xFF, 0xFF, 0xFF, 0xFF, 0];

    public static byte[] SynchronizeAbsoluteMouseReport() => [0, 0xFF, 0xFF, 0xFF, 0xFF, 0];

    public static byte[] RelativeMouse(
        byte bladeNumber,
        byte buttonMask,
        sbyte deltaX,
        sbyte deltaY,
        sbyte wheel) =>
        [0x05, ValidateBlade(bladeNumber), buttonMask, unchecked((byte)deltaX), unchecked((byte)deltaY), unchecked((byte)wheel)];

    public static byte[] Power(KvmPowerAction action) => [(byte)action, 0];

    public static byte[] EncryptedInput(
        byte command,
        byte bladeNumber,
        ReadOnlySpan<byte> encryptedReport)
    {
        if (command is not (0x03 or 0x05))
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Only keyboard and mouse reports use this wrapper.");
        }

        if (encryptedReport.Length != 16)
        {
            throw new ArgumentException("An encrypted KVM input report contains exactly 16 bytes.", nameof(encryptedReport));
        }

        var payload = new byte[18];
        payload[0] = command;
        payload[1] = ValidateBlade(bladeNumber);
        encryptedReport.CopyTo(payload.AsSpan(2));
        return payload;
    }

    public static byte[] EncryptedPower(ReadOnlySpan<byte> encryptedCommand)
    {
        if (encryptedCommand.Length != 16)
        {
            throw new ArgumentException("An encrypted KVM power command contains exactly 16 bytes.", nameof(encryptedCommand));
        }

        var payload = new byte[18];
        payload[0] = 0x33;
        encryptedCommand.CopyTo(payload.AsSpan(2));
        return payload;
    }

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

    public static byte[] RequestVirtualMediaCredential(byte bladeNumber = 0) => [0x31, bladeNumber];

    public static byte[] RequestVirtualMediaPort(byte bladeNumber = 0) => [0x35, bladeNumber];

    private static byte ValidateBlade(byte bladeNumber)
    {
        if (bladeNumber is 0 or > MaximumBladeNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bladeNumber),
                $"Blade numbers use the 1..{MaximumBladeNumber} chassis range.");
        }

        return bladeNumber;
    }

    private static byte ValidateColorDepth(byte colorDepth)
    {
        if (colorDepth > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(colorDepth));
        }

        return colorDepth;
    }
}
