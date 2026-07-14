using IbmcKvm.Core.Input;

namespace IbmcKvm.Core.Tests.Input;

public sealed class AbsoluteCoordinateMapperTests
{
    [Theory]
    [InlineData(-10, 100, 0)]
    [InlineData(0, 100, 0)]
    [InlineData(50, 100, 1500)]
    [InlineData(100, 100, 3000)]
    [InlineData(110, 100, 3000)]
    public void MapsAndClampsViewerCoordinates(double coordinate, double extent, ushort expected)
    {
        Assert.Equal(expected, AbsoluteCoordinateMapper.Map(coordinate, extent));
    }
}
