using IbmcKvm.App.Ui;

namespace IbmcKvm.App.Tests.Ui;

public sealed class FloatingToolbarStateTests
{
    [Fact]
    public void DefaultToolbarStartsPinnedAndVisible()
    {
        var state = new FloatingToolbarState();

        Assert.True(state.IsPinned);
        Assert.True(state.IsVisible);
    }

    [Fact]
    public void PinnedToolbarStaysVisibleWhenPointerLeaves()
    {
        var state = new FloatingToolbarState(isPinned: true);

        state.HideAfterPointerLeaves(isPointerOverToolbar: false);

        Assert.True(state.IsVisible);
        Assert.True(state.IsPinned);
    }

    [Fact]
    public void UnpinnedToolbarHidesOnlyAfterPointerLeaves()
    {
        var state = new FloatingToolbarState(isPinned: false);

        state.HideAfterPointerLeaves(isPointerOverToolbar: true);
        Assert.True(state.IsVisible);

        state.HideAfterPointerLeaves(isPointerOverToolbar: false);
        Assert.False(state.IsVisible);
    }

    [Fact]
    public void RevealingAnUnpinnedToolbarMakesItVisibleAgain()
    {
        var state = new FloatingToolbarState(isPinned: false);
        state.HideAfterPointerLeaves(isPointerOverToolbar: false);

        state.Reveal();

        Assert.True(state.IsVisible);
        Assert.False(state.IsPinned);
    }
}
