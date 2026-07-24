using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IbmcKvm.Desktop.Views;

internal sealed partial class MessageDialog : Window
{
    public MessageDialog(string title, string message, bool confirmation = true, bool dangerous = false)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        CancelButton.IsVisible = confirmation;
        ConfirmButton.Content = confirmation ? "确认" : "关闭";
        if (dangerous)
        {
            ConfirmButton.Classes.Remove("primary");
            ConfirmButton.Classes.Add("danger");
        }
    }

    public static Task<bool> ConfirmAsync(Window owner, string title, string message, bool dangerous = false) =>
        new MessageDialog(title, message, confirmation: true, dangerous).ShowDialog<bool>(owner);

    public static Task<bool> ShowAsync(Window owner, string title, string message) =>
        new MessageDialog(title, message, confirmation: false).ShowDialog<bool>(owner);

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => Close(true);
}
