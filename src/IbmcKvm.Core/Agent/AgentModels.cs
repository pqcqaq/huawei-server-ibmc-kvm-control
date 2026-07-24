namespace IbmcKvm.Core.Agent;

public sealed record AgentEndpoint(string Host, int Port)
{
    public const int DefaultPort = 7443;

    public static AgentEndpoint Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("The Linux Agent address is required.");
        }
        var normalized = value.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"agent://{normalized}";
        }
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "agent", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length != 0 ||
            (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/") ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
        {
            throw new FormatException("The Linux Agent address must use HOST:PORT or agent://HOST:PORT.");
        }
        var port = uri.IsDefaultPort ? DefaultPort : uri.Port;
        if (port is < 1 or > ushort.MaxValue)
        {
            throw new FormatException("The Linux Agent port must be between 1 and 65535.");
        }
        var host = uri.Host;
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }
        return new AgentEndpoint(host, port);
    }
}

[Flags]
public enum AgentCapabilities : ushort
{
    None = 0,
    Keyboard = 1 << 0,
    Mouse = 1 << 1,
    AbsoluteMouse = 1 << 2,
}

public sealed record AgentServerHello(
    ushort Width,
    ushort Height,
    byte FramesPerSecond,
    byte TileSize,
    AgentCapabilities Capabilities);

public sealed record AgentTile(
    ushort X,
    ushort Y,
    ushort Width,
    ushort Height,
    byte[] Jpeg);

public sealed record AgentVideoFrame(
    uint Sequence,
    ushort Width,
    ushort Height,
    bool IsKeyframe,
    byte TileSize,
    IReadOnlyList<AgentTile> Tiles);

public sealed record AgentMouseReport(byte Buttons, ushort X, ushort Y, sbyte Wheel);

public sealed record AgentConnectionOptions(
    string Host,
    int Port,
    string PairingToken,
    string ServerCertificateFingerprint);

public enum AgentSessionState
{
    Connecting,
    Connected,
    Streaming,
    Faulted,
    Closed,
}
