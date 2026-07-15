using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using IbmcKvm.App.Settings;
using IbmcKvm.Protocol.Login;
using Microsoft.Win32;
using IbmcKvm.App.Localization;

namespace IbmcKvm.App;

internal partial class CertificateTrustWindow : Window
{
    private readonly CertificateTrustStore store;
    private readonly IbmcEndpoint endpoint;
    private readonly ObservableCollection<TrustRow> rows = [];

    internal CertificateTrustWindow(CertificateTrustStore store, IbmcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(endpoint);
        InitializeComponent();
        this.store = store;
        this.endpoint = endpoint;
        ScopeText.Text = LocalizationManager.Format("当前范围 {0}:{1}", endpoint.Host, endpoint.HttpsPort);
        TrustList.ItemsSource = rows;
        RefreshRows();
    }

    private void RefreshRows()
    {
        rows.Clear();
        foreach (var record in store.Load().OrderBy(record => record.Scope).ThenBy(record => record.Kind))
        {
            rows.Add(new TrustRow(
                record,
                record.Kind == CertificateTrustKind.ServerCertificate
                    ? LocalizationManager.Translate("服务器")
                    : "CA",
                record.Scope,
                record.Subject,
                record.NotAfter.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.CurrentCulture),
                FormatFingerprint(record.Sha256Fingerprint)));
        }
    }

    private void TrustList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        RevokeButton.IsEnabled = TrustList.SelectedItem is TrustRow;

    private void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrustList.SelectedItem is not TrustRow selected ||
            MessageBox.Show(
                this,
                LocalizationManager.Format("撤销 {0} 的 {1} 信任？", selected.Scope, selected.KindLabel),
                LocalizationManager.Translate("撤销证书信任"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        store.Revoke(selected.Record.Id);
        RefreshRows();
    }

    private void ImportAuthorityButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = LocalizationManager.Translate("证书 (*.cer;*.crt;*.der)|*.cer;*.crt;*.der|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length is 0 or > CertificateTrustValidator.MaximumCertificateLength)
            {
                throw new InvalidDataException(LocalizationManager.Translate("证书文件大小无效。"));
            }

            var encoded = File.ReadAllBytes(dialog.FileName);
            using var certificate = X509CertificateLoader.LoadCertificate(encoded);
            var fingerprint = CertificateFingerprint.GetSha256(certificate);
            var confirmation = LocalizationManager.Format(
                "仅为 {0}:{1} 导入此 CA？\n\n主题：{2}\n颁发者：{3}\n有效期：{4:yyyy-MM-dd} 至 {5:yyyy-MM-dd}\n\nSHA-256：\n{6}",
                endpoint.Host,
                endpoint.HttpsPort,
                certificate.Subject,
                certificate.Issuer,
                certificate.NotBefore,
                certificate.NotAfter,
                FormatFingerprint(fingerprint));
            if (MessageBox.Show(
                    this,
                    confirmation,
                    LocalizationManager.Translate("确认导入 CA"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            store.TrustAuthority(endpoint, encoded);
            RefreshRows();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               System.Security.Cryptography.CryptographicException or
                                               InvalidDataException or ArgumentException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                LocalizationManager.Translate("无法导入证书"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string FormatFingerprint(string fingerprint) =>
        string.Join(':', Enumerable.Range(0, fingerprint.Length / 2)
            .Select(index => fingerprint.Substring(index * 2, 2)));

    private sealed record TrustRow(
        CertificateTrustRecord Record,
        string KindLabel,
        string Scope,
        string Subject,
        string ValidUntil,
        string Fingerprint);
}
