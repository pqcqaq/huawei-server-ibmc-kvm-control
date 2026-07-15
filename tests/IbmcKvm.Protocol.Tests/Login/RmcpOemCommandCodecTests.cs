using IbmcKvm.Protocol.Login;
using IbmcKvm.Protocol.Profiles;

namespace IbmcKvm.Protocol.Tests.Login;

public sealed class RmcpOemCommandCodecTests
{
    [Fact]
    public void BuildsSourceVerifiedLoginPayload()
    {
        var request = RmcpOemCommandCodec.BuildLogin("admin", ConnectionMode.Exclusive, 7);

        Assert.Equal(7, request.CodeKey);
        Assert.Equal(0x30, request.Command.NetFunction);
        Assert.Equal(0x94, request.Command.Command);
        Assert.Equal(
            Convert.FromHexString(
                "DB070021060000370024" +
                "00000007" +
                "0000000000000000000000000000000000000000000000000000000000000000" +
                "61646D696E" +
                "000000000000000000000000" +
                "01"),
            request.Command.Data.ToArray());
    }

    [Fact]
    public void BuildsVariantSpecificPortAndEncryptionCommands()
    {
        Assert.Equal(
            Convert.FromHexString("DB0700100402010000"),
            RmcpOemCommandCodec.KvmPort(KvmProtocolKind.Imana).Data.ToArray());
        Assert.Equal(
            Convert.FromHexString("DB0700380B0001FF00000100"),
            RmcpOemCommandCodec.VirtualMediaPort(KvmProtocolKind.LegacyIbmc).Data.ToArray());
        Assert.Equal(
            Convert.FromHexString("DB0700200300FF"),
            RmcpOemCommandCodec.EncryptionInfo(virtualMedia: true).Data.ToArray());
    }

    [Theory]
    [InlineData(KvmProtocolKind.Imana, "AA3412", 0x1234)]
    [InlineData(KvmProtocolKind.LegacyIbmc, "AA34120000", 0x1234)]
    public void ParsesVariantSpecificLittleEndianPorts(KvmProtocolKind kind, string hex, int expected)
    {
        Assert.Equal(expected, RmcpOemCommandCodec.ParsePort(Convert.FromHexString(hex), kind));
    }

    [Fact]
    public void ParsesFirmwareFamilyAndEncryptionFlag()
    {
        var deviceId = new byte[14];
        deviceId[13] = 0x21;

        Assert.Equal(2, RmcpOemCommandCodec.ParseFirmwareRevision(deviceId));
        Assert.True(RmcpOemCommandCodec.ParseEncryptionFlag([0x01]));
        Assert.IsType<LegacyIbmcProtocolProfile>(IbmcProtocolDiscovery.SelectRmcp(2));
        Assert.IsType<ImanaKvmProtocolProfile>(IbmcProtocolDiscovery.SelectRmcp(0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    public void RejectsShortDeviceIdResponses(string hex)
    {
        Assert.Throws<InvalidDataException>(() =>
            RmcpOemCommandCodec.ParseFirmwareRevision(Convert.FromHexString(hex)));
    }
}
