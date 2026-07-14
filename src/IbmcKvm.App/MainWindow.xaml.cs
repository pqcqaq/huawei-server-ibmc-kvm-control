using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IbmcKvm.App.Input;
using IbmcKvm.App.Settings;
using IbmcKvm.Core.Input;
using IbmcKvm.Core.Session;
using IbmcKvm.Core.Video;
using IbmcKvm.Core.VirtualMedia;
using IbmcKvm.Protocol.Login;
using IbmcKvm.Protocol.Session;
using Microsoft.Win32;

namespace IbmcKvm.App;

public partial class MainWindow : Window, IDisposable
{
    private static readonly Brush InputDisconnectedBrush = CreateInputStateBrush(RemoteInputState.Disconnected);
    private static readonly Brush InputFailedBrush = CreateInputStateBrush(RemoteInputState.ConnectionFailed);
    private static readonly Brush InputInactiveBrush = CreateInputStateBrush(RemoteInputState.ConnectedInactive);
    private static readonly Brush InputReadyBrush = CreateInputStateBrush(RemoteInputState.Ready);
    private readonly HidKeyboardState keyboard = new();
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private readonly EncryptedSettingsStore settingsStore = new();
    private KvmClientSession? session;
    private VirtualMediaController? virtualMediaController;
    private VirtualMediaWindow? virtualMediaWindow;
    private CancellationTokenSource? sessionLifetime;
    private Task? frameConsumer;
    private Task? diagnosticsConsumer;
    private WriteableBitmap? bitmap;
    private EncodedVideoFrame? latestFrame;
    private BlockVideoDecoder? blockDecoder;
    private byte mouseButtons;
    private ushort lastMouseX;
    private ushort lastMouseY;
    private long lastMouseSend;
    private int renderedFrames;
    private bool fullScreen;
    private bool hasLastMousePosition;
    private bool connectionFailed;
    private RemoteInputState remoteInputState = RemoteInputState.Disconnected;
    private WindowStyle previousWindowStyle;
    private WindowState previousWindowState;
    private ResizeMode previousResizeMode;

    public MainWindow()
    {
        InitializeComponent();
        LoadSavedConnectionSettings();
        UpdateRemoteInputStatus();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (session is not null)
        {
            await DisconnectAsync(updateInterface: true);
            return;
        }

        SetConnectionFormEnabled(false);
        connectionFailed = false;
        UpdateRemoteInputStatus();
        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "正在连接…";
        SetStatus("正在建立 HTTPS 会话", "正在连接", Brushes.Goldenrod);

        var operation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var password = string.Empty;
        try
        {
            var endpoint = IbmcEndpoint.Parse(AddressTextBox.Text);
            var userName = UserNameTextBox.Text;
            password = PasswordInput.Password;
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("请输入用户名和密码。");
            }

            var mode = ModeComboBox.SelectedIndex == 1 ? ConnectionMode.Exclusive : ConnectionMode.Shared;
            var (policy, fingerprint) = await ResolveCertificatePolicyAsync(endpoint, operation.Token);
            using var httpClient = IbmcLoginClient.CreateHttpClient(policy, fingerprint);
            var loginClient = new IbmcLoginClient(httpClient, TimeSpan.FromSeconds(20));
            var login = await loginClient.LoginAsync(
                endpoint,
                new LoginRequest(userName, password, mode),
                operation.Token);
            PasswordInput.Clear();

            if (!login.IsSuccess)
            {
                throw new InvalidOperationException(GetLoginError(login));
            }

            var verificationKey = SessionVerificationKey.Parse(
                login.VerifyValue ?? throw new FormatException("登录响应缺少 KVM 校验值。"));
            var kvmPort = login.KvmPort ?? throw new FormatException("登录响应缺少 KVM 端口。");

            sessionLifetime = new CancellationTokenSource();
            session = await KvmClientSession.ConnectAsync(
                new KvmConnectionOptions(
                    endpoint.Host,
                    kvmPort,
                    verificationKey.WireValue,
                    Encrypted: login.KvmEncrypted,
                    ExtendedVerifyValue: login.ExtendedVerifyValue,
                    VirtualMediaEncrypted: login.VirtualMediaEncrypted),
                operation.Token);
            virtualMediaController = new VirtualMediaController(session);
            frameConsumer = ConsumeFramesAsync(session, sessionLifetime.Token);
            diagnosticsConsumer = ConsumeDiagnosticsAsync(session, sessionLifetime.Token);

            var settingsPersisted = PersistConnectionSettings(
                AddressTextBox.Text.Trim(),
                userName,
                password,
                mode);
            password = string.Empty;
            PasswordInput.Clear();

            SessionControlPanel.IsEnabled = true;
            VirtualMediaButton.IsEnabled = true;
            ConnectButton.Content = "断开连接";
            ConnectButton.IsEnabled = true;
            SetConnectionFormEnabled(false);
            var connectionStatus = settingsPersisted
                ? $"已连接 {endpoint.Host}:{kvmPort}"
                : $"已连接 {endpoint.Host}:{kvmPort}，但本地设置未能更新";
            SetStatus(connectionStatus, "已连接", new SolidColorBrush(Color.FromRgb(21, 155, 101)));
            VideoHost.Focus();
            UpdateRemoteInputStatus();
        }
        catch (OperationCanceledException)
        {
            await DisconnectAsync(updateInterface: true);
            connectionFailed = true;
            SetStatus("连接已取消或超时", "连接失败", InputFailedBrush);
            UpdateRemoteInputStatus();
        }
        catch (Exception exception)
        {
            await DisconnectAsync(updateInterface: true);
            connectionFailed = true;
            SetStatus(exception.Message, "连接失败", InputFailedBrush);
            UpdateRemoteInputStatus();
            MessageBox.Show(this, exception.Message, "无法连接 iBMC", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            password = string.Empty;
            operation.Dispose();
            if (session is null)
            {
                PasswordInput.Clear();
                ConnectButton.IsEnabled = true;
                ConnectButton.Content = "连接远程控制台";
                SetConnectionFormEnabled(true);
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

        SetStatus("正在读取服务器证书", "校验证书", Brushes.Goldenrod);
        var details = await ServerCertificateProbe.ProbeAsync(endpoint, cancellationToken);
        var displayFingerprint = FormatFingerprint(details.Sha256Fingerprint);
        var warning =
            $"服务器证书不受系统信任，是否仅在本次会话中信任？\n\n" +
            $"主题：{details.Subject}\n" +
            $"颁发者：{details.Issuer}\n" +
            $"有效期：{details.NotBefore:yyyy-MM-dd} 至 {details.NotAfter:yyyy-MM-dd}\n" +
            $"验证状态：{FormatPolicyErrors(details.PolicyErrors)}\n\n" +
            $"SHA-256：\n{displayFingerprint}\n\n" +
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

    private async Task ConsumeFramesAsync(KvmClientSession sourceSession, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in sourceSession.ReadFramesAsync(cancellationToken))
            {
                blockDecoder ??= new BlockVideoDecoder();
                byte[] pixels;
                try
                {
                    pixels = await Task.Run(() => blockDecoder.Decode(frame), cancellationToken);
                }
                catch (InvalidDataException exception)
                {
                    blockDecoder.Reset();
                    await sourceSession.RequestFullFrameAsync(cancellationToken);
                    await Dispatcher.InvokeAsync(() => FooterStatusText.Text = $"视频解码：{exception.Message}");
                    continue;
                }
                await Dispatcher.InvokeAsync(() => DisplayFrame(frame, pixels));
            }

            if (!cancellationToken.IsCancellationRequested && sourceSession.Failure is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    connectionFailed = true;
                    SetStatus(sourceSession.Failure.Message, "连接中断", InputFailedBrush);
                    UpdateRemoteInputStatus();
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ConsumeDiagnosticsAsync(KvmClientSession sourceSession, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var diagnostics = sourceSession.GetDiagnostics();
                await Dispatcher.InvokeAsync(() =>
                {
                    if (latestFrame is null)
                    {
                        VideoMetricsText.Text =
                            $"Rx {diagnostics.PacketsReceived} · 视频 {diagnostics.VideoPacketsReceived} · " +
                            $"CRC {diagnostics.CrcErrors} · 帧错误 {diagnostics.FrameErrors} · " +
                            $"命令 0x{diagnostics.LastCommand:X2}";
                    }

                    if (!string.IsNullOrEmpty(diagnostics.LastFrameError))
                    {
                        FooterStatusText.Text = $"视频协议：{diagnostics.LastFrameError}";
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void DisplayFrame(EncodedVideoFrame frame, byte[] bgraPixels)
    {
        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            RemoteImage.Source = bitmap;
        }

        bitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            bgraPixels,
            checked(frame.Width * 4),
            0);
        latestFrame = frame;
        ViewerOverlay.Visibility = Visibility.Collapsed;
        ScreenshotButton.IsEnabled = true;
        UpdateRemoteInputStatus();

        renderedFrames++;
        var elapsed = frameClock.Elapsed.TotalSeconds;
        if (elapsed >= 1)
        {
            VideoMetricsText.Text = $"{frame.Width} × {frame.Height}   {renderedFrames / elapsed:0.0} fps";
            renderedFrames = 0;
            frameClock.Restart();
        }
    }

    private async Task DisconnectAsync(bool updateInterface)
    {
        virtualMediaWindow?.Close();
        virtualMediaWindow = null;
        var mediaController = virtualMediaController;
        virtualMediaController = null;
        if (mediaController is not null)
        {
            await mediaController.DisposeAsync();
        }

        var activeSession = session;
        session = null;
        sessionLifetime?.Cancel();
        sessionLifetime?.Dispose();
        sessionLifetime = null;

        if (activeSession is not null)
        {
            await activeSession.DisposeAsync();
        }

        if (frameConsumer is not null)
        {
            try
            {
                await frameConsumer;
            }
            catch (OperationCanceledException)
            {
            }
            frameConsumer = null;
        }

        if (diagnosticsConsumer is not null)
        {
            try
            {
                await diagnosticsConsumer;
            }
            catch (OperationCanceledException)
            {
            }
            diagnosticsConsumer = null;
        }

        keyboard.Clear();
        mouseButtons = 0;
        hasLastMousePosition = false;
        Mouse.Capture(null);
        if (!updateInterface)
        {
            return;
        }

        SessionControlPanel.IsEnabled = false;
        VirtualMediaButton.IsEnabled = false;
        ScreenshotButton.IsEnabled = false;
        latestFrame = null;
        blockDecoder = null;
        connectionFailed = false;
        ConnectButton.Content = "连接远程控制台";
        ConnectButton.IsEnabled = true;
        SetConnectionFormEnabled(true);
        LoadSavedConnectionSettings();
        ViewerOverlay.Visibility = Visibility.Visible;
        VideoMetricsText.Text = "无视频信号";
        SetStatus("已断开", "未连接", Brushes.Gray);
        UpdateRemoteInputStatus();
    }

    private async void VideoHost_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!CanSendRemoteInput)
        {
            return;
        }

        var virtualKey = GetVirtualKey(e);
        var changed = WindowsVirtualKeyMap.TryGetModifier(virtualKey, out var modifier)
            ? keyboard.SetModifier(modifier, pressed: true)
            : WindowsVirtualKeyMap.TryGetUsage(virtualKey, out var usage) && keyboard.Press(usage);
        if (changed)
        {
            e.Handled = true;
            await SendKeyboardSafelyAsync(keyboard.CreateReport());
        }
    }

    private async void VideoHost_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!CanSendRemoteInput)
        {
            return;
        }

        var virtualKey = GetVirtualKey(e);
        var changed = WindowsVirtualKeyMap.TryGetModifier(virtualKey, out var modifier)
            ? keyboard.SetModifier(modifier, pressed: false)
            : WindowsVirtualKeyMap.TryGetUsage(virtualKey, out var usage) && keyboard.Release(usage);
        if (changed)
        {
            e.Handled = true;
            await SendKeyboardSafelyAsync(keyboard.CreateReport());
        }
    }

    private void VideoHost_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdateRemoteInputStatus();

    private void VideoHost_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdateRemoteInputStatus();

    private void VideoHost_MouseEnter(object sender, MouseEventArgs e)
    {
        if (session is not null && IsActive)
        {
            VideoHost.Focus();
        }

        UpdateRemoteInputStatus();
    }

    private void VideoHost_MouseLeave(object sender, MouseEventArgs e) =>
        UpdateRemoteInputStatus();

    private async void VideoHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (session is null)
        {
            return;
        }

        VideoHost.Focus();
        UpdateRemoteInputStatus();
        if (!CanSendRemoteInput)
        {
            return;
        }

        mouseButtons |= e.ChangedButton switch
        {
            MouseButton.Left => (byte)1,
            MouseButton.Right => (byte)2,
            MouseButton.Middle => (byte)4,
            _ => (byte)0,
        };
        Mouse.Capture(VideoHost, CaptureMode.Element);
        e.Handled = true;
        await SendMouseAtCurrentPositionAsync(e.GetPosition(VideoHost), 0);
    }

    private async void VideoHost_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (session is null)
        {
            return;
        }

        mouseButtons &= e.ChangedButton switch
        {
            MouseButton.Left => 0xFE,
            MouseButton.Right => 0xFD,
            MouseButton.Middle => 0xFB,
            _ => 0xFF,
        };
        if (mouseButtons == 0)
        {
            Mouse.Capture(null);
        }
        e.Handled = true;
        var point = e.GetPosition(VideoHost);
        if (TryMapPointer(point, out _, out _))
        {
            await SendMouseAtCurrentPositionAsync(point, 0);
        }
        else
        {
            await SendMouseAtLastPositionAsync();
        }
    }

    private async void VideoHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        UpdateRemoteInputStatus();
        if (!CanSendRemoteInput)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref lastMouseSend) < 12)
        {
            return;
        }

        Interlocked.Exchange(ref lastMouseSend, now);
        await SendMouseAtCurrentPositionAsync(e.GetPosition(VideoHost), 0);
    }

    private async void VideoHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanSendRemoteInput)
        {
            return;
        }

        e.Handled = true;
        await SendMouseAtCurrentPositionAsync(e.GetPosition(VideoHost), e.Delta > 0 ? (sbyte)-1 : (sbyte)1);
    }

    private async Task SendMouseAtCurrentPositionAsync(Point point, sbyte wheel)
    {
        var activeSession = session;
        if (activeSession is null || !TryMapPointer(point, out var x, out var y))
        {
            return;
        }

        try
        {
            lastMouseX = x;
            lastMouseY = y;
            hasLastMousePosition = true;
            await activeSession.SendAbsoluteMouseAsync(mouseButtons, x, y, wheel);
        }
        catch (Exception exception)
        {
            MarkConnectionFailed(exception, "输入发送失败");
        }
    }

    private async Task SendMouseAtLastPositionAsync()
    {
        var activeSession = session;
        if (activeSession is null || !hasLastMousePosition)
        {
            return;
        }

        try
        {
            await activeSession.SendAbsoluteMouseAsync(mouseButtons, lastMouseX, lastMouseY);
        }
        catch (Exception exception)
        {
            MarkConnectionFailed(exception, "输入发送失败");
        }
    }

    private async Task ReleaseRemoteInputAsync()
    {
        var activeSession = session;
        var releaseMouse = mouseButtons != 0 && hasLastMousePosition;
        mouseButtons = 0;
        Mouse.Capture(null);
        var releaseKeyboard = keyboard.Clear();
        if (activeSession is null)
        {
            return;
        }

        try
        {
            await activeSession.SendKeyboardAsync(releaseKeyboard);
            if (releaseMouse)
            {
                await activeSession.SendAbsoluteMouseAsync(0, lastMouseX, lastMouseY);
            }
        }
        catch (Exception exception)
        {
            MarkConnectionFailed(exception, "输入释放失败");
        }
    }

    private bool TryMapPointer(Point point, out ushort x, out ushort y)
    {
        x = 0;
        y = 0;
        if (latestFrame is null || VideoHost.ActualWidth <= 0 || VideoHost.ActualHeight <= 0)
        {
            return false;
        }

        var scale = Math.Min(VideoHost.ActualWidth / latestFrame.Width, VideoHost.ActualHeight / latestFrame.Height);
        var contentWidth = latestFrame.Width * scale;
        var contentHeight = latestFrame.Height * scale;
        var offsetX = (VideoHost.ActualWidth - contentWidth) / 2;
        var offsetY = (VideoHost.ActualHeight - contentHeight) / 2;
        if (point.X < offsetX || point.X > offsetX + contentWidth ||
            point.Y < offsetY || point.Y > offsetY + contentHeight)
        {
            return false;
        }

        x = AbsoluteCoordinateMapper.Map(point.X - offsetX, contentWidth);
        y = AbsoluteCoordinateMapper.Map(point.Y - offsetY, contentHeight);
        return true;
    }

    private async Task SendKeyboardSafelyAsync(byte[] report)
    {
        var activeSession = session;
        if (activeSession is null)
        {
            return;
        }

        try
        {
            await activeSession.SendKeyboardAsync(report);
        }
        catch (Exception exception)
        {
            MarkConnectionFailed(exception, "输入发送失败");
        }
    }

    private async void CtrlAltDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        await SendKeyboardSafelyAsync(new byte[] { 5, 0, 0x4C, 0, 0, 0, 0, 0 });
        await Task.Delay(100);
        await SendKeyboardSafelyAsync(new byte[8]);
    }

    private async void ReleaseKeysButton_Click(object sender, RoutedEventArgs e) =>
        await SendKeyboardSafelyAsync(keyboard.Clear());

    private async void PowerOnButton_Click(object sender, RoutedEventArgs e) =>
        await SendPowerAsync(KvmPowerAction.PowerOn, "确认向服务器发送开机命令？");

    private async void GracefulPowerOffButton_Click(object sender, RoutedEventArgs e) =>
        await SendPowerAsync(KvmPowerAction.GracefulPowerOff, "确认请求操作系统正常关机？");

    private async void RestartButton_Click(object sender, RoutedEventArgs e) =>
        await SendPowerAsync(KvmPowerAction.Restart, "确认立即重启服务器？未保存的数据可能丢失。");

    private async void PowerOffButton_Click(object sender, RoutedEventArgs e) =>
        await SendPowerAsync(KvmPowerAction.PowerOff, "确认立即强制关机？未保存的数据将丢失。");

    private async Task SendPowerAsync(KvmPowerAction action, string confirmation)
    {
        var activeSession = session;
        if (activeSession is null || MessageBox.Show(
            this,
            confirmation,
            "确认电源操作",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await activeSession.SendPowerAsync(action);
            SetStatus("电源命令已发送", HeaderStatusText.Text, StatusDot.Fill);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "电源命令失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (bitmap is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PNG 图像 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"ibmc-kvm-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
        SetStatus($"截图已保存：{Path.GetFileName(dialog.FileName)}", HeaderStatusText.Text, StatusDot.Fill);
    }

    private void VirtualMediaButton_Click(object sender, RoutedEventArgs e)
    {
        if (virtualMediaController is null)
        {
            return;
        }

        if (virtualMediaWindow is { IsLoaded: true })
        {
            virtualMediaWindow.Activate();
            return;
        }

        virtualMediaWindow = new VirtualMediaWindow(virtualMediaController) { Owner = this };
        virtualMediaWindow.Closed += (_, _) => virtualMediaWindow = null;
        virtualMediaWindow.Show();
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!fullScreen)
        {
            previousWindowStyle = WindowStyle;
            previousWindowState = WindowState;
            previousResizeMode = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            fullScreen = true;
        }
        else
        {
            WindowStyle = previousWindowStyle;
            ResizeMode = previousResizeMode;
            WindowState = previousWindowState;
            fullScreen = false;
        }
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        if (session is not null && VideoHost.IsMouseOver)
        {
            VideoHost.Focus();
        }

        UpdateRemoteInputStatus();
    }

    private void Window_Deactivated(object? sender, EventArgs e) =>
        UpdateRemoteInputStatus();

    private async void Window_Closed(object? sender, EventArgs e)
    {
        try
        {
            await DisconnectAsync(updateInterface: false);
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        sessionLifetime?.Cancel();
        sessionLifetime?.Dispose();
        sessionLifetime = null;
        virtualMediaWindow?.Close();
        virtualMediaWindow = null;
        GC.SuppressFinalize(this);
    }

    private void SetConnectionFormEnabled(bool enabled)
    {
        AddressTextBox.IsEnabled = enabled;
        UserNameTextBox.IsEnabled = enabled;
        PasswordInput.IsEnabled = enabled;
        ModeComboBox.IsEnabled = enabled;
        TrustCheckBox.IsEnabled = enabled;
        RememberSettingsCheckBox.IsEnabled = enabled;
        ClearSavedSettingsButton.IsEnabled = enabled;
    }

    private bool CanSendRemoteInput => GetRemoteInputState() == RemoteInputState.Ready;

    private RemoteInputState GetRemoteInputState()
    {
        var pointerOverRemoteFrame = VideoHost.IsMouseOver &&
                                     TryMapPointer(Mouse.GetPosition(VideoHost), out _, out _);
        return RemoteInputAvailability.Resolve(
            isConnected: session is not null,
            connectionFailed,
            hasVideoFrame: latestFrame is not null,
            isPointerInsideViewer: pointerOverRemoteFrame,
            isViewerFocused: VideoHost.IsKeyboardFocusWithin,
            isWindowActive: IsActive);
    }

    private void UpdateRemoteInputStatus()
    {
        var nextState = GetRemoteInputState();
        var shouldRelease = remoteInputState == RemoteInputState.Ready &&
                            nextState != RemoteInputState.Ready &&
                            session is not null;
        remoteInputState = nextState;
        (InputStatusDot.Fill, InputStatusText.Text) = nextState switch
        {
            RemoteInputState.Disconnected => (InputDisconnectedBrush, "未连接"),
            RemoteInputState.ConnectionFailed => (InputFailedBrush, "连接失败"),
            RemoteInputState.Ready => (InputReadyBrush, "输入已启用"),
            _ => (InputInactiveBrush, GetInactiveInputStatus()),
        };

        if (shouldRelease)
        {
            _ = ReleaseRemoteInputAsync();
        }
    }

    private string GetInactiveInputStatus()
    {
        if (latestFrame is null)
        {
            return "已连接，等待视频";
        }

        if (!IsActive)
        {
            return "已连接，窗口未激活";
        }

        if (!VideoHost.IsMouseOver || !TryMapPointer(Mouse.GetPosition(VideoHost), out _, out _))
        {
            return "已连接，鼠标移入远程画面后可输入";
        }

        return "已连接，等待画面焦点";
    }

    private void MarkConnectionFailed(Exception exception, string header)
    {
        connectionFailed = true;
        SetStatus(exception.Message, header, InputFailedBrush);
        UpdateRemoteInputStatus();
    }

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
        if (!settingsStore.Delete())
        {
            RememberSettingsCheckBox.IsChecked = true;
            SetStatus("无法删除本地连接设置，请检查文件权限。", HeaderStatusText.Text, StatusDot.Fill);
        }
    }

    private void ClearSavedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!settingsStore.Delete())
        {
            SetStatus("无法删除本地连接设置，请检查文件权限。", HeaderStatusText.Text, StatusDot.Fill);
            return;
        }

        RememberSettingsCheckBox.IsChecked = false;
        AddressTextBox.Clear();
        UserNameTextBox.Clear();
        PasswordInput.Clear();
        ModeComboBox.SelectedIndex = 0;
        TrustCheckBox.IsChecked = false;
        SetStatus("已清除本地保存的连接设置", HeaderStatusText.Text, StatusDot.Fill);
    }

    private void SetStatus(string footer, string header, Brush dotBrush)
    {
        FooterStatusText.Text = footer;
        HeaderStatusText.Text = header;
        StatusDot.Fill = dotBrush;
    }

    private static int GetVirtualKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return KeyInterop.VirtualKeyFromKey(key);
    }

    private static SolidColorBrush CreateInputStateBrush(RemoteInputState state)
    {
        var brush = new SolidColorBrush(RemoteInputAvailability.GetIndicatorColor(state));
        brush.Freeze();
        return brush;
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
