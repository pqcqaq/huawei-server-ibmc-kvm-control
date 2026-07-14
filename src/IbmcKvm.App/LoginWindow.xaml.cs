using System.ComponentModel;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Windows;
using IbmcKvm.App.Settings;
using IbmcKvm.App.Ui;
using IbmcKvm.Core.Session;
using IbmcKvm.Protocol.Login;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.App;

public partial class LoginWindow : Window, IDisposable
{
    private readonly EncryptedSettingsStore settingsStore = new();
    private readonly CancellationTokenSource windowLifetime = new();
    private int disposed;

    public LoginWindow()
    {
        InitializeComponent();
        LoadSavedConnectionSettings();
        ApplyLoginState(LoginPhase.Ready);
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
                throw new InvalidOperationException("请输入用户名和密码。");
            }

            var mode = ModeComboBox.SelectedIndex == 1 ? ConnectionMode.Exclusive : ConnectionMode.Shared;
            var (policy, fingerprint) = await ResolveCertificatePolicyAsync(endpoint, operation.Token);
            SetLoadingStatus("正在验证账号与远程控制权限");
            using var httpClient = IbmcLoginClient.CreateHttpClient(policy, fingerprint);
            var loginClient = new IbmcLoginClient(httpClient, TimeSpan.FromSeconds(20));
            var login = await loginClient.LoginAsync(
                endpoint,
                new LoginRequest(userName, password, mode),
                operation.Token);
            if (!login.IsSuccess)
            {
                throw new InvalidOperationException(GetLoginError(login));
            }

            SetLoadingStatus("正在协商 KVM 视频与输入通道");
            var verificationKey = SessionVerificationKey.Parse(
                login.VerifyValue ?? throw new FormatException("登录响应缺少 KVM 校验值。"));
            var kvmPort = login.KvmPort ?? throw new FormatException("登录响应缺少 KVM 端口。");
            connectedSession = await KvmClientSession.ConnectAsync(
                new KvmConnectionOptions(
                    endpoint.Host,
                    kvmPort,
                    verificationKey.WireValue,
                    Encrypted: login.KvmEncrypted,
                    ExtendedVerifyValue: login.ExtendedVerifyValue,
                    VirtualMediaEncrypted: login.VirtualMediaEncrypted),
                operation.Token);

            var settingsPersisted = PersistConnectionSettings(address, userName, password, mode);
            PasswordInput.Clear();
            var consoleWindow = new MainWindow(
                connectedSession,
                $"{endpoint.Host}:{kvmPort}",
                settingsPersisted);
            connectedSession = null;
            Application.Current.MainWindow = consoleWindow;
            consoleWindow.Show();
            Close();
        }
        catch (OperationCanceledException) when (!windowLifetime.IsCancellationRequested)
        {
            ApplyLoginState(LoginPhase.Failed, "连接已取消或超时，请检查网络后重试。");
        }
        catch (Exception exception) when (!windowLifetime.IsCancellationRequested)
        {
            ApplyLoginState(LoginPhase.Failed, exception.Message);
        }
        finally
        {
            if (connectedSession is not null)
            {
                await connectedSession.DisposeAsync();
            }
        }
    }

    private async Task<(ServerCertificatePolicy Policy, string? Fingerprint)> ResolveCertificatePolicyAsync(
        IbmcEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (TrustCheckBox.IsChecked != true)
        {
            return (ServerCertificatePolicy.Strict, null);
        }

        SetLoadingStatus("正在读取服务器证书");
        var details = await ServerCertificateProbe.ProbeAsync(endpoint, cancellationToken);
        var warning =
            $"服务器证书不受系统信任，是否仅在本次会话中信任？\n\n" +
            $"主题：{details.Subject}\n" +
            $"颁发者：{details.Issuer}\n" +
            $"有效期：{details.NotBefore:yyyy-MM-dd} 至 {details.NotAfter:yyyy-MM-dd}\n" +
            $"验证状态：{FormatPolicyErrors(details.PolicyErrors)}\n\n" +
            $"SHA-256：\n{FormatFingerprint(details.Sha256Fingerprint)}\n\n" +
            "请通过可信渠道核对该指纹。";
        var answer = MessageBox.Show(
            this,
            warning,
            "确认 iBMC 服务器证书",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            throw new OperationCanceledException("用户未信任服务器证书。", cancellationToken);
        }

        return (ServerCertificatePolicy.PinForSession, details.Sha256Fingerprint);
    }

    private void ApplyLoginState(LoginPhase phase, string? detail = null)
    {
        var presentation = LoginPresentation.Resolve(phase, detail);
        ConnectionForm.IsEnabled = presentation.IsFormEnabled;
        LoadingOverlay.Visibility = presentation.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        LoginErrorBorder.Visibility = presentation.IsErrorVisible ? Visibility.Visible : Visibility.Collapsed;
        LoginErrorText.Text = presentation.IsErrorVisible ? presentation.StatusText : string.Empty;
        if (presentation.IsLoading)
        {
            LoadingStatusText.Text = presentation.StatusText;
        }

        if (phase == LoginPhase.Failed)
        {
            PasswordInput.Focus();
        }
    }

    private void SetLoadingStatus(string status) => LoadingStatusText.Text = status;

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
        ApplyLoginState(LoginPhase.Failed, "无法删除本地连接设置，请检查文件权限。");
    }

    private void ClearSavedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!settingsStore.Delete())
        {
            ApplyLoginState(LoginPhase.Failed, "无法删除本地连接设置，请检查文件权限。");
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
        LoginErrorCode.UserLocked => "iBMC 用户已锁定。",
        LoginErrorCode.InsufficientPrivilege => "该用户没有远程控制权限。",
        LoginErrorCode.PasswordExpired => "iBMC 密码已过期。",
        LoginErrorCode.LoginRestricted => "iBMC 当前限制该登录方式。",
        _ => $"iBMC 登录失败，错误码 {response.RawErrorCode}。",
    };

    private static string FormatFingerprint(string fingerprint) =>
        string.Join(':', Enumerable.Range(0, fingerprint.Length / 2)
            .Select(index => fingerprint.Substring(index * 2, 2)));

    private static string FormatPolicyErrors(SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None ? "系统信任" : errors.ToString();
}
