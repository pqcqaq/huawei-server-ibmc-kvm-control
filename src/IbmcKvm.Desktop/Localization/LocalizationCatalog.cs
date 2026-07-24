using System.Collections.Immutable;
using System.Text.Json;

namespace IbmcKvm.Desktop.Localization;

internal sealed record SupportedLanguage(string CultureName, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal static class LocalizationCatalog
{
    public const string DefaultCulture = "zh-CN";

    public static ImmutableArray<SupportedLanguage> SupportedLanguages { get; } =
    [
        new("zh-CN", "简体中文"),
        new("en-US", "English"),
        new("ja-JP", "日本語"),
        new("fr-FR", "Français"),
    ];

    public static ImmutableDictionary<string, string> Load(string cultureName)
    {
        var normalized = NormalizeCulture(cultureName);
        var baseline = LoadRaw(DefaultCulture);
        var values = string.Equals(normalized, DefaultCulture, StringComparison.OrdinalIgnoreCase)
            ? baseline
            : LoadRaw(normalized);
        if (!baseline.Keys.Order(StringComparer.Ordinal).SequenceEqual(
                values.Keys.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"The localization resource {normalized} does not contain the complete canonical key set.");
        }

        return values.ToImmutableDictionary(StringComparer.Ordinal);
    }

    public static string NormalizeCulture(string? cultureName) =>
        SupportedLanguages.FirstOrDefault(language =>
            string.Equals(language.CultureName, cultureName, StringComparison.OrdinalIgnoreCase))?.CultureName
        ?? DefaultCulture;

    private static Dictionary<string, string> LoadRaw(string cultureName)
    {
        var resourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", $"Strings.{cultureName}.json");
        if (!File.Exists(resourcePath))
        {
            throw new InvalidOperationException($"The localization resource {resourcePath} is missing.");
        }

        using var stream = File.OpenRead(resourcePath);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException($"The localization resource {resourcePath} is empty.");
        if (values.Count == 0 || values.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw new InvalidDataException($"The localization resource {resourcePath} contains an empty key or value.");
        }

        return values;
    }
}
