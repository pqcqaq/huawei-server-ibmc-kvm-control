using Avalonia.Input;
using IbmcKvm.Core.Input;
using IbmcKvm.Desktop.Input;

namespace IbmcKvm.Desktop.Tests.Input;

public sealed class AvaloniaHidKeyMapTests
{
    [Theory]
    [InlineData(Key.A, 0x04)]
    [InlineData(Key.Z, 0x1D)]
    [InlineData(Key.D1, 0x1E)]
    [InlineData(Key.D0, 0x27)]
    [InlineData(Key.Enter, 0x28)]
    [InlineData(Key.F12, 0x45)]
    [InlineData(Key.Delete, 0x4C)]
    [InlineData(Key.NumPad0, 0x62)]
    public void MapsKeysToUsbHidUsage(Key key, byte expected)
    {
        Assert.True(AvaloniaHidKeyMap.TryGetUsage(key, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MapsRightAltModifier()
    {
        Assert.True(AvaloniaHidKeyMap.TryGetModifier(Key.RightAlt, out var modifier));
        Assert.Equal(HidModifiers.RightAlt, modifier);
    }

    [Theory]
    [InlineData(PhysicalKey.A, 0x04)]
    [InlineData(PhysicalKey.Digit0, 0x27)]
    [InlineData(PhysicalKey.ArrowDown, 0x51)]
    [InlineData(PhysicalKey.NumPadEnter, 0x28)]
    [InlineData(PhysicalKey.IntlYen, 0x89)]
    public void MapsPhysicalKeysToUsbHidUsage(PhysicalKey key, byte expected)
    {
        Assert.True(AvaloniaHidKeyMap.TryGetUsage(key, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MapsPhysicalLeftControlModifier()
    {
        Assert.True(AvaloniaHidKeyMap.TryGetModifier(PhysicalKey.ControlLeft, out var modifier));
        Assert.Equal(HidModifiers.LeftControl, modifier);
    }
}
