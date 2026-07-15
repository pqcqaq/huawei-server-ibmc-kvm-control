using System.IO;
using IbmcKvm.App.Settings;

namespace IbmcKvm.App.Tests.Settings;

public sealed class UiPreferencesStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IbmcKvm.App.Tests", Guid.NewGuid().ToString("N"));
    private readonly string filePath;

    public UiPreferencesStoreTests()
    {
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "ui.json");
    }

    [Fact]
    public void SavesSupportedLanguageWithoutConnectionSettings()
    {
        var store = new UiPreferencesStore(filePath);

        store.SaveCulture("fr-FR");

        Assert.Equal("fr-FR", store.LoadCulture());
    }

    [Fact]
    public void InvalidOrCorruptPreferenceFallsBackToChinese()
    {
        File.WriteAllText(filePath, "not json");
        var store = new UiPreferencesStore(filePath);

        Assert.Equal("zh-CN", store.LoadCulture());
        store.SaveCulture("unsupported");
        Assert.Equal("zh-CN", store.LoadCulture());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
