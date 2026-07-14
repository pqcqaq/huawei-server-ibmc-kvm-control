using IbmcKvm.Protocol.Login;

namespace IbmcKvm.App.Settings;

internal sealed record ConnectionSettings(
    string Host,
    string UserName,
    string Password,
    ConnectionMode ConnectionMode,
    bool TrustSelfSignedCertificate,
    bool RememberSettings)
{
    public override string ToString() =>
        $"ConnectionSettings {{ Host = {Host}, UserName = {UserName}, Password = <redacted>, " +
        $"ConnectionMode = {ConnectionMode}, TrustSelfSignedCertificate = {TrustSelfSignedCertificate}, " +
        $"RememberSettings = {RememberSettings} }}";
}
