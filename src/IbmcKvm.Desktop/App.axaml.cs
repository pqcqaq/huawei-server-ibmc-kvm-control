using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using IbmcKvm.Core.Agent;
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
            desktop.MainWindow = Program.DirectAgentConnectionOptions is { } directAgent
                ? new DirectAgentConnectionWindow(directAgent)
                : Program.DirectConnectionOptions is { } directKvm
                    ? new DirectConnectionWindow(directKvm)
                    : new LoginWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class DirectAgentConnectionWindow : Window
{
    private readonly DirectAgentConnectionOptions options;

    public DirectAgentConnectionWindow(DirectAgentConnectionOptions options)
    {
        this.options = options;
        Title = "iBMC KVM - connecting to Linux Agent";
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
            var token = await options.ReadPairingTokenAsync();
            var session = await AgentClientSession.ConnectAsync(new(
                options.Host,
                options.Port,
                token,
                options.ServerCertificateFingerprint));
            var window = new AgentConsoleWindow(
                session,
                $"{options.Host}:{options.Port}",
                settingsPersisted: false);
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
