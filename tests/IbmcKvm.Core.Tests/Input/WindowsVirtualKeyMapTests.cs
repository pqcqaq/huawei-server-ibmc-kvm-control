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
    [InlineData(0x14, 0x39)]
    [InlineData(0x70, 0x3A)]
    [InlineData(0x7B, 0x45)]
    [InlineData(0x91, 0x47)]
    [InlineData(0x90, 0x53)]
    [InlineData(0x60, 0x62)]
    [InlineData(0x69, 0x61)]
    public void MapsVirtualKeyToUsbHidUsage(int virtualKey, byte expected)
    {
        Assert.True(WindowsVirtualKeyMap.TryGetUsage(virtualKey, out var usage));
        Assert.Equal(expected, usage);
    }

    [Theory]
    [InlineData(0x14)]
    [InlineData(0x90)]
    [InlineData(0x91)]
    public void IdentifiesLockKeysThatRequireRemoteStateRefresh(int virtualKey)
    {
        Assert.True(WindowsVirtualKeyMap.IsLockKey(virtualKey));
        Assert.False(WindowsVirtualKeyMap.IsLockKey(0x41));
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

    [Theory]
    [InlineData(0xDB, false, 0x30)]
    [InlineData(0xDD, false, 0x31)]
    [InlineData(0xDC, false, 0x89)]
    [InlineData(0xDC, true, 0x87)]
    [InlineData(0x1C, false, 0x8A)]
    public void MapsJapaneseKeyboardSpecificKeys(int virtualKey, bool shifted, byte expected)
    {
        Assert.True(WindowsVirtualKeyMap.TryGetUsage(
            virtualKey,
            RemoteKeyboardLayout.Japanese,
            shifted,
            out var usage));
        Assert.Equal(expected, usage);
    }

    [Theory]
    [InlineData(0x41, 0x14)]
    [InlineData(0x51, 0x04)]
    [InlineData(0x5A, 0x1A)]
    [InlineData(0x57, 0x1D)]
    [InlineData(0x4D, 0x33)]
    [InlineData(0xBC, 0x10)]
    public void MapsFrenchAzertyPositions(int virtualKey, byte expected)
    {
        Assert.True(WindowsVirtualKeyMap.TryGetUsage(
            virtualKey,
            RemoteKeyboardLayout.French,
            shifted: false,
            out var usage));
        Assert.Equal(expected, usage);
    }
}
