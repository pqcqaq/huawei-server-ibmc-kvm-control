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
        Assert.Equal(new byte[] { 0x09, 1 }, KvmCommandBuilder.Heartbeat(1));
        Assert.Equal(new byte[] { 0x1C, 35 }, KvmCommandBuilder.SetFrameRate(35));
        Assert.Equal(new byte[] { 0x24, 0, 2, 0, 0 }, KvmCommandBuilder.QueryMouseMode());
        Assert.Equal(new byte[] { 0x24, 0, 1, 0, 0 }, KvmCommandBuilder.SetMouseMode(KvmMouseMode.Absolute));
        Assert.Equal(new byte[] { 0x31, 0 }, KvmCommandBuilder.RequestVirtualMediaCredential());
        Assert.Equal(new byte[] { 0x35, 0 }, KvmCommandBuilder.RequestVirtualMediaPort());
        Assert.Equal(new byte[] { 0x31, 7 }, KvmCommandBuilder.RequestVirtualMediaCredential(7));
        Assert.Equal(new byte[] { 0x30, 0 }, KvmCommandBuilder.Power(KvmPowerAction.UsbReset));
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
