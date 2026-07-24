using System.Text.Json;
using IbmcKvm.Desktop.Localization;

namespace IbmcKvm.Desktop.Settings;

internal sealed class UiPreferencesStore
{
    private const int CurrentVersion = 1;
    private const int MaximumLength = 4096;
    private readonly string filePath;

    public UiPreferencesStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IbmcKvm",
            "ui-preferences.json"))
    {
    }

    internal UiPreferencesStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = Path.GetFullPath(filePath);
    }

    public string LoadCulture()
    {
        try
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length is 0 or > MaximumLength)
            {
                return LocalizationCatalog.DefaultCulture;
            }

            var preferences = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllBytes(filePath));
            return preferences?.Version == CurrentVersion
                ? LocalizationCatalog.NormalizeCulture(preferences.CultureName)
                : LocalizationCatalog.DefaultCulture;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return LocalizationCatalog.DefaultCulture;
        }
    }

    public void SaveCulture(string cultureName)
    {
        var normalized = LocalizationCatalog.NormalizeCulture(cultureName);
        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The UI-preferences path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(
                temporaryPath,
                JsonSerializer.SerializeToUtf8Bytes(new UiPreferences(CurrentVersion, normalized)));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record UiPreferences(int Version, string CultureName);
}
