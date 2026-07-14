namespace IbmcKvm.App.Ui;

internal sealed class FloatingToolbarState(bool isPinned = true)
{
    public bool IsPinned { get; private set; } = isPinned;

    public bool IsVisible { get; private set; } = true;

    public void SetPinned(bool isPinned)
    {
        IsPinned = isPinned;
        IsVisible = true;
    }

    public void Reveal() => IsVisible = true;

    public void HideAfterPointerLeaves(bool isPointerOverToolbar)
    {
        if (!IsPinned && !isPointerOverToolbar)
        {
            IsVisible = false;
        }
    }
}
