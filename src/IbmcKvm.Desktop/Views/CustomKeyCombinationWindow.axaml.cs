using Avalonia.Controls;
using Avalonia.Interactivity;
using IbmcKvm.Core.Input;
using IbmcKvm.Desktop.Localization;
using IbmcKvm.Desktop.Ui;

namespace IbmcKvm.Desktop.Views;

internal sealed partial class CustomKeyCombinationWindow : Window
{
    private readonly ComboBox[] selectors;

    public CustomKeyCombinationWindow()
    {
        InitializeComponent();
        selectors = [Key1ComboBox, Key2ComboBox, Key3ComboBox, Key4ComboBox, Key5ComboBox, Key6ComboBox];
        foreach (var selector in selectors)
        {
            selector.ItemsSource = KeyboardUiOptions.CustomKeys;
            selector.SelectedIndex = 0;
        }

        Opened += (_, _) => LocalizationManager.Apply(this);
    }

    public HidKeyCombination? Combination { get; private set; }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        var usages = selectors
            .Select(static selector => selector.SelectedItem is HidUsageOption option ? option.Usage : (byte)0)
            .Where(static usage => usage != 0)
            .ToArray();
        if (usages.Length == 0)
        {
            ValidationText.Text = LocalizationManager.Translate("至少选择一个按键。");
            return;
        }

        try
        {
            Combination = HidKeyCombination.Create(usages);
            Close(true);
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }
}
