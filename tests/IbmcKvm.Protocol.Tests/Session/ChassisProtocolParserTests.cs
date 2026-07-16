using System.Net;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Protocol.Tests.Session;

public sealed class ChassisProtocolParserTests
{
    [Fact]
    public void MapsFourteenSlotPresenceBitmapAndIgnoresReservedBits()
    {
        var presence = ChassisProtocolParser.ParsePresence([0x01, 0xC1, 0x83]);

        Assert.Equal(new byte[] { 1, 7, 8, 14 }, presence.PresentBladeNumbers);
        Assert.False(presence.IsPresent(2));
    }

    [Fact]
    public void ParsesDirectAvailableBladeEndpoint()
    {
        var state = ChassisProtocolParser.ParseState(
            [0x15, 0x03, 0xB0, 0x00, 192, 0, 2, 31, 0x1D, 0x4C, 0x81]);

        Assert.Equal((byte)3, state.BladeNumber);
        Assert.Equal(ChassisBladeStatus.Available, state.Status);
        Assert.Equal(IPAddress.Parse("192.0.2.31"), state.Address);
        Assert.Equal(7500, state.Port);
        Assert.False(state.UsesManagementAddress);
        Assert.True(state.UsesSharedCodeKey);
        Assert.True(state.CanControl);
    }

    [Theory]
    [InlineData(0x00, ChassisBladeStatus.Absent)]
    [InlineData(0xA4, ChassisBladeStatus.BmcResetting)]
    [InlineData(0x80, ChassisBladeStatus.KvmUnsupported)]
    [InlineData(0xAB, ChassisBladeStatus.FirmwareLoading)]
    [InlineData(0xAA, ChassisBladeStatus.SolActive)]
    [InlineData(0xAF, ChassisBladeStatus.BmcResetting)]
    public void ParsesNonConnectableStates(byte flags, ChassisBladeStatus expected)
    {
        var state = ChassisProtocolParser.ParseState([0x15, 1, flags]);

        Assert.Equal(expected, state.Status);
        Assert.False(state.CanControl);
    }

    [Fact]
    public void ParsesRelayedAvailableBlade()
    {
        var state = ChassisProtocolParser.ParseState(
            [0x15, 0x02, 0xA0, 0x00, 0, 0, 0, 0, 0x1D, 0x4C]);

        Assert.Equal(ChassisBladeStatus.Available, state.Status);
        Assert.True(state.UsesManagementAddress);
        Assert.Equal(7500, state.Port);
    }

    [Fact]
    public void ParsesBusyClientAndFourSessionLimit()
    {
        var user = "operator"u8.ToArray();
        var busyPayload = new byte[24];
        busyPayload[0] = 0x15;
        busyPayload[1] = 2;
        busyPayload[2] = 0xA1;
        busyPayload[3] = 3;
        new byte[] { 198, 51, 100, 9 }.CopyTo(busyPayload, 4);
        user.CopyTo(busyPayload, 8);

        var busy = ChassisProtocolParser.ParseState(busyPayload);
        var limited = ChassisProtocolParser.ParseState([0x15, 2, 0xA1, 4]);

        Assert.Equal(ChassisBladeStatus.KvmBusy, busy.Status);
        Assert.Equal(IPAddress.Parse("198.51.100.9"), busy.Address);
        Assert.Equal("operator", busy.BusyUserName);
        Assert.True(busy.CanMonitor);
        Assert.Equal(ChassisBladeStatus.ConnectionLimitReached, limited.Status);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x01, 0 })]
    [InlineData(new byte[] { 0x02, 0, 0 })]
    public void RejectsMalformedPresence(byte[] payload)
    {
        Assert.Throws<InvalidDataException>(() => ChassisProtocolParser.ParsePresence(payload));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x15, 1 })]
    [InlineData(new byte[] { 0x14, 1, 0xB0 })]
    [InlineData(new byte[] { 0x15, 0, 0xB0 })]
    [InlineData(new byte[] { 0x15, 15, 0xB0 })]
    [InlineData(new byte[] { 0x15, 1, 0xB0 })]
    [InlineData(new byte[] { 0x15, 1, 0xB0, 0, 1, 2, 3, 4, 0, 0 })]
    public void RejectsMalformedState(byte[] payload)
    {
        Assert.Throws<InvalidDataException>(() => ChassisProtocolParser.ParseState(payload));
    }
}
