using System.IO;
using System.Threading.Channels;
using IbmcKvm.Core.Video;
using SkiaSharp;

namespace IbmcKvm.Core.Recording;

public sealed class AviConsoleRecorder : IAsyncDisposable
{
    private readonly MjpegAviWriter writer;
    private readonly int writerWidth;
    private readonly int writerHeight;
    private readonly Channel<DecodedRecordingFrame> frames;
    private readonly Task writerLoop;
    private long droppedFrames;
    private int disposed;

    public AviConsoleRecorder(
        Stream stream,
        int width,
        int height,
        int queueCapacity = 4)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        writerWidth = width;
        writerHeight = height;
        writer = new MjpegAviWriter(stream, width, height, frameRate: 20);
        frames = Channel.CreateBounded<DecodedRecordingFrame>(new BoundedChannelOptions(queueCapacity)
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

    public bool TryRecord(EncodedVideoFrame frame, byte[] bgraPixels)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(bgraPixels);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (bgraPixels.Length != checked(frame.Width * frame.Height * 4))
        {
            throw new ArgumentException("The decoded frame pixel length is invalid.", nameof(bgraPixels));
        }

        if (frames.Writer.TryWrite(new(frame.Width, frame.Height, bgraPixels)))
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
    }

    private async Task RunWriterAsync()
    {
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var jpeg = EncodeJpeg(frame, writerWidth, writerHeight);
                await writer.WriteFrameAsync(jpeg).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Failure = exception;
            frames.Writer.TryComplete(exception);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static byte[] EncodeJpeg(
        DecodedRecordingFrame frame,
        int writerWidth,
        int writerHeight)
    {
        var sourceInfo = new SKImageInfo(
            frame.Width,
            frame.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Unpremul);
        using var source = new SKBitmap(sourceInfo);
        System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, source.GetPixels(), frame.Pixels.Length);
        using var destination = frame.Width == writerWidth && frame.Height == writerHeight
            ? null
            : new SKBitmap(new SKImageInfo(
                writerWidth,
                writerHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Unpremul));
        var encodedSource = source;
        if (destination is not null)
        {
            if (!source.ScalePixels(destination, SKFilterQuality.Medium))
            {
                throw new InvalidDataException("The decoded frame could not be resized for AVI recording.");
            }

            encodedSource = destination;
        }

        using var image = SKImage.FromBitmap(encodedSource);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85)
            ?? throw new InvalidDataException("The decoded frame could not be encoded as JPEG.");
        return encoded.ToArray();
    }

    private sealed record DecodedRecordingFrame(int Width, int Height, byte[] Pixels);
}
