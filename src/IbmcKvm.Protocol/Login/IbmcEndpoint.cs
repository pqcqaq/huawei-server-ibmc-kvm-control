using System.Globalization;
using System.Net;

namespace IbmcKvm.Protocol.Login;

public sealed record IbmcEndpoint
{
    public const int DefaultHttpsPort = 443;
    public const int DefaultIpmiPort = 623;

    private const string LoginPath = "/bmc/php/processparameter.php";

    public IbmcEndpoint(string host, int httpsPort, int ipmiPort = DefaultIpmiPort)
    {
        ValidateHost(host);
        ValidatePort(httpsPort, nameof(httpsPort));
        ValidatePort(ipmiPort, nameof(ipmiPort));

        Host = host;
        HttpsPort = httpsPort;
        IpmiPort = ipmiPort;
    }

    public string Host { get; }

    public int HttpsPort { get; }

    public int IpmiPort { get; }

    public Uri LoginUri => new UriBuilder(Uri.UriSchemeHttps, Host, HttpsPort, LoginPath).Uri;

    public static IbmcEndpoint Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new FormatException("The iBMC address is required.");
        }

        var value = input.Trim();
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseHttpsUri(value);
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            throw new FormatException("Only HTTPS iBMC addresses are supported.");
        }

        if (value[0] == '[')
        {
            return ParseBracketedIpv6(value);
        }

        var colonCount = value.Count(static character => character == ':');
        if (colonCount > 1)
        {
            if (!IPAddress.TryParse(value, out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                throw new FormatException("Invalid IPv6 iBMC address.");
            }

            return new IbmcEndpoint(value, DefaultHttpsPort);
        }

        if (colonCount == 1)
        {
            var separator = value.LastIndexOf(':');
            var host = value[..separator];
            var port = ParsePort(value[(separator + 1)..]);
            return new IbmcEndpoint(host, port);
        }

        return new IbmcEndpoint(value, DefaultHttpsPort);
    }

    private static IbmcEndpoint ParseHttpsUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/"))
        {
            throw new FormatException("Invalid HTTPS iBMC address.");
        }

        return new IbmcEndpoint(uri.Host, uri.IsDefaultPort ? DefaultHttpsPort : uri.Port);
    }

    private static IbmcEndpoint ParseBracketedIpv6(string value)
    {
        var closingBracket = value.IndexOf(']');
        if (closingBracket < 0)
        {
            throw new FormatException("The IPv6 address is missing a closing bracket.");
        }

        var host = value[1..closingBracket];
        if (!IPAddress.TryParse(host, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new FormatException("Invalid bracketed IPv6 iBMC address.");
        }

        var suffix = value[(closingBracket + 1)..];
        if (suffix.Length == 0)
        {
            return new IbmcEndpoint(host, DefaultHttpsPort);
        }

        if (suffix[0] != ':' || suffix.Length == 1)
        {
            throw new FormatException("Invalid bracketed IPv6 port.");
        }

        return new IbmcEndpoint(host, ParsePort(suffix[1..]));
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            throw new FormatException("Invalid iBMC port.");
        }

        if (port is < 1 or > 65535)
        {
            throw new FormatException("The iBMC port must be between 1 and 65535.");
        }

        return port;
    }

    private static void ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            (IPAddress.TryParse(host, out _) is false && Uri.CheckHostName(host) == UriHostNameType.Unknown))
        {
            throw new FormatException("Invalid iBMC host name or IP address.");
        }
    }

    private static void ValidatePort(int port, string parameterName)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(parameterName, port, "Ports must be between 1 and 65535.");
        }
    }
}
