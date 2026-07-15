using System.Windows;
using System.Windows.Controls;
using IbmcKvm.App.Ui;
using IbmcKvm.Core.Input;
using IbmcKvm.App.Localization;

namespace IbmcKvm.App;

public partial class CustomKeyCombinationWindow : Window
{
    private readonly ComboBox[] selectors;

    public CustomKeyCombinationWindow()
    {
        InitializeComponent();
        selectors =
        [
            Key1ComboBox,
            Key2ComboBox,
            Key3ComboBox,
            Key4ComboBox,
            Key5ComboBox,
            Key6ComboBox,
        ];
        foreach (var selector in selectors)
        {
            selector.ItemsSource = KeyboardUiOptions.CustomKeys;
            selector.SelectedIndex = 0;
        }
    }

    public HidKeyCombination? Combination { get; private set; }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var usages = selectors
            .Select(static selector => selector.SelectedValue is byte usage ? usage : (byte)0)
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
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }
}
