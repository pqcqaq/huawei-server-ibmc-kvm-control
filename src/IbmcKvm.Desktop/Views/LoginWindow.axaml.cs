using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using IbmcKvm.Core.Session;
using IbmcKvm.Desktop.Localization;
using IbmcKvm.Desktop.Platform;
using IbmcKvm.Desktop.Settings;
using IbmcKvm.Protocol.Login;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Desktop.Views;

public sealed partial class LoginWindow : Window
{
    private readonly SecretServiceStore secretStore = new();
    private readonly EncryptedSettingsStore settingsStore;
    private readonly CertificateTrustStore certificateTrustStore;
    private readonly UiPreferencesStore uiPreferencesStore = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool applyingLanguageSelection;

    public LoginWindow()
    {
        settingsStore = new EncryptedSettingsStore(secretStore);
        certificateTrustStore = new CertificateTrustStore(secretStore);
        InitializeComponent();
        LanguageComboBox.ItemsSource = LocalizationCatalog.SupportedLanguages;
        applyingLanguageSelection = true;
        LanguageComboBox.SelectedItem = LocalizationCatalog.SupportedLanguages.First(language =>
            string.Equals(
                language.CultureName,
                LocalizationManager.CurrentCultureName,
                StringComparison.OrdinalIgnoreCase));
        applyingLanguageSelection = false;
        Opened += async (_, _) =>
        {
            LocalizationManager.Apply(this);
            await LoadSavedSettingsAsync();
        };
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    private void LanguageComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (applyingLanguageSelection || LanguageComboBox.SelectedItem is not SupportedLanguage language)
        {
            return;
        }

        uiPreferencesStore.SaveCulture(language.CultureName);
        LocalizationManager.SetCulture(language.CultureName);
    }

    private async void ConnectButton_Click(object? sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在建立 HTTPS 会话");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        KvmClientSession? connectedSession = null;
        try
        {
            var address = AddressTextBox.Text?.Trim() ?? string.Empty;
            var endpoint = IbmcEndpoint.Parse(address);
            var userName = UserNameTextBox.Text ?? string.Empty;
            var password = PasswordInput.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(LocalizationManager.Translate("请输入用户名和密码。"));
            }

            var mode = ModeComboBox.SelectedIndex == 1 ? ConnectionMode.Exclusive : ConnectionMode.Shared;
            var connected = await ConnectWithDiscoveryAsync(endpoint, userName, password, mode, operation.Token);
            connectedSession = connected.Session;
            var settingsPersisted = await PersistConnectionSettingsAsync(address, userName, password, mode);
            PasswordInput.Text = string.Empty;

            var consoleWindow = new ConsoleWindow(
                connectedSession,
                $"{endpoint.Host}:{connected.Port}",
                settingsPersisted,
                mode == ConnectionMode.Exclusive);
            connectedSession = null;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = consoleWindow;
            }

            consoleWindow.Show();
            Close();
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            ShowError(LocalizationManager.Translate("连接已取消或超时，请检查网络后重试。"));
        }
        catch (Exception exception) when (!lifetime.IsCancellationRequested)
        {
            ShowError(exception.Message);
        }
        finally
        {
            if (connectedSession is not null)
            {
                await connectedSession.DisposeAsync();
            }

            if (!lifetime.IsCancellationRequested)
            {
                SetBusy(false);
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
            SetBusy(true, "正在验证账号与远程控制权限");
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
                login.VerifyValue ?? throw new FormatException("登录响应缺少 KVM 校验值。"));
            var kvmPort = login.KvmPort ?? throw new FormatException("登录响应缺少 KVM 端口。");
            SetBusy(true, "正在协商 KVM 视频与输入通道");
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
                    Privilege: login.Privilege ?? throw new FormatException("登录响应缺少权限级别。")),
                cancellationToken);
            return new ConnectedKvm(session, kvmPort);
        }
        catch (Exception exception) when (CanFallbackToRmcp(exception))
        {
            SetBusy(true, "HTTPS 不可用，正在尝试 RMCP+ 旧固件登录");
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
                SetBusy(true, "正在协商旧固件 KVM 视频与输入通道");
                var session = await KvmClientSession.ConnectAsync(
                    new KvmConnectionOptions(
                        endpoint.Host,
                        legacy.KvmPort,
                        legacy.CodeKey,
                        Encrypted: legacy.KvmEncrypted,
                        VerificationValue: legacy.CodeKey.ToString(CultureInfo.InvariantCulture),
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

    private async Task<ResolvedCertificatePolicy> ResolveCertificatePolicyAsync(
        IbmcEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var stored = await certificateTrustStore.ResolveAsync(endpoint, cancellationToken: cancellationToken);
        if (stored.ServerFingerprint is not null)
        {
            SetBusy(true, LocalizationManager.Translate("正在核对已保存的服务器证书"));
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

            return await ConfirmCertificateDecisionAsync(
                endpoint,
                current,
                stored.AuthorityCertificates,
                cancellationToken);
        }

        if (TrustCheckBox.IsChecked != true)
        {
            return new ResolvedCertificatePolicy(ServerCertificatePolicy.Strict, null, stored.AuthorityCertificates);
        }

        SetBusy(true, LocalizationManager.Translate("正在读取服务器证书"));
        var details = await ServerCertificateProbe.ProbeAsync(endpoint, cancellationToken);
        return await ConfirmCertificateDecisionAsync(
            endpoint,
            details,
            stored.AuthorityCertificates,
            cancellationToken);
    }

    private async Task<ResolvedCertificatePolicy> ConfirmCertificateDecisionAsync(
        IbmcEndpoint endpoint,
        ServerCertificateDetails details,
        ImmutableArray<byte[]> authorities,
        CancellationToken cancellationToken)
    {
        var decision = await new CertificateDecisionWindow(details).ShowDialog<CertificateDecision>(this);
        if (decision == CertificateDecision.Cancel)
        {
            throw new OperationCanceledException(LocalizationManager.Translate("用户未信任服务器证书。"), cancellationToken);
        }

        if (decision == CertificateDecision.PersistServer)
        {
            await certificateTrustStore.TrustServerAsync(endpoint, details, cancellationToken);
        }

        return new ResolvedCertificatePolicy(
            ServerCertificatePolicy.PinForSession,
            details.Sha256Fingerprint,
            authorities);
    }

    private async void TrustManagerButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var endpoint = IbmcEndpoint.Parse(AddressTextBox.Text?.Trim() ?? string.Empty);
            await new CertificateTrustWindow(certificateTrustStore, endpoint).ShowDialog(this);
        }
        catch (FormatException exception)
        {
            ShowError(LocalizationManager.Translate(exception.Message));
        }
    }

    private void HelpMenuButton_Click(object? sender, RoutedEventArgs e) => new HelpWindow().Show(this);

    private void RevealPasswordButton_Checked(object? sender, RoutedEventArgs e)
    {
        PasswordInput.RevealPassword = true;
        ToolTip.SetTip(RevealPasswordButton, LocalizationManager.Translate("隐藏密码"));
    }

    private void RevealPasswordButton_Unchecked(object? sender, RoutedEventArgs e)
    {
        PasswordInput.RevealPassword = false;
        ToolTip.SetTip(RevealPasswordButton, LocalizationManager.Translate("显示密码"));
    }

    private async Task LoadSavedSettingsAsync()
    {
        try
        {
            var settings = await settingsStore.LoadAsync(lifetime.Token);
            if (settings is null)
            {
                return;
            }

            AddressTextBox.Text = settings.Host;
            UserNameTextBox.Text = settings.UserName;
            PasswordInput.Text = settings.Password;
            ModeComboBox.SelectedIndex = settings.ConnectionMode == ConnectionMode.Exclusive ? 1 : 0;
            TrustCheckBox.IsChecked = settings.TrustSelfSignedCertificate;
            RememberSettingsCheckBox.IsChecked = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"无法读取安全存储：{exception.Message}");
        }
    }

    private async Task<bool> PersistConnectionSettingsAsync(
        string host,
        string userName,
        string password,
        ConnectionMode mode)
    {
        if (RememberSettingsCheckBox.IsChecked != true)
        {
            return settingsStore.Delete();
        }

        try
        {
            await settingsStore.SaveAsync(new ConnectionSettings(
                host,
                userName,
                password,
                mode,
                TrustCheckBox.IsChecked == true,
                RememberSettings: true), lifetime.Token);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"连接成功，但无法安全保存设置：{exception.Message}");
            return false;
        }
    }

    private void ClearSavedSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!settingsStore.Delete())
        {
            ShowError("无法删除本地连接设置，请检查文件权限。");
            return;
        }

        RememberSettingsCheckBox.IsChecked = false;
        AddressTextBox.Text = string.Empty;
        UserNameTextBox.Text = string.Empty;
        PasswordInput.Text = string.Empty;
        ModeComboBox.SelectedIndex = 0;
        TrustCheckBox.IsChecked = false;
        LoginErrorBorder.IsVisible = false;
        AddressTextBox.Focus();
    }

    private void RememberSettingsCheckBox_Unchecked(object? sender, RoutedEventArgs e)
    {
        if (settingsStore.Delete())
        {
            return;
        }

        RememberSettingsCheckBox.IsChecked = true;
        ShowError(LocalizationManager.Translate("无法删除本地连接设置，请检查文件权限。"));
    }

    private void SetBusy(bool busy, string? status = null)
    {
        ConnectionForm.IsEnabled = !busy;
        LoadingOverlay.IsVisible = busy;
        if (status is not null)
        {
            LoadingStatusText.Text = LocalizationManager.Translate(status);
        }

        if (busy)
        {
            LoginErrorBorder.IsVisible = false;
        }
    }

    private void ShowError(string message)
    {
        LoginErrorText.Text = LocalizationManager.Translate(message);
        LoginErrorBorder.IsVisible = true;
        PasswordInput.Focus();
    }

    private static bool CanFallbackToRmcp(Exception exception) => exception is
        HttpRequestException or HttpIOException or IOException or AuthenticationException or SocketException or
        TimeoutException or FormatException;

    private static string GetLoginError(IbmcLoginResponse response) => response.Error switch
    {
        LoginErrorCode.InvalidCredentials => "用户名或密码不正确。请核对大小写和密码末尾字符。",
        LoginErrorCode.UserLocked => "iBMC 用户已锁定。",
        LoginErrorCode.InsufficientPrivilege => "该用户没有远程控制权限。",
        LoginErrorCode.PasswordExpired => "iBMC 密码已过期。",
        LoginErrorCode.LoginRestricted => "iBMC 当前限制该登录方式。",
        _ => $"iBMC 登录失败，错误码 {response.RawErrorCode}。",
    };

    private sealed record ConnectedKvm(KvmClientSession Session, int Port);

    private sealed record ResolvedCertificatePolicy(
        ServerCertificatePolicy Policy,
        string? Fingerprint,
        ImmutableArray<byte[]> AuthorityCertificates);
}
