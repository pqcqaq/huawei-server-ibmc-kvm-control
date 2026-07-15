using System.Configuration;
using System.Data;
using System.Windows;
using IbmcKvm.App.Localization;
using IbmcKvm.App.Settings;

namespace IbmcKvm.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = AppStartupOptions.Parse(e.Args);
        LocalizationManager.SetCulture(options.CultureName ?? new UiPreferencesStore().LoadCulture());
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
                LocalizationManager.Apply((DependencyObject)sender)));
        var loginWindow = new LoginWindow();
        MainWindow = loginWindow;
        loginWindow.Show();
    }
}

