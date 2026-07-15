using IbmcKvm.Core.Session;
using IbmcKvm.Protocol.Session;
using IbmcKvm.App.Localization;

namespace IbmcKvm.App.Ui;

internal sealed record ChassisBladePresentation(
    byte BladeNumber,
    string Name,
    string Status,
    string Detail,
    bool CanConnect,
    bool CanMonitor,
    bool IsConnected);

internal static class ChassisUiState
{
    public static ChassisBladePresentation Resolve(
        ChassisBladeState state,
        IReadOnlyCollection<byte> connectedBlades)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(connectedBlades);
        var connected = connectedBlades.Contains(state.BladeNumber);
        var status = LocalizationManager.Translate(connected
            ? "已连接"
            : state.Status switch
            {
                ChassisBladeStatus.Available => "可连接",
                ChassisBladeStatus.Absent => "未安装",
                ChassisBladeStatus.BmcResetting => "BMC 重置中",
                ChassisBladeStatus.KvmUnsupported => "不支持 KVM",
                ChassisBladeStatus.KvmBusy => "KVM 使用中",
                ChassisBladeStatus.ConnectionLimitReached => "连接数已满",
                ChassisBladeStatus.SolActive => "SOL 使用中",
                ChassisBladeStatus.FirmwareLoading => "固件加载中",
                _ => "不可用",
            });
        var endpoint = state.Address is null
            ? string.Empty
            : state.Port is { } port
                ? $"{state.Address}:{port}"
                : state.Address.ToString();
        var detail = state.BusyUserName is { Length: > 0 }
            ? $"{endpoint}  {state.BusyUserName}".Trim()
            : endpoint;
        return new ChassisBladePresentation(
            state.BladeNumber,
            LocalizationManager.Format("刀片 {0}", state.BladeNumber),
            status,
            detail,
            !connected && state.CanControl,
            !connected && state.CanMonitor,
            connected);
    }

    public static bool CanRouteInput(
        KvmBladeSessionMode mode,
        bool splitViewEnabled) =>
        mode == KvmBladeSessionMode.Control && !splitViewEnabled;
}
