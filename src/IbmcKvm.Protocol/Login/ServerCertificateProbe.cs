using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace IbmcKvm.Protocol.Login;

public sealed record ServerCertificateDetails(
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    string Sha256Fingerprint,
    SslPolicyErrors PolicyErrors,
    ReadOnlyMemory<byte> CertificateDer);

public static class ServerCertificateProbe
{
    public static async Task<ServerCertificateDetails> ProbeAsync(
        IbmcEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.HttpsPort, cancellationToken).ConfigureAwait(false);

        X509Certificate2? captured = null;
        var policyErrors = SslPolicyErrors.None;
        await using var ssl = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, errors) =>
            {
                if (certificate is not null)
                {
                    captured = new X509Certificate2(certificate);
                }

                policyErrors = errors;
                return certificate is not null;
            });

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = endpoint.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, cancellationToken).ConfigureAwait(false);

        using (captured)
        {
            if (captured is null)
            {
                throw new AuthenticationException("The iBMC did not present a server certificate.");
            }

            return new ServerCertificateDetails(
                captured.Subject,
                captured.Issuer,
                captured.NotBefore,
                captured.NotAfter,
                CertificateFingerprint.GetSha256(captured),
                policyErrors,
                captured.Export(X509ContentType.Cert));
        }
    }
}
