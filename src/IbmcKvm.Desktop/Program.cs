using Avalonia;

namespace IbmcKvm.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DirectConnectionOptions = DirectConnectionOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static DirectConnectionOptions? DirectConnectionOptions { get; private set; }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}

internal sealed record DirectConnectionOptions(string Host, int Port, int CodeKey)
{
    public static DirectConnectionOptions? Parse(IEnumerable<string> arguments)
    {
        string? value = null;
        var codeKey = 12345678;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--direct-kvm=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument["--direct-kvm=".Length..];
            }
            else if (argument.StartsWith("--code-key=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(argument["--code-key=".Length..], out var parsed))
            {
                codeKey = parsed;
            }
        }

        if (value is null)
        {
            return null;
        }

        var separator = value.LastIndexOf(':');
        if (separator <= 0 ||
            !int.TryParse(value[(separator + 1)..], out var port) ||
            port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentException("--direct-kvm must use HOST:PORT.");
        }

        return new DirectConnectionOptions(value[..separator], port, codeKey);
    }
}
