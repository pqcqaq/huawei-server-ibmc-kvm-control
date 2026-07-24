using System.Net;
using IbmcKvm.Core.Session;
using IbmcKvm.Desktop.Ui;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Desktop.Tests.Ui;

public sealed class ChassisUiStateTests
{
    [Theory]
    [InlineData(ChassisBladeStatus.Absent, "未安装")]
    [InlineData(ChassisBladeStatus.KvmBusy, "KVM 使用中")]
    [InlineData(ChassisBladeStatus.SolActive, "SOL 使用中")]
    [InlineData(ChassisBladeStatus.FirmwareLoading, "固件加载中")]
    [InlineData(ChassisBladeStatus.KvmUnsupported, "不支持 KVM")]
    public void SurfacesSupportedBladeStates(ChassisBladeStatus status, string expected)
    {
        var item = ChassisUiState.Resolve(State(status), []);

        Assert.Equal(expected, item.Status);
    }

    [Fact]
    public void ConnectedBladeCannotBeOpenedTwice()
    {
        var item = ChassisUiState.Resolve(State(ChassisBladeStatus.Available), [2]);

        Assert.True(item.IsConnected);
        Assert.False(item.CanConnect);
        Assert.False(item.CanMonitor);
    }

    [Theory]
    [InlineData(KvmBladeSessionMode.Control, false, true)]
    [InlineData(KvmBladeSessionMode.Control, true, false)]
    [InlineData(KvmBladeSessionMode.Monitor, false, false)]
    public void RoutesInputOnlyToSingleSelectedControlView(
        KvmBladeSessionMode mode,
        bool split,
        bool expected) =>
        Assert.Equal(expected, ChassisUiState.CanRouteInput(mode, split));

    private static ChassisBladeState State(ChassisBladeStatus status) => new(
        2,
        status,
        0xA0,
        0,
        IPAddress.Parse("192.0.2.2"),
        7500,
        false,
        status == ChassisBladeStatus.KvmBusy ? "operator" : null,
        true);
}
