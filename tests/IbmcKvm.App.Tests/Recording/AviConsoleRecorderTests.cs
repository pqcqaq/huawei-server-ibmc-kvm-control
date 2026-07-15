using System.IO;
using System.Text;
using IbmcKvm.App.Recording;
using IbmcKvm.Core.Video;

namespace IbmcKvm.App.Tests.Recording;

public sealed class AviConsoleRecorderTests
{
    [Fact]
    public async Task EncodesDecodedFramesIntoPlayableMjpegChunks()
    {
        await using var stream = new MemoryStream();
        AviConsoleRecorder recorder;
        await using (recorder = new AviConsoleRecorder(stream, 2, 2, queueCapacity: 2))
        {
            Assert.True(recorder.TryRecord(Frame(2, 2), Pixels(2, 2, 0x20)));
            Assert.True(recorder.TryRecord(Frame(1, 1), Pixels(1, 1, 0xE0)));
        }

        var bytes = stream.ToArray();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("MJPG", Encoding.ASCII.GetString(bytes, 112, 4));
        Assert.Equal("00dc", Encoding.ASCII.GetString(bytes, 224, 4));
        Assert.True(bytes.AsSpan().IndexOf(Convert.FromHexString("FFD8")) >= 0);
        Assert.Null(recorder.Failure);
    }

    private static EncodedVideoFrame Frame(int width, int height) => new(
        FrameNumber: 1,
        IsDifference: false,
        Width: width,
        Height: height,
        RemoteCursorX: 0,
        RemoteCursorY: 0,
        ColorDepth: 3,
        QuantizationTable: 2,
        EncodedData: [0]);

    private static byte[] Pixels(int width, int height, byte value) =>
        Enumerable.Repeat(value, width * height * 4).ToArray();
}
