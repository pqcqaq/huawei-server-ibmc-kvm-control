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

        var transition = state.HideAfterPointerLeaves(isPointerOverToolbar: false);

        Assert.True(state.IsVisible);
        Assert.True(state.IsPinned);
        Assert.Equal(FloatingToolbarTransition.None, transition);
    }

    [Fact]
    public void UnpinnedToolbarHidesOnlyAfterPointerLeaves()
    {
        var state = new FloatingToolbarState(isPinned: false);

        var transitionWhileOver = state.HideAfterPointerLeaves(isPointerOverToolbar: true);
        Assert.True(state.IsVisible);
        Assert.Equal(FloatingToolbarTransition.None, transitionWhileOver);

        var transition = state.HideAfterPointerLeaves(isPointerOverToolbar: false);
        Assert.False(state.IsVisible);
        Assert.Equal(FloatingToolbarTransition.Hide, transition);
    }

    [Fact]
    public void RevealingAnUnpinnedToolbarMakesItVisibleAgain()
    {
        var state = new FloatingToolbarState(isPinned: false);
        state.HideAfterPointerLeaves(isPointerOverToolbar: false);

        var transition = state.Reveal();

        Assert.True(state.IsVisible);
        Assert.False(state.IsPinned);
        Assert.Equal(FloatingToolbarTransition.Show, transition);
    }
}
