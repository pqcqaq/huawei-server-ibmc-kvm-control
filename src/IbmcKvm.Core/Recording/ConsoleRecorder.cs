using System.Threading.Channels;
using IbmcKvm.Core.Video;

namespace IbmcKvm.Core.Recording;

public sealed class ConsoleRecorder : IAsyncDisposable
{
    private readonly RepRecordingWriter writer;
    private readonly Channel<RecordingFrame> frames;
    private readonly Task writerLoop;
    private int nextSequence;
    private long droppedFrames;
    private int disposed;

    public ConsoleRecorder(RepRecordingWriter writer, int queueCapacity = 64)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        this.writer = writer;
        frames = Channel.CreateBounded<RecordingFrame>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        writerLoop = RunWriterAsync();
    }

    public long DroppedFrames => Interlocked.Read(ref droppedFrames);

    public Exception? Failure { get; private set; }

    public bool TryRecord(EncodedVideoFrame frame, long timestampMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var copy = frame with { EncodedData = frame.EncodedData.ToArray() };
        if (frames.Writer.TryWrite(new(copy, timestampMilliseconds)))
        {
            return true;
        }

        Interlocked.Increment(ref droppedFrames);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        frames.Writer.TryComplete();
        await writerLoop.ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunWriterAsync()
    {
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await writer.WriteFrameAsync(
                    frame.Frame,
                    Interlocked.Increment(ref nextSequence),
                    frame.TimestampMilliseconds).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Failure = exception;
            frames.Writer.TryComplete(exception);
        }
    }

    private sealed record RecordingFrame(EncodedVideoFrame Frame, long TimestampMilliseconds);
}
