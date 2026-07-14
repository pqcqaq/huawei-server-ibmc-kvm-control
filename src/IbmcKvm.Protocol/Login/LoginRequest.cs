namespace IbmcKvm.Protocol.Login;

public enum ConnectionMode
{
    Shared = 0,
    Exclusive = 1,
}

public sealed record LoginRequest(string UserName, string Password, ConnectionMode Mode);

