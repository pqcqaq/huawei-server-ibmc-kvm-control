using IbmcKvm.Core.Input;

namespace IbmcKvm.Core.Tests.Input;

public sealed class WindowsVirtualKeyMapTests
{
    [Theory]
    [InlineData(0x41, 0x04)]
    [InlineData(0x5A, 0x1D)]
    [InlineData(0x31, 0x1E)]
    [InlineData(0x30, 0x27)]
    [InlineData(0x0D, 0x28)]
    [InlineData(0x70, 0x3A)]
    [InlineData(0x7B, 0x45)]
    [InlineData(0x60, 0x62)]
    [InlineData(0x69, 0x61)]
    public void MapsVirtualKeyToUsbHidUsage(int virtualKey, byte expected)
    {
        Assert.True(WindowsVirtualKeyMap.TryGetUsage(virtualKey, out var usage));
        Assert.Equal(expected, usage);
    }

    [Theory]
    [InlineData(0xA2, HidModifiers.LeftControl)]
    [InlineData(0xA5, HidModifiers.RightAlt)]
    [InlineData(0x5C, HidModifiers.RightGui)]
    public void MapsModifierVirtualKey(int virtualKey, HidModifiers expected)
    {
        Assert.True(WindowsVirtualKeyMap.TryGetModifier(virtualKey, out var modifier));
        Assert.Equal(expected, modifier);
    }
}
