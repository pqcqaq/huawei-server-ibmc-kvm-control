using Avalonia.Controls;
using Avalonia.Interactivity;
using IbmcKvm.Desktop.Localization;

namespace IbmcKvm.Desktop.Views;

internal sealed partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Opened += (_, _) => LocalizationManager.Apply(this);
    }

    private async void AboutButton_Click(object? sender, RoutedEventArgs e) =>
        await new AboutWindow().ShowDialog(this);
}
