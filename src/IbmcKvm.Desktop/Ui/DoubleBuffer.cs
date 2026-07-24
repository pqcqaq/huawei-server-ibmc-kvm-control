namespace IbmcKvm.Desktop.Ui;

internal sealed class DoubleBuffer<T> : IDisposable
    where T : class, IDisposable
{
    private T? front;
    private T? back;

    public T? Front => front;

    public T AcquireWritable(Func<T, bool> canReuse, Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(canReuse);
        ArgumentNullException.ThrowIfNull(factory);

        if ((front is not null && !canReuse(front)) ||
            (back is not null && !canReuse(back)))
        {
            Reset();
        }

        return back ??= factory();
    }

    public T Publish(T buffer)
    {
        if (!ReferenceEquals(buffer, back))
        {
            throw new InvalidOperationException("Only the acquired back buffer can be published.");
        }

        (front, back) = (back, front);
        return front!;
    }

    public void Reset()
    {
        front?.Dispose();
        if (!ReferenceEquals(front, back))
        {
            back?.Dispose();
        }

        front = null;
        back = null;
    }

    public void Dispose() => Reset();
}
