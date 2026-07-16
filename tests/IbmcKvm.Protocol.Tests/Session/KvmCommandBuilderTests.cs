using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Protocol.Tests.Session;

public sealed class KvmCommandBuilderTests
{
    [Fact]
    public void BuildsObservedSessionCommands()
    {
        Assert.Equal(new byte[] { 0x0B }, KvmCommandBuilder.RequestBladePresent());
        Assert.Equal(new byte[] { 0x14, 1 }, KvmCommandBuilder.RequestBladeState(1, exclusive: false));
        Assert.Equal(new byte[] { 0x21, 1 }, KvmCommandBuilder.RequestBladeState(1, exclusive: true));
        Assert.Equal(new byte[] { 0x06, 1, 3, 1, 1 }, KvmCommandBuilder.ConnectBlade(1, 3));
        Assert.Equal(new byte[] { 0x17, 1, 1 }, KvmCommandBuilder.MonitorBlade(1));
        Assert.Equal(new byte[] { 0x18, 1, 1 }, KvmCommandBuilder.StopMonitoringBlade(1));
        Assert.Equal(new byte[] { 0x07, 1 }, KvmCommandBuilder.DisconnectBlade(1));
        Assert.Equal(new byte[] { 0x09, 1 }, KvmCommandBuilder.Heartbeat(1));
        Assert.Equal(new byte[] { 0x1C, 35 }, KvmCommandBuilder.SetFrameRate(35));
        Assert.Equal(new byte[] { 0x24, 0, 2, 0, 0 }, KvmCommandBuilder.QueryMouseMode());
        Assert.Equal(new byte[] { 0x04, 1, 1 }, KvmCommandBuilder.QueryKeyboardState(1));
        Assert.Equal(new byte[] { 0x40, 0 }, KvmCommandBuilder.StartRecording());
        Assert.Equal(new byte[] { 0x41, 0 }, KvmCommandBuilder.StopRecording());
        Assert.Equal(new byte[] { 0x1B, 1, 3 }, KvmCommandBuilder.SetColorDepth(1, 3));
        Assert.Equal(new byte[] { 0x27, 0, 40, 2, 0 }, KvmCommandBuilder.SetVideoQuality(40, committed: false));
        Assert.Equal(new byte[] { 0x27, 0, 70, 1, 0 }, KvmCommandBuilder.SetVideoQuality(60, committed: true));
        Assert.Equal(new byte[] { 0x27, 0, 100, 1, 0 }, KvmCommandBuilder.SetVideoQuality(90, committed: true));
        Assert.Equal(new byte[] { 0x24, 0, 1, 0, 0 }, KvmCommandBuilder.SetMouseMode(KvmMouseMode.Absolute));
        Assert.Equal(new byte[] { 0x31, 0 }, KvmCommandBuilder.RequestVirtualMediaCredential());
        Assert.Equal(new byte[] { 0x35, 0 }, KvmCommandBuilder.RequestVirtualMediaPort());
        Assert.Equal(new byte[] { 0x31, 7 }, KvmCommandBuilder.RequestVirtualMediaCredential(7));
        Assert.Equal(new byte[] { 0x30, 0 }, KvmCommandBuilder.Power(KvmPowerAction.UsbReset));
        Assert.Equal(new byte[] { 0x23, 0 }, KvmCommandBuilder.Power(KvmPowerAction.ForcedPowerCycle));
        Assert.Equal(
            new byte[] { 0x05, 1, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0 },
            KvmCommandBuilder.SynchronizeAbsoluteMouse(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(255)]
    public void RejectsBladeNumbersOutsideProtocolChassisRange(byte bladeNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KvmCommandBuilder.RequestBladeState(bladeNumber, false));
    }

    [Fact]
    public void BuildsLegacyPlainKeyboardReport()
    {
        var report = new byte[] { 5, 0, 76, 0, 0, 0, 0, 0 };

        Assert.Equal(
            new byte[] { 0x03, 1, 5, 0, 76, 0, 0, 0, 0, 0 },
            KvmCommandBuilder.Keyboard(1, report, 0x01020304, KvmKeyboardEncoding.LegacyPlain));
    }

    [Theory]
    [InlineData(0x01020304, "05004C0000000000", "5B2D00AAA634E6CF0E5F37283142DFB8")]
    [InlineData(-1, "0000040000000000", "203ED554F51DA3AA173B90D6347CC847")]
    public void BuildsCodeKeyEncryptedKeyboardReportMatchingLegacyJar(
        int codeKey,
        string reportHex,
        string encryptedHex)
    {
        var expected = new byte[] { 0x03, 1 }
            .Concat(Convert.FromHexString(encryptedHex))
            .ToArray();

        Assert.Equal(
            expected,
            KvmCommandBuilder.Keyboard(
                1,
                Convert.FromHexString(reportHex),
                codeKey,
                KvmKeyboardEncoding.CodeKeyAes));
    }

    [Fact]
    public void BuildsAbsoluteMouseReportInNetworkByteOrder()
    {
        Assert.Equal(
            new byte[] { 0x05, 1, 3, 0x0B, 0xB8, 0x05, 0xDC, 0xFF },
            KvmCommandBuilder.AbsoluteMouse(1, 3, 3000, 1500, -1));
    }

    [Fact]
    public void BuildsSessionEncryptedInputAndPowerWrappers()
    {
        var keyboard = Convert.FromHexString("1CE62A1C88E9C99EF265C6C1B3F44016");
        var mouse = Convert.FromHexString("025685722DA2D7A823B424BAA23AA22F");
        var power = Convert.FromHexString("8DA322A5E47D81800AA0FF60CB4E5E37");

        Assert.Equal(
            Convert.FromHexString("03011CE62A1C88E9C99EF265C6C1B3F44016"),
            KvmCommandBuilder.EncryptedInput(0x03, 1, keyboard));
        Assert.Equal(
            Convert.FromHexString("0501025685722DA2D7A823B424BAA23AA22F"),
            KvmCommandBuilder.EncryptedInput(0x05, 1, mouse));
        Assert.Equal(
            Convert.FromHexString("33008DA322A5E47D81800AA0FF60CB4E5E37"),
            KvmCommandBuilder.EncryptedPower(power));
    }

    [Fact]
    public void BuildsReusableAbsoluteMouseReport()
    {
        Assert.Equal(
            Convert.FromHexString("030BB805DCFF"),
            KvmCommandBuilder.AbsoluteMouseReport(3, 3000, 1500, -1));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("2147483648", int.MinValue)]
    [InlineData("4294967295", -1)]
    public void ParsesUnsignedVerificationValueForSignedWireField(string value, int expected)
    {
        Assert.Equal(expected, SessionVerificationKey.Parse(value).WireValue);
    }
}
