using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using IbmcKvm.Desktop.Localization;

namespace IbmcKvm.Desktop.Views;

internal sealed partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "unknown"}";
        Opened += (_, _) => LocalizationManager.Apply(this);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
