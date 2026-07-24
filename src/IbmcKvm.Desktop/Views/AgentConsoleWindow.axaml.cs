using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using IbmcKvm.Core.Agent;
using IbmcKvm.Core.Input;
using IbmcKvm.Desktop.Input;
using IbmcKvm.Desktop.Localization;
using IbmcKvm.Desktop.Ui;

namespace IbmcKvm.Desktop.Views;

public sealed partial class AgentConsoleWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly AgentFrameDecoder decoder = new();
    private readonly DoubleBuffer<WriteableBitmap> frameBuffers = new();
    private readonly HidKeyboardState keyboard = new();
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private AgentClientSession session;
    private Task consumer = Task.CompletedTask;
    private AgentDecodedFrame? latestFrame;
    private byte[]? latestPixels;
    private byte mouseButtons;
    private long lastMouseSend;
    private int renderedFrames;
    private bool keyboardArmed;
    private bool showLocalPointer = true;
    private bool fullscreen;
    private bool closing;

    public AgentConsoleWindow(AgentClientSession connectedSession, string endpointDisplay, bool settingsPersisted)
    {
        session = connectedSession ?? throw new ArgumentNullException(nameof(connectedSession));
        InitializeComponent();
        Title = $"Linux Agent - {endpointDisplay}";
        ConnectedEndpointText.Text = endpointDisplay;
        InputStatusText.Text = settingsPersisted ? "已连接，等待视频" : "已连接，本地设置未保存";
        AddHandler(InputElement.KeyDownEvent, VideoHost_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyUpEvent, VideoHost_KeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        Opened += (_, _) =>
        {
            LocalizationManager.Apply(this);
            keyboardArmed = VideoHost.Focus();
            consumer = ConsumeSessionsAsync(lifetime.Token);
        };
        Activated += (_, _) => keyboardArmed = VideoHost.Focus();
        Deactivated += async (_, _) =>
        {
            keyboardArmed = false;
            await ReleaseRemoteInputAsync();
        };
        Closing += (_, _) =>
        {
            closing = true;
            lifetime.Cancel();
        };
        Closed += Window_Closed;
    }

    private async Task ConsumeSessionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var source = session;
            try
            {
                await foreach (var frame in source.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
                {
                    AgentDecodedFrame decoded;
                    try
                    {
                        decoded = await Task.Run(() => decoder.Decode(frame), cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidDataException)
                    {
                        decoder.Reset();
                        await source.RequestKeyframeAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    await Dispatcher.UIThread.InvokeAsync(() => DisplayFrame(decoded));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetStatus(
                    $"连接中断，正在恢复：{exception.Message}",
                    StatusKind.Warning));
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (!await TryReconnectAsync(source, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<bool> TryReconnectAsync(
        AgentClientSession failed,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 4)), cancellationToken).ConfigureAwait(false);
                var replacement = await failed.ReconnectAsync(cancellationToken).ConfigureAwait(false);
                session = replacement;
                decoder.Reset();
                await failed.DisposeAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    frameBuffers.Reset();
                    RemoteImage.Source = null;
                    ViewerOverlay.IsVisible = true;
                    OverlayText.Text = LocalizationManager.Translate("连接已恢复，等待视频");
                    SetStatus("Linux Agent 连接已恢复", StatusKind.Ready);
                });
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetStatus(
                    $"第 {attempt} 次恢复失败：{exception.Message}",
                    StatusKind.Warning));
            }
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            OverlayText.Text = LocalizationManager.Translate("Linux Agent 连接已断开");
            ViewerOverlay.IsVisible = true;
            SetStatus("无法恢复 Linux Agent 连接", StatusKind.Error);
        });
        return false;
    }

    private void DisplayFrame(AgentDecodedFrame frame)
    {
        var bitmap = frameBuffers.AcquireWritable(
            candidate => candidate.PixelSize.Width == frame.Width && candidate.PixelSize.Height == frame.Height,
            () => new WriteableBitmap(
                new PixelSize(frame.Width, frame.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul));
        using (var locked = bitmap.Lock())
        {
            var stride = checked(frame.Width * 4);
            for (var row = 0; row < frame.Height; row++)
            {
                Marshal.Copy(
                    frame.BgraPixels,
                    row * stride,
                    IntPtr.Add(locked.Address, row * locked.RowBytes),
                    stride);
            }
        }
        RemoteImage.Source = frameBuffers.Publish(bitmap);
        latestFrame = frame;
        latestPixels = frame.BgraPixels;
        ViewerOverlay.IsVisible = false;
        ScreenshotButton.IsEnabled = true;
        SetStatus("Linux Agent 已连接", StatusKind.Ready);
        renderedFrames++;
        if (frameClock.Elapsed.TotalSeconds >= 1)
        {
            VideoMetricsText.Text = $"{frame.Width} × {frame.Height}   {renderedFrames / frameClock.Elapsed.TotalSeconds:0.0} fps";
            renderedFrames = 0;
            frameClock.Restart();
        }
    }

    private async void VideoHost_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!keyboardArmed || latestFrame is null)
        {
            return;
        }
        if (TryGetModifier(e, out var modifier))
        {
            e.Handled = true;
            if (keyboard.SetModifier(modifier, pressed: true))
            {
                await SendKeyboardAsync();
            }
            return;
        }
        if (TryGetUsage(e, out var usage))
        {
            e.Handled = true;
            if (keyboard.Press(usage))
            {
                await SendKeyboardAsync();
            }
        }
    }

    private async void VideoHost_KeyUp(object? sender, KeyEventArgs e)
    {
        if (!keyboardArmed)
        {
            return;
        }
        if (TryGetModifier(e, out var modifier))
        {
            e.Handled = true;
            if (keyboard.SetModifier(modifier, pressed: false))
            {
                await SendKeyboardAsync();
            }
            return;
        }
        if (TryGetUsage(e, out var usage))
        {
            e.Handled = true;
            if (keyboard.Release(usage))
            {
                await SendKeyboardAsync();
            }
        }
    }

    private async Task SendKeyboardAsync() =>
        await RunInputAsync(() => session.SendKeyboardAsync(keyboard.CreateReport(), lifetime.Token));

    private async void VideoHost_LostFocus(object? sender, RoutedEventArgs e)
    {
        keyboardArmed = false;
        await ReleaseRemoteInputAsync();
    }

    private void VideoHost_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (!IsActive)
        {
            Activate();
        }
        keyboardArmed = VideoHost.Focus();
    }

    private async void VideoHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        keyboardArmed = VideoHost.Focus();
        var kind = e.GetCurrentPoint(VideoHost).Properties.PointerUpdateKind;
        mouseButtons |= kind switch
        {
            PointerUpdateKind.LeftButtonPressed => (byte)1,
            PointerUpdateKind.RightButtonPressed => (byte)2,
            PointerUpdateKind.MiddleButtonPressed => (byte)4,
            _ => (byte)0,
        };
        e.Pointer.Capture(VideoHost);
        e.Handled = true;
        await SendPointerAsync(e.GetPosition(VideoHost), 0);
    }

    private async void VideoHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var kind = e.GetCurrentPoint(VideoHost).Properties.PointerUpdateKind;
        mouseButtons &= kind switch
        {
            PointerUpdateKind.LeftButtonReleased => (byte)0xFE,
            PointerUpdateKind.RightButtonReleased => (byte)0xFD,
            PointerUpdateKind.MiddleButtonReleased => (byte)0xFB,
            _ => (byte)0xFF,
        };
        if (mouseButtons == 0)
        {
            e.Pointer.Capture(null);
        }
        e.Handled = true;
        await SendPointerAsync(e.GetPosition(VideoHost), 0);
    }

    private async void VideoHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        var now = Environment.TickCount64;
        if (latestFrame is null || now - Interlocked.Read(ref lastMouseSend) < 8)
        {
            return;
        }
        Interlocked.Exchange(ref lastMouseSend, now);
        await SendPointerAsync(e.GetPosition(VideoHost), 0);
    }

    private async void VideoHost_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (latestFrame is null)
        {
            return;
        }
        e.Handled = true;
        await SendPointerAsync(e.GetPosition(VideoHost), e.Delta.Y > 0 ? (sbyte)1 : (sbyte)-1);
    }

    private async Task SendPointerAsync(Point point, sbyte wheel)
    {
        if (!TryMapPointer(point, out var x, out var y))
        {
            return;
        }
        await RunInputAsync(() => session.SendMouseAsync(mouseButtons, x, y, wheel, lifetime.Token));
    }

    private bool TryMapPointer(Point point, out ushort x, out ushort y)
    {
        x = 0;
        y = 0;
        if (latestFrame is null || VideoHost.Bounds.Width <= 0 || VideoHost.Bounds.Height <= 0)
        {
            return false;
        }
        var scale = Math.Min(VideoHost.Bounds.Width / latestFrame.Width, VideoHost.Bounds.Height / latestFrame.Height);
        var contentWidth = latestFrame.Width * scale;
        var contentHeight = latestFrame.Height * scale;
        var offsetX = (VideoHost.Bounds.Width - contentWidth) / 2;
        var offsetY = (VideoHost.Bounds.Height - contentHeight) / 2;
        if (point.X < offsetX || point.X > offsetX + contentWidth ||
            point.Y < offsetY || point.Y > offsetY + contentHeight)
        {
            return false;
        }
        x = checked((ushort)Math.Round(
            Math.Clamp(point.X - offsetX, 0, contentWidth) * ushort.MaxValue / contentWidth,
            MidpointRounding.AwayFromZero));
        y = checked((ushort)Math.Round(
            Math.Clamp(point.Y - offsetY, 0, contentHeight) * ushort.MaxValue / contentHeight,
            MidpointRounding.AwayFromZero));
        return true;
    }

    private async Task RunInputAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"输入发送失败：{exception.Message}", StatusKind.Error);
        }
    }

    private async Task ReleaseRemoteInputAsync()
    {
        mouseButtons = 0;
        try
        {
            await session.SendKeyboardAsync(keyboard.Clear());
            await session.ReleaseMouseButtonsAsync();
        }
        catch
        {
        }
    }

    private async void ReleaseKeysButton_Click(object? sender, RoutedEventArgs e)
    {
        await ReleaseRemoteInputAsync();
        SetStatus("已释放远端键盘和鼠标", StatusKind.Ready);
    }

    private void ShowLocalPointerButton_Click(object? sender, RoutedEventArgs e)
    {
        showLocalPointer = ShowLocalPointerButton.IsChecked == true;
        VideoHost.Cursor = showLocalPointer
            ? new Cursor(StandardCursorType.Arrow)
            : new Cursor(StandardCursorType.None);
    }

    private async void ScreenshotButton_Click(object? sender, RoutedEventArgs e)
    {
        if (latestFrame is null || latestPixels is null)
        {
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "保存 Linux Agent 截图",
            SuggestedFileName = $"linux-agent-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            FileTypeChoices = [new("PNG 图像") { Patterns = ["*.png"], MimeTypes = ["image/png"] }],
        });
        if (file is null)
        {
            return;
        }
        await using var stream = await file.OpenWriteAsync();
        using var image = new WriteableBitmap(
            new PixelSize(latestFrame.Width, latestFrame.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        using (var locked = image.Lock())
        {
            var stride = checked(latestFrame.Width * 4);
            for (var row = 0; row < latestFrame.Height; row++)
            {
                Marshal.Copy(latestPixels, row * stride, IntPtr.Add(locked.Address, row * locked.RowBytes), stride);
            }
        }
        image.Save(stream);
        SetStatus($"截图已保存：{file.Name}", StatusKind.Ready);
    }

    private void FullscreenButton_Click(object? sender, RoutedEventArgs e)
    {
        fullscreen = !fullscreen;
        WindowState = fullscreen ? WindowState.FullScreen : WindowState.Normal;
    }

    private void DisconnectButton_Click(object? sender, RoutedEventArgs e)
    {
        DisconnectButton.IsEnabled = false;
        var login = new LoginWindow();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = login;
        }
        login.Show();
        Close();
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        if (!closing)
        {
            lifetime.Cancel();
        }
        try
        {
            await consumer;
        }
        catch (OperationCanceledException)
        {
        }
        await session.DisposeAsync();
        frameBuffers.Dispose();
        lifetime.Dispose();
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusMessageText.Text = LocalizationManager.Translate(message);
        InputStatusText.Text = StatusMessageText.Text;
        InputStatusDot.Fill = Brush.Parse(kind switch
        {
            StatusKind.Ready => "#25B979",
            StatusKind.Warning => "#D39B35",
            StatusKind.Error => "#D55A50",
            _ => "#829089",
        });
        StatusMessageBorder.BorderBrush = InputStatusDot.Fill;
    }

    private static bool TryGetModifier(KeyEventArgs e, out HidModifiers modifier) =>
        e.PhysicalKey != PhysicalKey.None
            ? AvaloniaHidKeyMap.TryGetModifier(e.PhysicalKey, out modifier)
            : AvaloniaHidKeyMap.TryGetModifier(e.Key, out modifier);

    private static bool TryGetUsage(KeyEventArgs e, out byte usage) =>
        e.PhysicalKey != PhysicalKey.None
            ? AvaloniaHidKeyMap.TryGetUsage(e.PhysicalKey, out usage)
            : AvaloniaHidKeyMap.TryGetUsage(e.Key, out usage);

    private enum StatusKind
    {
        Neutral,
        Ready,
        Warning,
        Error,
    }
}
