using System.Net.Security;
using System.Windows;
using IbmcKvm.Protocol.Login;
using IbmcKvm.App.Localization;

namespace IbmcKvm.App;

internal enum CertificateDecision
{
    Cancel,
    SessionOnly,
    PersistServer,
}

internal partial class CertificateDecisionWindow : Window
{
    internal CertificateDecisionWindow(ServerCertificateDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        InitializeComponent();
        SubjectText.Text = details.Subject;
        IssuerText.Text = details.Issuer;
        ValidityText.Text = $"{details.NotBefore:yyyy-MM-dd HH:mm} - {details.NotAfter:yyyy-MM-dd HH:mm}";
        PolicyText.Text = details.PolicyErrors == SslPolicyErrors.None
            ? LocalizationManager.Translate("系统信任")
            : details.PolicyErrors.ToString();
        FingerprintText.Text = string.Join(':', Enumerable.Range(0, details.Sha256Fingerprint.Length / 2)
            .Select(index => details.Sha256Fingerprint.Substring(index * 2, 2)));
    }

    internal CertificateDecision Decision { get; private set; }

    private void TrustSessionButton_Click(object sender, RoutedEventArgs e)
    {
        Decision = CertificateDecision.SessionOnly;
        DialogResult = true;
    }

    private void TrustServerButton_Click(object sender, RoutedEventArgs e)
    {
        Decision = CertificateDecision.PersistServer;
        DialogResult = true;
    }
}
