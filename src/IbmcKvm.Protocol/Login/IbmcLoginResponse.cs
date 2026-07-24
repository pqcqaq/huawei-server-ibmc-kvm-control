namespace IbmcKvm.Protocol.Login;

public enum LoginErrorCode
{
    None = 0,
    InvalidCredentials,
    UserLocked,
    InsufficientPrivilege,
    PasswordExpired,
    LoginRestricted,
    Unknown,
}

public sealed record IbmcLoginResponse
{
    public required int RawErrorCode { get; init; }

    public required LoginErrorCode Error { get; init; }

    public bool IsSuccess => Error == LoginErrorCode.None;

    public string? VerifyValue { get; init; }

    public string? DecryptionKey { get; init; }

    public int? Privilege { get; init; }

    public bool KvmEncrypted { get; init; }

    public bool VirtualMediaEncrypted { get; init; }

    public int? KvmPort { get; init; }

    public int? VirtualMediaPort { get; init; }

    public string? SerialNumber { get; init; }

    public string? ExtendedVerifyValue { get; init; }
}
