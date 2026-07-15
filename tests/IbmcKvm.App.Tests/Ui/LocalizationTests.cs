using IbmcKvm.App.Localization;

namespace IbmcKvm.App.Tests.Ui;

public sealed class LocalizationTests
{
    [Fact]
    public void EverySupportedLanguageHasTheSameCompleteKeySet()
    {
        var baseline = LocalizationCatalog.Load(LocalizationCatalog.DefaultCulture);

        foreach (var language in LocalizationCatalog.SupportedLanguages)
        {
            var catalog = LocalizationCatalog.Load(language.CultureName);
            Assert.Equal(baseline.Keys.Order(), catalog.Keys.Order());
            Assert.All(catalog.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        }
    }

    [Fact]
    public void TranslationsDoNotCollideWithAnotherCanonicalKey()
    {
        var canonicalKeys = LocalizationCatalog.Load(LocalizationCatalog.DefaultCulture).Keys.ToHashSet();

        foreach (var language in LocalizationCatalog.SupportedLanguages
                     .Where(language => language.CultureName != LocalizationCatalog.DefaultCulture))
        {
            var catalog = LocalizationCatalog.Load(language.CultureName);
            Assert.DoesNotContain(
                catalog,
                pair => pair.Key != pair.Value && canonicalKeys.Contains(pair.Value));
        }
    }

    [Theory]
    [InlineData("unknown", "zh-CN")]
    [InlineData("EN-us", "en-US")]
    [InlineData("ja-JP", "ja-JP")]
    public void NormalizesSupportedCultureNames(string input, string expected) =>
        Assert.Equal(expected, LocalizationCatalog.NormalizeCulture(input));

    [Theory]
    [InlineData("en-US", "电源", "Power")]
    [InlineData("ja-JP", "电源", "電源")]
    [InlineData("fr-FR", "电源", "Alimentation")]
    [InlineData("en-US", "虚拟软驱与光驱", "Virtual floppy and optical drives")]
    [InlineData("ja-JP", "虚拟软驱与光驱", "仮想フロッピーと光学ドライブ")]
    [InlineData("fr-FR", "虚拟软驱与光驱", "Lecteurs virtuels de disquette et optique")]
    public void TranslatesHelpNavigationAndHeadings(string cultureName, string source, string expected)
    {
        try
        {
            LocalizationManager.SetCulture(cultureName);

            Assert.Equal(expected, LocalizationManager.Translate(source));
        }
        finally
        {
            LocalizationManager.SetCulture(LocalizationCatalog.DefaultCulture);
        }
    }

    [Theory]
    [InlineData("en-US", "Recovering KVM (2/4)")]
    [InlineData("ja-JP", "KVM を復旧中 (2/4)")]
    [InlineData("fr-FR", "Récupération KVM (2/4)")]
    public void FormatsParameterizedRuntimeStatus(string cultureName, string expected)
    {
        try
        {
            LocalizationManager.SetCulture(cultureName);

            Assert.Equal(expected, LocalizationManager.Format("正在恢复 KVM（{0}/{1}）", 2, 4));
        }
        finally
        {
            LocalizationManager.SetCulture(LocalizationCatalog.DefaultCulture);
        }
    }

    [Fact]
    public void FormatsMultilineCertificateDecisionUsingSelectedCulture()
    {
        try
        {
            LocalizationManager.SetCulture("en-US");
            var result = LocalizationManager.Format(
                "仅为 {0}:{1} 导入此 CA？\n\n主题：{2}\n颁发者：{3}\n有效期：{4:yyyy-MM-dd} 至 {5:yyyy-MM-dd}\n\nSHA-256：\n{6}",
                "bmc.example",
                443,
                "CN=Test",
                "CN=Issuer",
                new DateTime(2026, 1, 2),
                new DateTime(2030, 12, 31),
                "AA:BB");

            Assert.Contains("Import this CA only for bmc.example:443?", result);
            Assert.Contains("Validity: 2026-01-02 to 2030-12-31", result);
            Assert.Contains("SHA-256:\nAA:BB", result);
        }
        finally
        {
            LocalizationManager.SetCulture(LocalizationCatalog.DefaultCulture);
        }
    }

    [Theory]
    [InlineData("--language=en-US", "en-US")]
    [InlineData("--LANGUAGE=ja-JP", "ja-JP")]
    [InlineData("--language=invalid", "zh-CN")]
    public void ParsesOneRunLanguageOverride(string argument, string expected) =>
        Assert.Equal(expected, AppStartupOptions.Parse([argument]).CultureName);
}
