using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using IbmcKvm.Core.Session;
using IbmcKvm.Desktop.Localization;
using IbmcKvm.Desktop.Settings;
using IbmcKvm.Desktop.Views;

namespace IbmcKvm.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        LocalizationManager.SetCulture(new UiPreferencesStore().LoadCulture());
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Program.DirectConnectionOptions is { } direct
                ? new DirectConnectionWindow(direct)
                : new LoginWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class DirectConnectionWindow : Window
{
    private readonly DirectConnectionOptions options;

    public DirectConnectionWindow(DirectConnectionOptions options)
    {
        this.options = options;
        Title = "iBMC KVM - connecting";
        Width = 1;
        Height = 1;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        Opacity = 0;
        Opened += ConnectAsync;
    }

    private async void ConnectAsync(object? sender, EventArgs e)
    {
        try
        {
            var session = await KvmClientSession.ConnectAsync(new(
                options.Host,
                options.Port,
                options.CodeKey));
            var window = new ConsoleWindow(session, $"{options.Host}:{options.Port}", settingsPersisted: true, exclusive: false);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = window;
            }

            window.Show();
            Close();
        }
        catch (Exception exception)
        {
            Content = new TextBlock { Text = exception.ToString() };
            Width = 640;
            Height = 400;
            Opacity = 1;
            SystemDecorations = SystemDecorations.Full;
            ShowInTaskbar = true;
        }
    }
}
