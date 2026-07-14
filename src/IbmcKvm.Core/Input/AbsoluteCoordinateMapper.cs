namespace IbmcKvm.Core.Input;

public static class AbsoluteCoordinateMapper
{
    public static ushort Map(double coordinate, double extent)
    {
        if (!double.IsFinite(coordinate) || !double.IsFinite(extent) || extent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extent));
        }

        return checked((ushort)Math.Round(
            Math.Clamp(coordinate, 0, extent) * 3000 / extent,
            MidpointRounding.AwayFromZero));
    }
}
