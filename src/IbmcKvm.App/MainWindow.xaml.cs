using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IbmcKvm.App.Input;
using IbmcKvm.App.Ui;
using IbmcKvm.Core.Input;
using IbmcKvm.Core.Session;
using IbmcKvm.Core.Video;
using IbmcKvm.Core.VirtualMedia;
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
    private readonly FloatingToolbarState toolbarState = new();
    private readonly DispatcherTimer toolbarHideTimer;
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
    private int disconnectStarted;
    private RemoteInputState remoteInputState = RemoteInputState.Disconnected;
    private WindowStyle previousWindowStyle;
    private WindowState previousWindowState;
    private ResizeMode previousResizeMode;

    public MainWindow(KvmClientSession connectedSession, string endpointDisplay, bool settingsPersisted)
    {
        ArgumentNullException.ThrowIfNull(connectedSession);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointDisplay);
        InitializeComponent();
        session = connectedSession;
        virtualMediaController = new VirtualMediaController(connectedSession);
        sessionLifetime = new CancellationTokenSource();
        frameConsumer = ConsumeFramesAsync(connectedSession, sessionLifetime.Token);
        diagnosticsConsumer = ConsumeDiagnosticsAsync(connectedSession, sessionLifetime.Token);
        toolbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        toolbarHideTimer.Tick += ToolbarHideTimer_Tick;
        toolbarState.SetPinned(isPinned: true);
        ApplyToolbarState();
        ConnectedEndpointText.Text = endpointDisplay;
        SetStatus(
            settingsPersisted ? $"已连接 {endpointDisplay}" : $"已连接 {endpointDisplay}，本地设置未能更新",
            settingsPersisted ? InputReadyBrush : Brushes.Goldenrod);
        UpdateRemoteInputStatus();
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
                    await Dispatcher.InvokeAsync(() => SetStatus($"视频解码：{exception.Message}", Brushes.Goldenrod));
                    continue;
                }
                await Dispatcher.InvokeAsync(() => DisplayFrame(frame, pixels));
            }

            if (!cancellationToken.IsCancellationRequested && sourceSession.Failure is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    connectionFailed = true;
                    SetStatus(sourceSession.Failure.Message, InputFailedBrush);
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
                        SetStatus($"视频协议：{diagnostics.LastFrameError}", Brushes.Goldenrod);
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

    private async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref disconnectStarted, 1) != 0)
        {
            return;
        }

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
        latestFrame = null;
        blockDecoder = null;
        ViewerOverlay.Visibility = Visibility.Visible;
        VideoMetricsText.Text = "无视频信号";
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
        if (session is not null)
        {
            if (!IsActive)
            {
                Activate();
            }

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

        if (!IsActive)
        {
            Activate();
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
            SetStatus("电源命令已发送", InputReadyBrush);
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
        SetStatus($"截图已保存：{Path.GetFileName(dialog.FileName)}", InputReadyBrush);
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
        if (session is not null)
        {
            VideoHost.Focus();
        }

        UpdateRemoteInputStatus();
    }

    private void Window_Deactivated(object? sender, EventArgs e) =>
        UpdateRemoteInputStatus();

    private void Window_MouseLeave(object sender, MouseEventArgs e) =>
        ScheduleToolbarHide();

    private async void Window_Closed(object? sender, EventArgs e)
    {
        try
        {
            await DisconnectAsync();
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        toolbarHideTimer.Stop();
        sessionLifetime?.Cancel();
        sessionLifetime?.Dispose();
        sessionLifetime = null;
        virtualMediaWindow?.Close();
        virtualMediaWindow = null;
        GC.SuppressFinalize(this);
    }

    private void ConsoleRoot_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.GetPosition(ConsoleRoot).Y <= 72)
        {
            toolbarState.Reveal();
            ApplyToolbarState();
            toolbarHideTimer.Stop();
        }
    }

    private void ToolbarRevealZone_MouseEnter(object sender, MouseEventArgs e)
    {
        toolbarState.Reveal();
        ApplyToolbarState();
        toolbarHideTimer.Stop();
    }

    private void FloatingToolbar_MouseEnter(object sender, MouseEventArgs e) =>
        toolbarHideTimer.Stop();

    private void FloatingToolbar_MouseLeave(object sender, MouseEventArgs e) =>
        ScheduleToolbarHide();

    private void PinToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        toolbarState.SetPinned(PinToolbarButton.IsChecked == true);
        ApplyToolbarState();
        if (!toolbarState.IsPinned)
        {
            ScheduleToolbarHide();
        }
    }

    private void ScheduleToolbarHide()
    {
        if (toolbarState.IsPinned)
        {
            return;
        }

        toolbarHideTimer.Stop();
        toolbarHideTimer.Start();
    }

    private void ToolbarHideTimer_Tick(object? sender, EventArgs e)
    {
        toolbarHideTimer.Stop();
        toolbarState.HideAfterPointerLeaves(FloatingToolbar.IsMouseOver);
        ApplyToolbarState();
    }

    private void ApplyToolbarState()
    {
        FloatingToolbar.Visibility = toolbarState.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        PinToolbarButton.IsChecked = toolbarState.IsPinned;
        PinToolbarButton.ToolTip = toolbarState.IsPinned
            ? "取消固定，鼠标离开后自动隐藏"
            : "固定工具栏，始终显示";
    }

    private void PowerMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        DisconnectButton.IsEnabled = false;
        SetStatus("正在关闭 KVM 连接…", Brushes.Goldenrod);
        await DisconnectAsync();
        var loginWindow = new LoginWindow();
        Application.Current.MainWindow = loginWindow;
        loginWindow.Show();
        Close();
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
        SetStatus($"{header}：{exception.Message}", InputFailedBrush);
        UpdateRemoteInputStatus();
    }

    private void SetStatus(string message, Brush accent)
    {
        StatusMessageText.Text = message;
        StatusMessageBorder.BorderBrush = accent;
        StatusMessageText.Foreground = accent == InputFailedBrush ? ColorBrush("#FFB8B0") : Brushes.White;
        StatusMessageBorder.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush ColorBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
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

}
