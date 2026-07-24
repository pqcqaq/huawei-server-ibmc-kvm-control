using IbmcKvm.Desktop.Ui;

namespace IbmcKvm.Desktop.Tests.Ui;

public sealed class DoubleBufferTests
{
    [Fact]
    public void AlternatesBetweenTwoBuffersAfterWarmup()
    {
        using var buffers = new DoubleBuffer<TestBuffer>();
        var created = 0;

        var first = buffers.AcquireWritable(_ => true, Create);
        Assert.Same(first, buffers.Publish(first));

        var second = buffers.AcquireWritable(_ => true, Create);
        Assert.NotSame(first, second);
        Assert.Same(second, buffers.Publish(second));

        var third = buffers.AcquireWritable(_ => true, Create);
        Assert.Same(first, third);
        Assert.Same(third, buffers.Publish(third));
        Assert.Equal(2, created);

        TestBuffer Create() => new(++created);
    }

    [Fact]
    public void ReplacesAndDisposesBothBuffersWhenTheyCannotBeReused()
    {
        using var buffers = new DoubleBuffer<TestBuffer>();
        var first = buffers.AcquireWritable(_ => true, () => new TestBuffer(1));
        buffers.Publish(first);
        var second = buffers.AcquireWritable(_ => true, () => new TestBuffer(1));
        buffers.Publish(second);

        var replacement = buffers.AcquireWritable(buffer => buffer.Size == 2, () => new TestBuffer(2));

        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.Equal(2, replacement.Size);
        Assert.Null(buffers.Front);
        Assert.Same(replacement, buffers.Publish(replacement));
    }

    private sealed class TestBuffer(int size) : IDisposable
    {
        public int Size { get; } = size;

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
