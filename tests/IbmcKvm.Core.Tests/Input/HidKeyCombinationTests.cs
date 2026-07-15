using IbmcKvm.Core.Input;

namespace IbmcKvm.Core.Tests.Input;

public sealed class HidKeyCombinationTests
{
    [Theory]
    [MemberData(nameof(Presets))]
    public void BuildsSourceCompatiblePresetReports(HidKeyCombination combination, byte[] expected)
    {
        Assert.Equal(expected, combination.CreateReport());
    }

    [Fact]
    public void ConvertsModifierUsagesAndSortsOrdinaryKeys()
    {
        var combination = HidKeyCombination.Create(0x4C, 0xE2, 0x29, 0xE0);

        Assert.Equal(HidModifiers.LeftControl | HidModifiers.LeftAlt, combination.Modifiers);
        Assert.Equal(new byte[] { 5, 0, 0x29, 0x4C, 0, 0, 0, 0 }, combination.CreateReport());
    }

    [Fact]
    public void RejectsMoreThanSixOrDuplicateUsages()
    {
        Assert.Throws<ArgumentException>(() => HidKeyCombination.Create(4, 5, 6, 7, 8, 9, 10));
        Assert.Throws<ArgumentException>(() => HidKeyCombination.Create(4, 4));
    }

    public static TheoryData<HidKeyCombination, byte[]> Presets => new()
    {
        { HidKeyCombination.CtrlShift, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0 } },
        { HidKeyCombination.CtrlEscape, new byte[] { 1, 0, 0x29, 0, 0, 0, 0, 0 } },
        { HidKeyCombination.CtrlAltDelete, new byte[] { 5, 0, 0x4C, 0, 0, 0, 0, 0 } },
        { HidKeyCombination.AltTab, new byte[] { 4, 0, 0x2B, 0, 0, 0, 0, 0 } },
        { HidKeyCombination.CtrlSpace, new byte[] { 1, 0, 0x2C, 0, 0, 0, 0, 0 } },
        { HidKeyCombination.KeyboardReset, new byte[8] },
    };
}
