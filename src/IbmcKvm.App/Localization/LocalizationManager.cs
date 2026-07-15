using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace IbmcKvm.App.Localization;

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
        foreach (Window window in Application.Current?.Windows ?? [])
        {
            Apply(window);
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

    public static void Apply(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            ApplyElement(current);
            if (current is FrameworkElement { ContextMenu: { } contextMenu })
            {
                pending.Push(contextMenu);
            }

            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
            {
                pending.Push(child);
            }

            if (current is Visual or Visual3D)
            {
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var index = 0; index < count; index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }
        }
    }

    public static void ApplyElement(DependencyObject element)
    {
        if (element is FrameworkElement frameworkElement)
        {
            frameworkElement.Language = XmlLanguage.GetLanguage(CurrentCultureName);
            if (frameworkElement.ToolTip is string toolTip)
            {
                frameworkElement.ToolTip = Translate(toolTip);
            }
        }

        if (element is Window window)
        {
            window.Title = Translate(window.Title);
        }

        if (element is TextBlock textBlock)
        {
            textBlock.Text = Translate(textBlock.Text);
        }

        if (element is ContentControl contentControl && contentControl.Content is string content)
        {
            contentControl.Content = Translate(content);
        }

        if (element is HeaderedContentControl headeredContent && headeredContent.Header is string contentHeader)
        {
            headeredContent.Header = Translate(contentHeader);
        }

        if (element is HeaderedItemsControl headeredItems && headeredItems.Header is string itemsHeader)
        {
            headeredItems.Header = Translate(itemsHeader);
        }

        if (element is ListView { View: GridView gridView })
        {
            foreach (var column in gridView.Columns)
            {
                if (column.Header is string header)
                {
                    column.Header = Translate(header);
                }
            }
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
