using System.Net.Security;
using Avalonia.Controls;
using Avalonia.Interactivity;
using IbmcKvm.Desktop.Localization;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Desktop.Views;

internal enum CertificateDecision
{
    Cancel,
    SessionOnly,
    PersistServer,
}

internal sealed partial class CertificateDecisionWindow : Window
{
    public CertificateDecisionWindow(ServerCertificateDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        InitializeComponent();
        SubjectText.Text = details.Subject;
        IssuerText.Text = details.Issuer;
        ValidityText.Text = $"{details.NotBefore:yyyy-MM-dd HH:mm} - {details.NotAfter:yyyy-MM-dd HH:mm}";
        PolicyText.Text = details.PolicyErrors == SslPolicyErrors.None ? "系统信任" : details.PolicyErrors.ToString();
        FingerprintText.Text = string.Join(':', Enumerable.Range(0, details.Sha256Fingerprint.Length / 2)
            .Select(index => details.Sha256Fingerprint.Substring(index * 2, 2)));
        Opened += (_, _) => LocalizationManager.Apply(this);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(CertificateDecision.Cancel);

    private void TrustSessionButton_Click(object? sender, RoutedEventArgs e) => Close(CertificateDecision.SessionOnly);

    private void TrustServerButton_Click(object? sender, RoutedEventArgs e) => Close(CertificateDecision.PersistServer);
}
