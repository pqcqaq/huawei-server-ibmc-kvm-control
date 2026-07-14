namespace IbmcKvm.App.Ui;

internal enum LoginPhase
{
    Ready,
    Connecting,
    Failed,
}

internal sealed record LoginPresentation(
    bool IsFormEnabled,
    bool IsLoading,
    bool IsErrorVisible,
    string StatusText)
{
    public static LoginPresentation Resolve(LoginPhase phase, string? detail = null) => phase switch
    {
        LoginPhase.Ready => new(true, false, false, detail ?? string.Empty),
        LoginPhase.Connecting => new(false, true, false, detail ?? "正在连接 iBMC"),
        LoginPhase.Failed => new(true, false, true, detail ?? "连接失败，请检查连接设置。"),
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };
}
