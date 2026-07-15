using System.IO;
using System.Windows;
using System.Windows.Threading;
using IbmcKvm.App;

namespace IbmcKvm.DesktopSmoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var outputDirectory = ParseOutputDirectory(args);
            Directory.CreateDirectory(outputDirectory);

            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var application = new IbmcKvm.App.App();
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var run = new DesktopSmokeRunner(application, outputDirectory).RunAsync();
            _ = run.ContinueWith(
                _ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.Run();
            run.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string ParseOutputDirectory(string[] args)
    {
        const string prefix = "--output=";
        var value = args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return Path.GetFullPath(
            value is null
                ? Path.Combine(".artifacts", "desktop-smoke")
                : value[prefix.Length..]);
    }
}
