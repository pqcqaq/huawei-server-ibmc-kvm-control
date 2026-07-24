using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Controls;
using Avalonia.Interactivity;
using IbmcKvm.Desktop.Localization;
using IbmcKvm.Desktop.Settings;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Desktop.Views;

internal sealed partial class CertificateTrustWindow : Window
{
    private readonly CertificateTrustStore store;
    private readonly IbmcEndpoint endpoint;
    private readonly ObservableCollection<TrustRow> rows = [];
    private readonly CancellationTokenSource lifetime = new();

    public CertificateTrustWindow(CertificateTrustStore store, IbmcEndpoint endpoint)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        InitializeComponent();
        ScopeText.Text = LocalizationManager.Format("当前范围 {0}:{1}", endpoint.Host, endpoint.HttpsPort);
        TrustList.ItemsSource = rows;
        Opened += async (_, _) =>
        {
            LocalizationManager.Apply(this);
            await RefreshRowsAsync();
        };
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    private async Task RefreshRowsAsync()
    {
        rows.Clear();
        foreach (var record in (await store.LoadAsync(lifetime.Token))
                     .OrderBy(record => record.Scope)
                     .ThenBy(record => record.Kind))
        {
            rows.Add(new TrustRow(
                record,
                LocalizationManager.Translate(record.Kind == CertificateTrustKind.ServerCertificate ? "服务器" : "CA"),
                record.Scope,
                record.Subject,
                record.NotAfter.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.CurrentCulture),
                FormatFingerprint(record.Sha256Fingerprint)));
        }
    }

    private void TrustList_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        RevokeButton.IsEnabled = TrustList.SelectedItem is TrustRow;

    private async void RevokeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TrustList.SelectedItem is not TrustRow selected ||
            !await MessageDialog.ConfirmAsync(
                this,
                LocalizationManager.Translate("撤销证书信任"),
                LocalizationManager.Format("撤销 {0} 的 {1} 信任？", selected.Scope, selected.KindLabel),
                dangerous: true))
        {
            return;
        }

        await store.RevokeAsync(selected.Record.Id, lifetime.Token);
        await RefreshRowsAsync();
    }

    private async void ImportAuthorityButton_Click(object? sender, RoutedEventArgs e)
    {
        var file = (await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = LocalizationManager.Translate("导入 CA"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new("证书") { Patterns = ["*.cer", "*.crt", "*.der"] },
                Avalonia.Platform.Storage.FilePickerFileTypes.All,
            ],
        })).FirstOrDefault();
        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenReadAsync();
            if (stream.Length is 0 or > CertificateTrustValidator.MaximumCertificateLength)
            {
                throw new InvalidDataException("证书文件大小无效。");
            }

            var encoded = new byte[stream.Length];
            await stream.ReadExactlyAsync(encoded, lifetime.Token);
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
            if (!await MessageDialog.ConfirmAsync(
                    this,
                    LocalizationManager.Translate("确认导入 CA"),
                    confirmation,
                    dangerous: true))
            {
                return;
            }

            await store.TrustAuthorityAsync(endpoint, encoded, lifetime.Token);
            await RefreshRowsAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               System.Security.Cryptography.CryptographicException or
                                               InvalidDataException or ArgumentException)
        {
            await MessageDialog.ShowAsync(this, LocalizationManager.Translate("无法导入证书"), exception.Message);
        }
    }

    private static string FormatFingerprint(string value) =>
        string.Join(':', Enumerable.Range(0, value.Length / 2)
            .Select(index => value.Substring(index * 2, 2)));

    private sealed record TrustRow(
        CertificateTrustRecord Record,
        string KindLabel,
        string Scope,
        string Subject,
        string ValidUntil,
        string Fingerprint);
}
