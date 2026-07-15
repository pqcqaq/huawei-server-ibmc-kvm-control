using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace IbmcKvm.Protocol.Login;

public static class CertificateTrustValidator
{
    public const int MaximumCustomAuthorities = 16;
    public const int MaximumCertificateLength = 64 * 1024;

    public static bool IsTrustedByCustomAuthority(
        X509Certificate certificate,
        X509Chain? presentedChain,
        SslPolicyErrors policyErrors,
        IReadOnlyCollection<byte[]> authorityCertificates)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(authorityCertificates);
        if ((policyErrors & (SslPolicyErrors.RemoteCertificateNameMismatch |
                             SslPolicyErrors.RemoteCertificateNotAvailable)) != 0 ||
            authorityCertificates.Count is 0 or > MaximumCustomAuthorities)
        {
            return false;
        }

        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.DisableCertificateDownloads = true;

        var roots = new List<X509Certificate2>(authorityCertificates.Count);
        try
        {
            foreach (var encoded in authorityCertificates)
            {
                if (encoded.Length is 0 or > MaximumCertificateLength)
                {
                    return false;
                }

                var root = X509CertificateLoader.LoadCertificate(encoded);
                roots.Add(root);
                var constraints = root.Extensions
                    .OfType<X509BasicConstraintsExtension>()
                    .FirstOrDefault();
                if (constraints?.CertificateAuthority != true)
                {
                    return false;
                }

                chain.ChainPolicy.CustomTrustStore.Add(root);
            }

            if (presentedChain is not null)
            {
                foreach (var element in presentedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            return chain.Build(leaf);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (var root in roots)
            {
                root.Dispose();
            }
        }
    }
}
