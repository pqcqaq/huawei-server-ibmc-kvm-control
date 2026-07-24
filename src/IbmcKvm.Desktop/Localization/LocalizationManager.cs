using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;

namespace IbmcKvm.Desktop.Localization;

internal static class LocalizationManager
{
    private static readonly ImmutableDictionary<string, ImmutableDictionary<string, string>> Catalogs =
        LocalizationCatalog.SupportedLanguages.ToImmutableDictionary(
            language => language.CultureName,
            language => LocalizationCatalog.Load(language.CultureName),
            StringComparer.OrdinalIgnoreCase);
    private static readonly ImmutableDictionary<string, string> ReverseExact = BuildReverseExact();

    public static string CurrentCultureName { get; private set; } = LocalizationCatalog.DefaultCulture;

    public static void SetCulture(string cultureName)
    {
        var normalized = LocalizationCatalog.NormalizeCulture(cultureName);
        CurrentCultureName = normalized;
        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                Apply(window);
            }
        }
    }

    public static string Translate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var canonical = ReverseExact.TryGetValue(value, out var key) ? key : value;
        return Catalogs[CurrentCultureName].TryGetValue(canonical, out var translated)
            ? translated
            : value;
    }

    public static string Format(string value, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Translate(value), arguments);

    public static void Apply(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var controls = root.GetLogicalDescendants().OfType<Control>().Prepend(root).ToArray();
        foreach (var control in controls)
        {
            ApplyElement(control);
            if (control.ContextMenu is { } menu)
            {
                Apply(menu);
            }
        }
    }

    private static void ApplyElement(Control control)
    {
        if (control is Window window)
        {
            window.Title = Translate(window.Title);
        }

        if (control is TextBlock textBlock)
        {
            textBlock.Text = Translate(textBlock.Text);
        }

        if (control is ContentControl { Content: string content } contentControl)
        {
            contentControl.Content = Translate(content);
        }

        if (control is HeaderedContentControl { Header: string contentHeader } headeredContent)
        {
            headeredContent.Header = Translate(contentHeader);
        }

        if (control is HeaderedItemsControl { Header: string itemsHeader } headeredItems)
        {
            headeredItems.Header = Translate(itemsHeader);
        }

        if (ToolTip.GetTip(control) is string tip)
        {
            ToolTip.SetTip(control, Translate(tip));
        }
    }

    private static ImmutableDictionary<string, string> BuildReverseExact()
    {
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var catalog in Catalogs.Values)
        {
            foreach (var pair in catalog)
            {
                result.TryAdd(pair.Key, pair.Key);
                result.TryAdd(pair.Value, pair.Key);
            }
        }

        return result.ToImmutable();
    }
}
