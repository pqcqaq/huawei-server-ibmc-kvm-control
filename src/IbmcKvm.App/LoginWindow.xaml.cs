using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Net.Security;
using System.Security.Cryptography;
using System.Windows;
using IbmcKvm.App.Settings;
using IbmcKvm.App.Ui;
using IbmcKvm.App.Localization;
using IbmcKvm.Core.Session;
using IbmcKvm.Protocol.Login;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.App;

public partial class LoginWindow : Window, IDisposable
{
    private readonly EncryptedSettingsStore settingsStore = new();
    private readonly CertificateTrustStore certificateTrustStore = new();
    private readonly UiPreferencesStore uiPreferencesStore = new();
    private bool applyingLanguageSelection;
    private readonly CancellationTokenSource windowLifetime = new();
    private int disposed;

    public LoginWindow()
    {
        InitializeComponent();
        LanguageComboBox.ItemsSource = LocalizationCatalog.SupportedLanguages;
        applyingLanguageSelection = true;
        LanguageComboBox.SelectedValue = LocalizationManager.CurrentCultureName;
        applyingLanguageSelection = false;
        LoadSavedConnectionSettings();
        ApplyLoginState(LoginPhase.Ready);
    }

    private void LanguageComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (applyingLanguageSelection || LanguageComboBox.SelectedValue is not string cultureName)
        {
            return;
        }

        uiPreferencesStore.SaveCulture(cultureName);
        LocalizationManager.SetCulture(cultureName);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyLoginState(LoginPhase.Connecting, "正在建立 HTTPS 会话");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(30));

        KvmClientSession? connectedSession = null;
        try
        {
            var address = AddressTextBox.Text.Trim();
            var endpoint = IbmcEndpoint.Parse(address);
            var userName = UserNameTextBox.Text;
            var password = PasswordInput.Password;
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(LocalizationManager.Translate("请输入用户名和密码。"));
            }

            var mode = ModeComboBox.SelectedIndex == 1 ? ConnectionMode.Exclusive : ConnectionMode.Shared;
            var connected = await ConnectWithDiscoveryAsync(
                endpoint,
                userName,
                password,
                mode,
                operation.Token);
            connectedSession = connected.Session;
            var kvmPort = connected.Port;

            var settingsPersisted = PersistConnectionSettings(address, userName, password, mode);
            PasswordInput.Clear();
            var consoleWindow = new MainWindow(
                connectedSession,
                $"{endpoint.Host}:{kvmPort}",
                settingsPersisted,
                mode == ConnectionMode.Exclusive);
            connectedSession = null;
            Application.Current.MainWindow = consoleWindow;
            consoleWindow.Show();
            Close();
        }
        catch (OperationCanceledException) when (!windowLifetime.IsCancellationRequested)
        {
            ApplyLoginState(LoginPhase.Failed, LocalizationManager.Translate("连接已取消或超时，请检查网络后重试。"));
        }
        catch (Exception exception) when (!windowLifetime.IsCancellationRequested)
        {
            ApplyLoginState(LoginPhase.Failed, LocalizationManager.Translate(exception.Message));
        }
        finally
        {
            if (connectedSession is not null)
            {
                await connectedSession.DisposeAsync();
            }
        }
    }

    private async Task<ConnectedKvm> ConnectWithDiscoveryAsync(
        IbmcEndpoint endpoint,
        string userName,
        string password,
        ConnectionMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            var certificate = await ResolveCertificatePolicyAsync(endpoint, cancellationToken);
            SetLoadingStatus("正在验证账号与远程控制权限");
            using var httpClient = IbmcLoginClient.CreateHttpClient(
                certificate.Policy,
                certificate.Fingerprint,
                certificate.AuthorityCertificates);
            var login = await new IbmcLoginClient(httpClient, TimeSpan.FromSeconds(20)).LoginAsync(
                endpoint,
                new LoginRequest(userName, password, mode),
                cancellationToken);
            if (!login.IsSuccess)
            {
                throw new InvalidOperationException(GetLoginError(login));
            }

            var verificationKey = SessionVerificationKey.Parse(
                login.VerifyValue ?? throw new FormatException(LocalizationManager.Translate("登录响应缺少 KVM 校验值。")));
            var kvmPort = login.KvmPort ?? throw new FormatException(LocalizationManager.Translate("登录响应缺少 KVM 端口。"));
            SetLoadingStatus("正在协商 KVM 视频与输入通道");
            var session = await KvmClientSession.ConnectAsync(
                new KvmConnectionOptions(
                    endpoint.Host,
                    kvmPort,
                    verificationKey.WireValue,
                    Encrypted: login.KvmEncrypted,
                    ExtendedVerifyValue: login.ExtendedVerifyValue,
                    VerificationValue: login.VerifyValue,
                    LoginDecryptionKey: login.DecryptionKey,
                    VirtualMediaEncrypted: login.VirtualMediaEncrypted,
                    Privilege: login.Privilege ?? throw new FormatException(LocalizationManager.Translate("登录响应缺少权限级别。"))),
                cancellationToken);
            return new ConnectedKvm(session, kvmPort);
        }
        catch (Exception exception) when (CanFallbackToRmcp(exception))
        {
            SetLoadingStatus("HTTPS 不可用，正在尝试 RMCP+ 旧固件登录");
            var passwordCharacters = password.ToCharArray();
            try
            {
                var legacy = await new RmcpOemLoginClient(new ManagedRmcpPlusTransport()).LoginAsync(
                    endpoint.Host,
                    endpoint.IpmiPort,
                    userName,
                    passwordCharacters,
                    mode,
                    cancellationToken);
                SetLoadingStatus("正在协商旧固件 KVM 视频与输入通道");
                var session = await KvmClientSession.ConnectAsync(
                    new KvmConnectionOptions(
                        endpoint.Host,
                        legacy.KvmPort,
                        legacy.CodeKey,
                        Encrypted: legacy.KvmEncrypted,
                        VerificationValue: legacy.CodeKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        LoginDecryptionKey: legacy.LoginDecryptionKey,
                        VirtualMediaBladeNumber: 1,
                        VirtualMediaEncrypted: legacy.VirtualMediaEncrypted,
                        Privilege: legacy.Privilege,
                        ProtocolProfile: legacy.Profile,
                        KnownVirtualMediaPort: legacy.VirtualMediaPort),
                    cancellationToken);
                return new ConnectedKvm(session, legacy.KvmPort);
            }
            finally
            {
                passwordCharacters.AsSpan().Clear();
            }
        }
    }

    private static bool CanFallbackToRmcp(Exception exception) => exception is
        HttpRequestException or
        HttpIOException or
        IOException or
        AuthenticationException or
        SocketException or
        TimeoutException or
        FormatException;

    private sealed record ConnectedKvm(KvmClientSession Session, int Port);

    private async Task<ResolvedCertificatePolicy> ResolveCertificatePolicyAsync(
        IbmcEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var stored = certificateTrustStore.Resolve(endpoint);
        if (stored.ServerFingerprint is not null)
        {
            SetLoadingStatus("正在核对已保存的服务器证书");
            var current = await ServerCertificateProbe.ProbeAsync(endpoint, cancellationToken);
            if (string.Equals(
                    CertificateFingerprint.Normalize(stored.ServerFingerprint),
                    CertificateFingerprint.Normalize(current.Sha256Fingerprint),
                    StringComparison.Ordinal))
            {
                return new ResolvedCertificatePolicy(
                    ServerCertificatePolicy.PinForSession,
                    current.Sha256Fingerprint,
                    stored.AuthorityCertificates);
            }

            return ConfirmCertificateDecision(endpoint, current, stored.AuthorityCertificates, cancellationToken);
        }

        if (TrustCheckBox.IsChecked != true)
        {
            return new ResolvedCertificatePolicy(
                ServerCertificatePolicy.Strict,
                null,
                stored.AuthorityCertificates);
        }

        SetLoadingStatus("正在读取服务器证书");
        var details = await ServerCertificateProbe.ProbeAsync(endpoint, cancellationToken);
        return ConfirmCertificateDecision(endpoint, details, stored.AuthorityCertificates, cancellationToken);
    }

    private ResolvedCertificatePolicy ConfirmCertificateDecision(
        IbmcEndpoint endpoint,
        ServerCertificateDetails details,
        System.Collections.Immutable.ImmutableArray<byte[]> authorities,
        CancellationToken cancellationToken)
    {
        var decisionWindow = new CertificateDecisionWindow(details) { Owner = this };
        if (decisionWindow.ShowDialog() != true || decisionWindow.Decision == CertificateDecision.Cancel)
        {
            throw new OperationCanceledException(LocalizationManager.Translate("用户未信任服务器证书。"), cancellationToken);
        }

        if (decisionWindow.Decision == CertificateDecision.PersistServer)
        {
            certificateTrustStore.TrustServer(endpoint, details);
        }

        return new ResolvedCertificatePolicy(
            ServerCertificatePolicy.PinForSession,
            details.Sha256Fingerprint,
            authorities);
    }

    private void TrustManagerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var endpoint = IbmcEndpoint.Parse(AddressTextBox.Text.Trim());
            new CertificateTrustWindow(certificateTrustStore, endpoint) { Owner = this }.ShowDialog();
        }
        catch (FormatException exception)
        {
            ApplyLoginState(LoginPhase.Failed, LocalizationManager.Translate(exception.Message));
        }
    }

    private void HelpMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow { Owner = this };
        help.Show();
    }

    private void ApplyLoginState(LoginPhase phase, string? detail = null)
    {
        var presentation = LoginPresentation.Resolve(phase, detail);
        ConnectionForm.IsEnabled = presentation.IsFormEnabled;
        LoadingOverlay.Visibility = presentation.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        LoginErrorBorder.Visibility = presentation.IsErrorVisible ? Visibility.Visible : Visibility.Collapsed;
        LoginErrorText.Text = presentation.IsErrorVisible
            ? LocalizationManager.Translate(presentation.StatusText)
            : string.Empty;
        if (presentation.IsLoading)
        {
            LoadingStatusText.Text = LocalizationManager.Translate(presentation.StatusText);
        }

        if (phase == LoginPhase.Failed)
        {
            PasswordInput.Focus();
        }
    }

    private void SetLoadingStatus(string status) => LoadingStatusText.Text = LocalizationManager.Translate(status);

    private void LoadSavedConnectionSettings()
    {
        var settings = settingsStore.Load();
        if (settings is null)
        {
            return;
        }

        AddressTextBox.Text = settings.Host;
        UserNameTextBox.Text = settings.UserName;
        PasswordInput.Password = settings.Password;
        ModeComboBox.SelectedIndex = settings.ConnectionMode == ConnectionMode.Exclusive ? 1 : 0;
        TrustCheckBox.IsChecked = settings.TrustSelfSignedCertificate;
        RememberSettingsCheckBox.IsChecked = true;
    }

    private bool PersistConnectionSettings(
        string host,
        string userName,
        string password,
        ConnectionMode connectionMode)
    {
        if (RememberSettingsCheckBox.IsChecked != true)
        {
            return settingsStore.Delete();
        }

        try
        {
            settingsStore.Save(new ConnectionSettings(
                host,
                userName,
                password,
                connectionMode,
                TrustCheckBox.IsChecked == true,
                RememberSettings: true));
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or
                                               UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private void RememberSettingsCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (settingsStore.Delete())
        {
            return;
        }

        RememberSettingsCheckBox.IsChecked = true;
        ApplyLoginState(LoginPhase.Failed, LocalizationManager.Translate("无法删除本地连接设置，请检查文件权限。"));
    }

    private void ClearSavedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!settingsStore.Delete())
        {
            ApplyLoginState(LoginPhase.Failed, LocalizationManager.Translate("无法删除本地连接设置，请检查文件权限。"));
            return;
        }

        RememberSettingsCheckBox.IsChecked = false;
        AddressTextBox.Clear();
        UserNameTextBox.Clear();
        PasswordInput.Clear();
        ModeComboBox.SelectedIndex = 0;
        TrustCheckBox.IsChecked = false;
        ApplyLoginState(LoginPhase.Ready);
        AddressTextBox.Focus();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        windowLifetime.Cancel();
        windowLifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string GetLoginError(IbmcLoginResponse response) => response.Error switch
    {
        LoginErrorCode.UserLocked => LocalizationManager.Translate("iBMC 用户已锁定。"),
        LoginErrorCode.InsufficientPrivilege => LocalizationManager.Translate("该用户没有远程控制权限。"),
        LoginErrorCode.PasswordExpired => LocalizationManager.Translate("iBMC 密码已过期。"),
        LoginErrorCode.LoginRestricted => LocalizationManager.Translate("iBMC 当前限制该登录方式。"),
        _ => LocalizationManager.Format("iBMC 登录失败，错误码 {0}。", response.RawErrorCode),
    };

    private sealed record ResolvedCertificatePolicy(
        ServerCertificatePolicy Policy,
        string? Fingerprint,
        System.Collections.Immutable.ImmutableArray<byte[]> AuthorityCertificates);
}
