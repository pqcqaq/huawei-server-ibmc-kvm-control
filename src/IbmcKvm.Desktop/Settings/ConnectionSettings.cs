using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Desktop.Settings;

internal enum ConnectionTargetKind
{
    Ibmc,
    LinuxAgent,
}

internal sealed record ConnectionSettings(
    string Host,
    string UserName,
    string Password,
    ConnectionMode ConnectionMode,
    bool TrustSelfSignedCertificate,
    bool RememberSettings,
    ConnectionTargetKind TargetKind = ConnectionTargetKind.Ibmc)
{
    public override string ToString() =>
        $"ConnectionSettings {{ Host = {Host}, UserName = {UserName}, Password = <redacted>, " +
        $"ConnectionMode = {ConnectionMode}, TrustSelfSignedCertificate = {TrustSelfSignedCertificate}, " +
        $"RememberSettings = {RememberSettings}, TargetKind = {TargetKind} }}";
}
