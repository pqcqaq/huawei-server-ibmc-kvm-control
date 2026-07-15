namespace IbmcKvm.App.Localization;

internal sealed record AppStartupOptions(string? CultureName)
{
    public static AppStartupOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        const string prefix = "--language=";
        var value = arguments.FirstOrDefault(argument =>
            argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return new AppStartupOptions(
            value is null ? null : LocalizationCatalog.NormalizeCulture(value[prefix.Length..]));
    }
}
