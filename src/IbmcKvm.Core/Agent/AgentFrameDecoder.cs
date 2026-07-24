using System.Runtime.InteropServices;
using SkiaSharp;

namespace IbmcKvm.Core.Agent;

public sealed record AgentDecodedFrame(uint Sequence, int Width, int Height, byte[] BgraPixels);

public sealed class AgentFrameDecoder
{
    private byte[] pixels = [];
    private ushort width;
    private ushort height;
    private uint lastSequence;
    private bool hasFrame;

    public AgentDecodedFrame Decode(AgentVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if ((long)frame.Width * frame.Height > AgentProtocol.MaximumFramePixelCount)
        {
            Reset();
            throw new InvalidDataException("An Agent frame exceeds the maximum pixel count.");
        }
        if (!frame.IsKeyframe && (!hasFrame || unchecked(lastSequence + 1) != frame.Sequence))
        {
            Reset();
            throw new InvalidDataException("An Agent difference frame does not follow the last decoded frame.");
        }
        if (frame.IsKeyframe || frame.Width != width || frame.Height != height)
        {
            if (!frame.IsKeyframe)
            {
                Reset();
                throw new InvalidDataException("An Agent resolution change requires a keyframe.");
            }
            width = frame.Width;
            height = frame.Height;
            pixels = new byte[checked(width * height * 4)];
        }

        foreach (var tile in frame.Tiles)
        {
            DecodeTile(tile);
        }
        hasFrame = true;
        lastSequence = frame.Sequence;
        return new AgentDecodedFrame(frame.Sequence, width, height, pixels.ToArray());
    }

    public void Reset()
    {
        pixels = [];
        width = 0;
        height = 0;
        lastSequence = 0;
        hasFrame = false;
    }

    private void DecodeTile(AgentTile tile)
    {
        using var decoded = SKBitmap.Decode(tile.Jpeg)
            ?? throw new InvalidDataException("An Agent JPEG tile could not be decoded.");
        if (decoded.Width != tile.Width || decoded.Height != tile.Height)
        {
            throw new InvalidDataException("An Agent JPEG tile dimensions do not match its metadata.");
        }
        using var converted = new SKBitmap(new SKImageInfo(
            tile.Width,
            tile.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Unpremul));
        if (!decoded.CopyTo(converted, SKColorType.Bgra8888))
        {
            throw new InvalidDataException("An Agent JPEG tile could not be converted to BGRA32.");
        }
        var tilePixels = new byte[checked(tile.Width * tile.Height * 4)];
        Marshal.Copy(converted.GetPixels(), tilePixels, 0, tilePixels.Length);
        for (var row = 0; row < tile.Height; row++)
        {
            Buffer.BlockCopy(
                tilePixels,
                row * tile.Width * 4,
                pixels,
                ((tile.Y + row) * width + tile.X) * 4,
                tile.Width * 4);
        }
    }
}
