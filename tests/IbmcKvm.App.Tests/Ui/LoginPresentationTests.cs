using IbmcKvm.App.Ui;

namespace IbmcKvm.App.Tests.Ui;

public sealed class LoginPresentationTests
{
    [Fact]
    public void ConnectingDisablesFormAndShowsLoadingMessage()
    {
        var state = LoginPresentation.Resolve(LoginPhase.Connecting, "正在协商 KVM 通道");

        Assert.False(state.IsFormEnabled);
        Assert.True(state.IsLoading);
        Assert.False(state.IsErrorVisible);
        Assert.Equal("正在协商 KVM 通道", state.StatusText);
    }

    [Fact]
    public void FailureRestoresFormAndExposesTheError()
    {
        var state = LoginPresentation.Resolve(LoginPhase.Failed, "凭据无效");

        Assert.True(state.IsFormEnabled);
        Assert.False(state.IsLoading);
        Assert.True(state.IsErrorVisible);
        Assert.Equal("凭据无效", state.StatusText);
    }
}
