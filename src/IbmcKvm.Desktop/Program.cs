using Avalonia;
using IbmcKvm.Core.Agent;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DirectConnectionOptions = DirectConnectionOptions.Parse(args);
        DirectAgentConnectionOptions = DirectAgentConnectionOptions.Parse(args);
        if (DirectConnectionOptions is not null && DirectAgentConnectionOptions is not null)
        {
            throw new ArgumentException("--direct-kvm and --direct-agent cannot be used together.");
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static DirectConnectionOptions? DirectConnectionOptions { get; private set; }

    internal static DirectAgentConnectionOptions? DirectAgentConnectionOptions { get; private set; }

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

internal sealed record DirectAgentConnectionOptions(
    string Host,
    int Port,
    string TokenFile,
    string ServerCertificateFingerprint)
{
    private const UnixFileMode UnsafeTokenFileModes =
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;

    public static DirectAgentConnectionOptions? Parse(IEnumerable<string> arguments)
    {
        string? endpointValue = null;
        string? tokenFile = null;
        string? fingerprint = null;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--direct-agent=", StringComparison.OrdinalIgnoreCase))
            {
                endpointValue = argument["--direct-agent=".Length..];
            }
            else if (argument.StartsWith("--agent-token-file=", StringComparison.OrdinalIgnoreCase))
            {
                tokenFile = argument["--agent-token-file=".Length..];
            }
            else if (argument.StartsWith("--agent-fingerprint=", StringComparison.OrdinalIgnoreCase))
            {
                fingerprint = argument["--agent-fingerprint=".Length..];
            }
        }

        if (endpointValue is null && tokenFile is null && fingerprint is null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(endpointValue) ||
            string.IsNullOrWhiteSpace(tokenFile) ||
            string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException(
                "--direct-agent requires --agent-token-file and --agent-fingerprint.");
        }

        var endpoint = AgentEndpoint.Parse(endpointValue);
        return new DirectAgentConnectionOptions(
            endpoint.Host,
            endpoint.Port,
            Path.GetFullPath(tokenFile),
            CertificateFingerprint.Normalize(fingerprint));
    }

    public async Task<string> ReadPairingTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(TokenFile) & UnsafeTokenFileModes) != 0)
        {
            throw new UnauthorizedAccessException(
                "The Linux Agent pairing-token file must not be accessible by group or other users.");
        }

        var file = new FileInfo(TokenFile);
        if (file.Length > 4096)
        {
            throw new InvalidDataException("The Linux Agent pairing-token file is unexpectedly large.");
        }
        var token = (await File.ReadAllTextAsync(TokenFile, cancellationToken).ConfigureAwait(false)).Trim();
        if (token.Length == 0)
        {
            throw new InvalidDataException("The Linux Agent pairing-token file is empty.");
        }
        return token;
    }
}
