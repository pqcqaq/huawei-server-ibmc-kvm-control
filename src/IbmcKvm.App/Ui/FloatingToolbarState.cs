namespace IbmcKvm.App.Ui;

internal enum FloatingToolbarTransition
{
    None,
    Show,
    Hide,
}

internal sealed class FloatingToolbarState(bool isPinned = true)
{
    public bool IsPinned { get; private set; } = isPinned;

    public bool IsVisible { get; private set; } = true;

    public FloatingToolbarTransition SetPinned(bool isPinned)
    {
        IsPinned = isPinned;
        IsVisible = true;
        return FloatingToolbarTransition.None;
    }

    public FloatingToolbarTransition Reveal()
    {
        if (IsVisible)
        {
            return FloatingToolbarTransition.None;
        }

        IsVisible = true;
        return FloatingToolbarTransition.Show;
    }

    public FloatingToolbarTransition HideAfterPointerLeaves(bool isPointerOverToolbar)
    {
        if (IsPinned || isPointerOverToolbar || !IsVisible)
        {
            return FloatingToolbarTransition.None;
        }

        IsVisible = false;
        return FloatingToolbarTransition.Hide;
    }
}
