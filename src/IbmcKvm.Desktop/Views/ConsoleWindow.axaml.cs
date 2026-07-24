using System.Diagnostics;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
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
using IbmcKvm.Core.Input;
using IbmcKvm.Core.Recording;
using IbmcKvm.Core.Session;
using IbmcKvm.Core.Video;
using IbmcKvm.Core.VirtualMedia;
using IbmcKvm.Desktop.Input;
using IbmcKvm.Desktop.Localization;
using IbmcKvm.Desktop.Platform;
using IbmcKvm.Desktop.Ui;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Desktop.Views;

public sealed partial class ConsoleWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly KvmSessionSupervisor supervisor = new();
    private readonly HidKeyboardState keyboard = new();
    private readonly HashSet<int> pressedKeys = [];
    private readonly IPointerController pointerController;
    private readonly Dictionary<byte, BladeConsoleRuntime> bladeRuntimes = [];
    private readonly ObservableCollection<ChassisBladePresentation> chassisItems = [];
    private readonly ObservableCollection<ChassisBladePresentation> bladeTabs = [];
    private readonly ChassisConsoleCoordinator<KvmClientSession> chassisCoordinator;
    private KvmClientSession chassisManagementSession;
    private readonly byte primaryBladeNumber;
    private readonly bool exclusiveChassisQueries;
    private BladeConsoleRuntime? activeRuntime;
    private ChassisSnapshot? chassisSnapshot;
    private KvmClientSession session;
    private VirtualMediaController mediaController = null!;
    private EncodedVideoFrame? latestFrame;
    private byte[]? latestPixels;
    private ConsoleRecorder? repRecorder;
    private AviConsoleRecorder? aviRecorder;
    private VirtualMediaWindow? virtualMediaWindow;
    private byte mouseButtons;
    private Point? lastRelativePoint;
    private long lastMouseSend;
    private bool applyingBladeSelection;
    private PointerMode pointerMode;
    private RemoteKeyboardLayout keyboardLayout = RemoteKeyboardLayout.UnitedStates;
    private bool captureActive;
    private bool showLocalPointer = true;
    private bool toolbarPinned = true;
    private bool pointerOverToolbar;
    private readonly DispatcherTimer toolbarHideTimer;
    private bool closing;
    private bool fullscreen;
    private bool keyboardArmed;

    public ConsoleWindow(
        KvmClientSession connectedSession,
        string endpointDisplay,
        bool settingsPersisted,
        bool exclusive)
    {
        session = connectedSession ?? throw new ArgumentNullException(nameof(connectedSession));
        exclusiveChassisQueries = exclusive;
        chassisManagementSession = connectedSession;
        primaryBladeNumber = connectedSession.BladeNumber;
        var initialState = new ChassisBladeState(
            connectedSession.BladeNumber,
            ChassisBladeStatus.Available,
            0xB0,
            0,
            null,
            null,
            true,
            null,
            true);
        chassisCoordinator = new ChassisConsoleCoordinator<KvmClientSession>(
            initialState,
            connectedSession,
            (state, mode, cancellationToken) =>
                chassisManagementSession.ConnectRelatedBladeAsync(state, mode, cancellationToken));
        pointerController = OperatingSystem.IsLinux()
            ? new X11PointerController()
            : new UnsupportedPointerController("Captured pointer mode is currently implemented for Linux/X11.");
        InitializeComponent();
        ChassisBladeList.ItemsSource = chassisItems;
        BladeTabsList.ItemsSource = bladeTabs;
        toolbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        toolbarHideTimer.Tick += ToolbarHideTimer_Tick;
        AddHandler(InputElement.KeyDownEvent, VideoHost_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyUpEvent, VideoHost_KeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);

        Title = $"iBMC KVM - {endpointDisplay}{(exclusive ? " [独占]" : string.Empty)}";
        ConnectedEndpointText.Text = endpointDisplay;
        InputStatusText.Text = settingsPersisted ? "已连接，等待视频" : "已连接，本地设置未保存";
        StatusMessageText.Text = settingsPersisted ? "已连接" : "已连接，本地设置未保存";
        var initialRuntime = AddBladeRuntime(
            initialState,
            connectedSession,
            KvmBladeSessionMode.Control,
            endpointDisplay);
        SelectRuntime(initialRuntime, updateCoordinator: false);
        supervisor.ProgressChanged += Supervisor_ProgressChanged;
        Opened += async (_, _) =>
        {
            LocalizationManager.Apply(this);
            await RequestRemoteLockKeysAsync(session, lifetime.Token);
            if (session.Capabilities.SupportsChassis)
            {
                _ = RefreshChassisAsync(silent: true);
            }

        };
        Activated += (_, _) =>
        {
            keyboardArmed = VideoHost.Focus();
            UpdateInputAvailability();
        };
        Deactivated += async (_, _) =>
        {
            keyboardArmed = false;
            await ReleaseRemoteInputAsync();
            UpdateInputAvailability();
        };
        Closing += Window_Closing;
        Closed += Window_Closed;
    }

    private async Task ConsumeFramesAsync(BladeConsoleRuntime runtime, CancellationToken cancellationToken)
    {
        var source = runtime.Session;
        try
        {
            await foreach (var frame in source.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    repRecorder?.TryRecord(frame, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                catch (ObjectDisposedException)
                {
                }

                byte[] pixels;
                try
                {
                    pixels = await Task.Run(() => runtime.Decoder.Decode(frame), cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    runtime.Decoder.Reset();
                    await source.RequestFullFrameAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    aviRecorder?.TryRecord(frame, pixels.ToArray());
                }
                catch (ObjectDisposedException)
                {
                }

                await Dispatcher.UIThread.InvokeAsync(() => DisplayFrame(runtime, frame, pixels));
            }

            if (!cancellationToken.IsCancellationRequested && source.Failure is not null)
            {
                await HandleSessionFailureAsync(runtime, source, source.Failure).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await HandleSessionFailureAsync(runtime, source, source.Failure ?? exception).ConfigureAwait(false);
        }
    }

    private async Task ConsumeDiagnosticsAsync(BladeConsoleRuntime runtime, CancellationToken cancellationToken)
    {
        var source = runtime.Session;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var diagnostics = source.GetDiagnostics();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(activeRuntime, runtime))
                    {
                        return;
                    }

                    if (runtime.LatestFrame is null)
                    {
                        VideoMetricsText.Text = LocalizationManager.Format(
                            "Rx {0} · 视频 {1} · CRC {2} · 帧错误 {3} · 命令 0x{4:X2}",
                            diagnostics.PacketsReceived,
                            diagnostics.VideoPacketsReceived,
                            diagnostics.CrcErrors,
                            diagnostics.FrameErrors,
                            diagnostics.LastCommand);
                    }

                    if (diagnostics.FrameErrors > runtime.ReportedFrameErrors)
                    {
                        runtime.ReportedFrameErrors = diagnostics.FrameErrors;
                        SetStatus(
                            LocalizationManager.Format(
                                "视频协议：{0}",
                                diagnostics.LastFrameError ?? "帧序列中断，已请求完整画面"),
                            StatusKind.Warning);
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void DisplayFrame(BladeConsoleRuntime runtime, EncodedVideoFrame frame, byte[] pixels)
    {
        var bitmap = runtime.FrameBuffers.AcquireWritable(
            candidate => candidate.PixelSize.Width == frame.Width && candidate.PixelSize.Height == frame.Height,
            () => new WriteableBitmap(
                new PixelSize(frame.Width, frame.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul));

        using (var locked = bitmap.Lock())
        {
            var sourceStride = checked(frame.Width * 4);
            for (var row = 0; row < frame.Height; row++)
            {
                Marshal.Copy(
                    pixels,
                    row * sourceStride,
                    IntPtr.Add(locked.Address, row * locked.RowBytes),
                sourceStride);
            }
        }

        var publishedBitmap = runtime.FrameBuffers.Publish(bitmap);
        if (runtime.SplitImage is not null)
        {
            runtime.SplitImage.Source = publishedBitmap;
        }

        runtime.LatestFrame = frame;
        runtime.LatestPixels = pixels;
        if (!ReferenceEquals(activeRuntime, runtime))
        {
            return;
        }

        latestFrame = runtime.LatestFrame;
        latestPixels = runtime.LatestPixels;
        RemoteImage.Source = publishedBitmap;
        ViewerOverlay.IsVisible = false;
        ScreenshotButton.IsEnabled = true;
        UpdateInputAvailability();
        runtime.RenderedFrames++;
        if (runtime.FrameClock.Elapsed.TotalSeconds >= 1)
        {
            VideoMetricsText.Text = $"{frame.Width} × {frame.Height}   {runtime.RenderedFrames / runtime.FrameClock.Elapsed.TotalSeconds:0.0} fps";
            runtime.RenderedFrames = 0;
            runtime.FrameClock.Restart();
        }
    }

    private BladeConsoleRuntime AddBladeRuntime(
        ChassisBladeState state,
        KvmClientSession connectedSession,
        KvmBladeSessionMode mode,
        string displayEndpoint)
    {
        var runtime = new BladeConsoleRuntime(
            state,
            connectedSession,
            mode,
            displayEndpoint,
            new VirtualMediaController(connectedSession));
        runtime.Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        bladeRuntimes.Add(state.BladeNumber, runtime);
        AttachSessionEvents(connectedSession);
        runtime.FrameConsumer = ConsumeFramesAsync(runtime, runtime.Lifetime.Token);
        runtime.DiagnosticsConsumer = ConsumeDiagnosticsAsync(runtime, runtime.Lifetime.Token);
        _ = RequestRemoteLockKeysAsync(connectedSession, runtime.Lifetime.Token);
        RefreshBladePresentations();
        return runtime;
    }

    private void SelectRuntime(BladeConsoleRuntime runtime, bool updateCoordinator = true)
    {
        if (updateCoordinator)
        {
            chassisCoordinator.Select(runtime.State.BladeNumber);
        }

        activeRuntime = runtime;
        session = runtime.Session;
        mediaController = runtime.MediaController;
        latestFrame = runtime.LatestFrame;
        latestPixels = runtime.LatestPixels;
        RemoteImage.Source = runtime.Bitmap;
        ViewerOverlay.IsVisible = runtime.LatestFrame is null;
        ScreenshotButton.IsEnabled = runtime.LatestFrame is not null;
        ConnectedEndpointText.Text = runtime.EndpointDisplay;
        SetMouseModeSelection(
            runtime.Session.CurrentMouseMode == KvmMouseMode.Absolute ? PointerMode.Absolute : PointerMode.Relative);
        ApplyLocalPointerCursor();
        ApplyCapabilities();
        UpdateRemoteLockIndicators(runtime.Session.RemoteLockKeys);
        RefreshBladePresentations();
        UpdateBladeTabSelection();
        UpdateSplitView();
        UpdateInputAvailability();
    }

    private async Task RefreshChassisAsync(bool silent)
    {
        if (!chassisManagementSession.Capabilities.SupportsChassis || lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var snapshot = await chassisManagementSession.RefreshChassisAsync(
                exclusiveChassisQueries,
                TimeSpan.FromSeconds(3),
                lifetime.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                chassisSnapshot = snapshot;
                ChassisButton.IsVisible = true;
                RefreshBladePresentations();
                if (!silent)
                {
                    SetStatus("机箱状态已刷新", StatusKind.Ready);
                }
            });
        }
        catch (Exception exception) when (silent && exception is TimeoutException or InvalidDataException or
                                               NotSupportedException or IOException)
        {
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
                SetStatus(LocalizationManager.Format("机箱刷新失败：{0}", exception.Message), StatusKind.Error));
        }
    }

    private void RefreshBladePresentations()
    {
        var connected = chassisCoordinator.Sessions.Select(slot => slot.BladeNumber).ToArray();
        var states = chassisSnapshot?.Blades ??
                     bladeRuntimes.Values
                         .Select(runtime => runtime.State)
                         .OrderBy(state => state.BladeNumber)
                         .ToImmutableArray();
        chassisItems.Clear();
        foreach (var state in states)
        {
            chassisItems.Add(ChassisUiState.Resolve(state, connected));
        }

        bladeTabs.Clear();
        foreach (var runtime in bladeRuntimes.Values.OrderBy(runtime => runtime.State.BladeNumber))
        {
            bladeTabs.Add(ChassisUiState.Resolve(runtime.State, connected));
        }

        var showTabs = bladeTabs.Count > 1;
        BladeTabsBar.IsVisible = showTabs;
        SplitViewButton.IsVisible = showTabs;
        DisconnectBladeButton.IsEnabled = activeRuntime is not null &&
                                          activeRuntime.State.BladeNumber != primaryBladeNumber;
        UpdateBladeTabSelection();
    }

    private void UpdateBladeTabSelection()
    {
        applyingBladeSelection = true;
        try
        {
            BladeTabsList.SelectedItem = activeRuntime is null
                ? null
                : bladeTabs.FirstOrDefault(item => item.BladeNumber == activeRuntime.State.BladeNumber);
        }
        finally
        {
            applyingBladeSelection = false;
        }
    }

    private async void BladeTabsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (applyingBladeSelection || BladeTabsList.SelectedItem is not ChassisBladePresentation selected ||
            !bladeRuntimes.TryGetValue(selected.BladeNumber, out var runtime) ||
            ReferenceEquals(runtime, activeRuntime))
        {
            return;
        }

        await StopRecordingAsync();
        await ReleaseRemoteInputAsync();
        virtualMediaWindow?.Close();
        virtualMediaWindow = null;
        SelectRuntime(runtime);
        SetStatus(LocalizationManager.Format("已选择刀片 {0}", runtime.State.BladeNumber), StatusKind.Ready);
    }

    private void ChassisBladeList_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateSelectedBladeActions();

    private void UpdateSelectedBladeActions()
    {
        var selected = ChassisBladeList.SelectedItem as ChassisBladePresentation;
        ConnectBladeButton.IsEnabled = selected?.CanConnect == true;
        MonitorBladeButton.IsEnabled = selected?.CanMonitor == true;
    }

    private void ChassisButton_Click(object? sender, RoutedEventArgs e) =>
        ChassisPanel.IsVisible = !ChassisPanel.IsVisible;

    private async void RefreshChassisButton_Click(object? sender, RoutedEventArgs e) =>
        await RefreshChassisAsync(silent: false);

    private async void ConnectBladeButton_Click(object? sender, RoutedEventArgs e) =>
        await ConnectSelectedBladeAsync(KvmBladeSessionMode.Control);

    private async void MonitorBladeButton_Click(object? sender, RoutedEventArgs e) =>
        await ConnectSelectedBladeAsync(KvmBladeSessionMode.Monitor);

    private async Task ConnectSelectedBladeAsync(KvmBladeSessionMode mode)
    {
        if (ChassisBladeList.SelectedItem is not ChassisBladePresentation selected || chassisSnapshot is null)
        {
            return;
        }

        var state = chassisSnapshot[selected.BladeNumber];
        ConnectBladeButton.IsEnabled = false;
        MonitorBladeButton.IsEnabled = false;
        SetStatus(
            mode == KvmBladeSessionMode.Control
                ? LocalizationManager.Format("正在连接刀片 {0}", state.BladeNumber)
                : LocalizationManager.Format("正在监视刀片 {0}", state.BladeNumber),
            StatusKind.Warning);
        try
        {
            var slot = await chassisCoordinator.ConnectAsync(state, mode, lifetime.Token);
            if (!bladeRuntimes.TryGetValue(state.BladeNumber, out var runtime))
            {
                runtime = AddBladeRuntime(state, slot.Session, mode, FormatBladeEndpoint(state));
            }

            await StopRecordingAsync();
            await ReleaseRemoteInputAsync();
            virtualMediaWindow?.Close();
            virtualMediaWindow = null;
            SelectRuntime(runtime);
            SetStatus(
                mode == KvmBladeSessionMode.Control
                    ? LocalizationManager.Format("刀片 {0} 已连接", state.BladeNumber)
                    : LocalizationManager.Format("刀片 {0} 只读监视已连接", state.BladeNumber),
                StatusKind.Ready);
        }
        catch (Exception exception)
        {
            SetStatus(LocalizationManager.Format("刀片连接失败：{0}", exception.Message), StatusKind.Error);
        }
        finally
        {
            RefreshBladePresentations();
            UpdateSelectedBladeActions();
        }
    }

    private async void DisconnectBladeButton_Click(object? sender, RoutedEventArgs e)
    {
        var runtime = activeRuntime;
        if (runtime is null || runtime.State.BladeNumber == primaryBladeNumber)
        {
            return;
        }

        await StopRecordingAsync();
        await ReleaseRemoteInputAsync();
        virtualMediaWindow?.Close();
        virtualMediaWindow = null;
        runtime.Lifetime.Cancel();
        DetachSessionEvents(runtime.Session);
        await runtime.MediaController.DisposeAsync();
        await chassisCoordinator.DisconnectAsync(runtime.State.BladeNumber, lifetime.Token);
        await runtime.AwaitConsumerAsync();
        bladeRuntimes.Remove(runtime.State.BladeNumber);
        if (chassisCoordinator.SelectedBladeNumber is { } selected &&
            bladeRuntimes.TryGetValue(selected, out var replacement))
        {
            SelectRuntime(replacement, updateCoordinator: false);
        }

        RefreshBladePresentations();
        UpdateSplitView();
        SetStatus(LocalizationManager.Format("刀片 {0} 会话已关闭", runtime.State.BladeNumber), StatusKind.Ready);
    }

    private void SplitViewButton_Click(object? sender, RoutedEventArgs e)
    {
        chassisCoordinator.SetSplitView(SplitViewButton.IsChecked == true);
        UpdateSplitView();
        ApplyCapabilities();
    }

    private void UpdateSplitView()
    {
        var enabled = chassisCoordinator.IsSplitViewEnabled && bladeRuntimes.Count > 1;
        if (!enabled && chassisCoordinator.IsSplitViewEnabled)
        {
            chassisCoordinator.SetSplitView(false);
            SplitViewButton.IsChecked = false;
        }

        SplitVideoGrid.Children.Clear();
        SplitVideoGrid.RowDefinitions.Clear();
        SplitVideoGrid.ColumnDefinitions.Clear();
        if (!enabled)
        {
            SplitVideoGrid.IsVisible = false;
            RemoteImage.IsVisible = true;
            ViewerOverlay.IsVisible = latestFrame is null;
            return;
        }

        SplitVideoGrid.RowDefinitions.Add(new RowDefinition());
        SplitVideoGrid.RowDefinitions.Add(new RowDefinition());
        SplitVideoGrid.ColumnDefinitions.Add(new ColumnDefinition());
        SplitVideoGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var index = 0;
        foreach (var runtime in bladeRuntimes.Values.OrderBy(runtime => runtime.State.BladeNumber))
        {
            var image = new Image { Source = runtime.Bitmap, Stretch = Stretch.Uniform };
            runtime.SplitImage = image;
            var label = new TextBlock
            {
                Text = LocalizationManager.Format(
                    "刀片 {0} · {1}",
                    runtime.State.BladeNumber,
                    LocalizationManager.Translate(runtime.Mode == KvmBladeSessionMode.Monitor ? "监视" : "控制")),
                Foreground = Brushes.White,
                Background = Brush.Parse("#D21A211E"),
                Padding = new Thickness(7, 4),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(8),
                FontSize = 10,
            };
            var cell = new Grid();
            cell.Children.Add(image);
            cell.Children.Add(label);
            var border = new Border
            {
                BorderBrush = runtime == activeRuntime ? Brush.Parse("#25B979") : Brush.Parse("#3E4A45"),
                BorderThickness = new Thickness(runtime == activeRuntime ? 2 : 1),
                Margin = new Thickness(3),
                Child = cell,
            };
            Grid.SetRow(border, index / 2);
            Grid.SetColumn(border, index % 2);
            SplitVideoGrid.Children.Add(border);
            index++;
        }

        RemoteImage.IsVisible = false;
        ViewerOverlay.IsVisible = false;
        SplitVideoGrid.IsVisible = true;
    }

    private static string FormatBladeEndpoint(ChassisBladeState state)
    {
        var host = state.UsesManagementAddress
            ? LocalizationManager.Translate("机箱转发")
            : state.Address?.ToString() ?? LocalizationManager.Translate("未知地址");
        return state.Port is { } port ? $"{host}:{port}" : host;
    }

    private async void VideoHost_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!keyboardArmed)
        {
            return;
        }

        if (pointerMode == PointerMode.Captured && captureActive && e.Key == Key.Escape)
        {
            ReleasePointerCapture();
            SetStatus("已释放捕获鼠标", StatusKind.Ready);
            e.Handled = true;
            return;
        }

        if (!CanSendRemoteInput)
        {
            return;
        }

        if (TryGetModifier(e, out var modifier))
        {
            e.Handled = true;
            if (keyboard.SetModifier(modifier, pressed: true))
            {
                await SendKeyboardAsync(keyboard.CreateReport());
            }

            return;
        }

        if (!TryGetUsage(e, out var usage))
        {
            return;
        }

        var repeated = !pressedKeys.Add(GetKeyIdentity(e));
        e.Handled = true;
        if (repeated && IsLockKey(e))
        {
            return;
        }

        var lockKey = IsLockKey(e);
        await RunInputAsync(() => session.SendKeyPulseAsync(keyboard.Modifiers, usage, cancellationToken: lifetime.Token));
        if (lockKey)
        {
            _ = RefreshRemoteLockKeysAfterInputAsync(session, lifetime.Token);
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
                await SendKeyboardAsync(keyboard.CreateReport());
            }

            return;
        }

        e.Handled = pressedKeys.Remove(GetKeyIdentity(e));
    }

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
        if (keyboardArmed && latestFrame is not null)
        {
            UpdateInputAvailability();
        }
    }

    private async void VideoHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        keyboardArmed = VideoHost.Focus();
        if (!CanSendRemoteInput)
        {
            UpdateInputAvailability();
            return;
        }

        var properties = e.GetCurrentPoint(VideoHost).Properties;
        mouseButtons |= properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => (byte)1,
            PointerUpdateKind.RightButtonPressed => (byte)2,
            PointerUpdateKind.MiddleButtonPressed => (byte)4,
            _ => (byte)0,
        };

        if (pointerMode == PointerMode.Captured)
        {
            if (!pointerController.IsSupported || !pointerController.TryCapture(TryGetPlatformHandle()?.Handle ?? 0))
            {
                SetStatus(pointerController.UnsupportedReason ?? "无法捕获鼠标。", StatusKind.Error);
                return;
            }

            captureActive = true;
            ApplyLocalPointerCursor();
            CenterPointer();
            SetStatus("鼠标已捕获；按 Esc 释放", StatusKind.Ready);
        }
        else
        {
            e.Pointer.Capture(VideoHost);
        }

        e.Handled = true;
        await SendPointerAsync(e.GetPosition(VideoHost), 0);
    }

    private async void VideoHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var properties = e.GetCurrentPoint(VideoHost).Properties;
        mouseButtons &= properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => (byte)0xFE,
            PointerUpdateKind.RightButtonReleased => (byte)0xFD,
            PointerUpdateKind.MiddleButtonReleased => (byte)0xFB,
            _ => (byte)0xFF,
        };
        if (mouseButtons == 0 && !captureActive)
        {
            e.Pointer.Capture(null);
        }

        e.Handled = true;
        await SendPointerAsync(e.GetPosition(VideoHost), 0);
    }

    private async void VideoHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!CanSendRemoteInput)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref lastMouseSend) < 8)
        {
            return;
        }

        Interlocked.Exchange(ref lastMouseSend, now);
        await SendPointerAsync(e.GetPosition(VideoHost), 0);
    }

    private async void VideoHost_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!CanSendRemoteInput)
        {
            return;
        }

        e.Handled = true;
        await SendPointerAsync(e.GetPosition(VideoHost), e.Delta.Y > 0 ? (sbyte)-1 : (sbyte)1);
    }

    private async Task SendPointerAsync(Point point, sbyte wheel)
    {
        if (!CanSendRemoteInput)
        {
            return;
        }

        await RunInputAsync(async () =>
        {
            if (session.CurrentMouseMode == KvmMouseMode.Relative)
            {
                if (captureActive)
                {
                    var center = new Point(VideoHost.Bounds.Width / 2, VideoHost.Bounds.Height / 2);
                    var dx = ToDelta(point.X - center.X);
                    var dy = ToDelta(point.Y - center.Y);
                    if (dx == 0 && dy == 0 && wheel == 0)
                    {
                        return;
                    }

                    await session.SendRelativeMouseAsync(mouseButtons, dx, dy, wheel, lifetime.Token);
                    CenterPointer();
                    return;
                }

                var previous = lastRelativePoint;
                lastRelativePoint = point;
                await session.SendRelativeMouseAsync(
                    mouseButtons,
                    previous is null ? (sbyte)0 : ToDelta(point.X - previous.Value.X),
                    previous is null ? (sbyte)0 : ToDelta(point.Y - previous.Value.Y),
                    wheel,
                    lifetime.Token);
                return;
            }

            if (TryMapPointer(point, out var x, out var y))
            {
                await session.SendAbsoluteMouseAsync(mouseButtons, x, y, wheel, lifetime.Token);
            }
        });
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

        x = AbsoluteCoordinateMapper.Map(point.X - offsetX, contentWidth);
        y = AbsoluteCoordinateMapper.Map(point.Y - offsetY, contentHeight);
        return true;
    }

    private async Task SendKeyboardAsync(byte[] report) =>
        await RunInputAsync(() => session.SendKeyboardAsync(report, lifetime.Token).AsTask());

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
        lastRelativePoint = null;
        ReleasePointerCapture();
        pressedKeys.Clear();
        var report = keyboard.Clear();
        try
        {
            if (session.Permissions.CanControlKvm)
            {
                await session.SendKeyboardAsync(report);
                if (session.CurrentMouseMode == KvmMouseMode.Relative)
                {
                    await session.SendRelativeMouseAsync(0, 0, 0);
                }
            }
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

    private async void SynchronizeMouseButton_Click(object? sender, RoutedEventArgs e) =>
        await RunCommandAsync(() => session.SynchronizeMouseAsync(lifetime.Token), "远端鼠标同步命令已发送");

    private void ContextMenuButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Open(button);
        }
    }

    private async void PresetCombinationMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string preset })
        {
            return;
        }

        var combination = preset switch
        {
            "CtrlShift" => HidKeyCombination.CtrlShift,
            "CtrlEscape" => HidKeyCombination.CtrlEscape,
            "CtrlAltDelete" => HidKeyCombination.CtrlAltDelete,
            "AltTab" => HidKeyCombination.AltTab,
            "CtrlSpace" => HidKeyCombination.CtrlSpace,
            "KeyboardReset" => HidKeyCombination.KeyboardReset,
            _ => null,
        };
        if (combination is not null)
        {
            await SendCombinationAsync(
                combination,
                preset == "KeyboardReset" ? "远端键盘已重置" : "组合键已发送");
        }
    }

    private async void CustomCombinationMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        await ReleaseRemoteInputAsync();
        var dialog = new CustomKeyCombinationWindow();
        if (await dialog.ShowDialog<bool>(this) && dialog.Combination is { } combination)
        {
            await SendCombinationAsync(combination, "自定义组合键已发送");
        }
    }

    private async Task SendCombinationAsync(HidKeyCombination combination, string successMessage)
    {
        await ReleaseRemoteInputAsync();
        await RunCommandAsync(
            () => session.SendKeyCombinationAsync(combination, cancellationToken: lifetime.Token),
            successMessage);
        keyboardArmed = VideoHost.Focus();
    }

    private async void KeyboardLayoutMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string layoutName } ||
            !Enum.TryParse<RemoteKeyboardLayout>(layoutName, out var selectedLayout))
        {
            return;
        }

        await ReleaseRemoteInputAsync();
        keyboardLayout = selectedLayout;
        UsKeyboardMenuItem.IsChecked = selectedLayout == RemoteKeyboardLayout.UnitedStates;
        JapaneseKeyboardMenuItem.IsChecked = selectedLayout == RemoteKeyboardLayout.Japanese;
        FrenchKeyboardMenuItem.IsChecked = selectedLayout == RemoteKeyboardLayout.French;
        var label = KeyboardUiOptions.Layouts.First(option => option.Layout == selectedLayout).Label;
        SetStatus(LocalizationManager.Format("键盘布局已切换为 {0}", label), StatusKind.Ready);
        keyboardArmed = VideoHost.Focus();
    }

    private async void MouseModeMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
        {
            return;
        }

        var requested = tag switch
        {
            "Relative" => PointerMode.Relative,
            "Captured" => PointerMode.Captured,
            _ => PointerMode.Absolute,
        };
        if (requested == PointerMode.Captured && !pointerController.IsSupported)
        {
            SetStatus(pointerController.UnsupportedReason ?? "捕获鼠标不可用。", StatusKind.Error);
            SetMouseModeSelection(pointerMode);
            return;
        }

        var previous = pointerMode;
        try
        {
            await ReleaseRemoteInputAsync();
            var protocolMode = requested == PointerMode.Absolute ? KvmMouseMode.Absolute : KvmMouseMode.Relative;
            if (session.CurrentMouseMode != protocolMode)
            {
                await session.SetMouseModeAsync(protocolMode, lifetime.Token);
            }

            pointerMode = requested;
            lastRelativePoint = null;
            ApplyLocalPointerCursor();
            UpdateConsoleSettingsPresentation();
            SetStatus(
                requested == PointerMode.Captured
                    ? "已切换到捕获鼠标；单击画面捕获，按 Esc 释放"
                    : requested == PointerMode.Relative ? "已切换到相对鼠标" : "已切换到绝对鼠标",
                StatusKind.Ready);
        }
        catch (Exception exception)
        {
            pointerMode = previous;
            SetMouseModeSelection(previous);
            ApplyLocalPointerCursor();
            SetStatus($"鼠标模式切换失败：{exception.Message}", StatusKind.Error);
        }
    }

    private void ShowLocalPointerButton_Click(object? sender, RoutedEventArgs e)
    {
        showLocalPointer = ShowLocalPointerButton.IsChecked == true;
        ToolTip.SetTip(
            ShowLocalPointerButton,
            LocalizationManager.Translate(showLocalPointer ? "隐藏本地鼠标指针" : "显示本地鼠标指针"));
        ApplyLocalPointerCursor();
    }

    private async void VideoQualityMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } ||
            !byte.TryParse(tag, out var value))
        {
            return;
        }

        try
        {
            await session.SetVideoQualityAsync(value, committed: true, lifetime.Token);
            UpdateConsoleSettingsPresentation();
            SetStatus(LocalizationManager.Format("图像清晰度已设为 {0}", value), StatusKind.Ready);
        }
        catch (Exception exception)
        {
            UpdateConsoleSettingsPresentation();
            SetStatus(LocalizationManager.Format("图像清晰度设置失败：{0}", exception.Message), StatusKind.Error);
        }
    }

    private async void ColorDepthMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } ||
            !byte.TryParse(tag, out var value))
        {
            return;
        }

        try
        {
            await session.SetColorDepthAsync(value, lifetime.Token);
            activeRuntime?.Decoder.Reset();
            UpdateConsoleSettingsPresentation();
            var label = value switch { 2 => "8-bit", 1 => "7-bit", 0 => "6-bit", _ => "4-bit" };
            SetStatus(LocalizationManager.Format("颜色位数已设为 {0}", label), StatusKind.Ready);
        }
        catch (Exception exception)
        {
            UpdateConsoleSettingsPresentation();
            SetStatus(LocalizationManager.Format("颜色位数设置失败：{0}", exception.Message), StatusKind.Error);
        }
    }

    private async void ScreenshotButton_Click(object? sender, RoutedEventArgs e)
    {
        if (latestFrame is null || latestPixels is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "保存远程控制台截图",
            SuggestedFileName = $"ibmc-kvm-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            FileTypeChoices =
            [
                new("PNG 图像") { Patterns = ["*.png"], MimeTypes = ["image/png"] },
            ],
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

    private async void RecordButton_Click(object? sender, RoutedEventArgs e)
    {
        if (repRecorder is not null || aviRecorder is not null)
        {
            await StopRecordingAsync();
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "保存控制台录像",
            SuggestedFileName = $"ibmc-{DateTime.Now:yyyyMMdd-HHmmss}.rep",
            FileTypeChoices =
            [
                new("iBMC REP 录像") { Patterns = ["*.rep"] },
                new("Motion JPEG AVI") { Patterns = ["*.avi"], MimeTypes = ["video/x-msvideo"] },
            ],
        });
        if (file is null)
        {
            return;
        }

        var extension = Path.GetExtension(file.Name);
        if (extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) && latestFrame is null)
        {
            await MessageDialog.ShowAsync(this, "无法开始录像", "收到第一帧视频后才能开始 AVI 录像。");
            return;
        }

        try
        {
            var stream = await file.OpenWriteAsync();
            if (extension.Equals(".avi", StringComparison.OrdinalIgnoreCase))
            {
                aviRecorder = new AviConsoleRecorder(stream, latestFrame!.Width, latestFrame.Height);
            }
            else
            {
                repRecorder = new ConsoleRecorder(new RepRecordingWriter(stream));
            }

            await session.StartRecordingAsync(lifetime.Token);
            ToolTip.SetTip(RecordButton, LocalizationManager.Translate("停止本地录像"));
            SetStatus("正在录像", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            await StopRecordingAsync();
            SetStatus($"录像启动失败：{exception.Message}", StatusKind.Error);
        }
    }

    private async Task StopRecordingAsync()
    {
        var rep = Interlocked.Exchange(ref repRecorder, null);
        var avi = Interlocked.Exchange(ref aviRecorder, null);
        if (rep is null && avi is null)
        {
            return;
        }

        try
        {
            await session.StopRecordingAsync();
        }
        catch
        {
        }

        if (rep is not null)
        {
            await rep.DisposeAsync();
        }

        if (avi is not null)
        {
            await avi.DisposeAsync();
        }

        ToolTip.SetTip(RecordButton, LocalizationManager.Translate("开始本地录像"));
        var dropped = (rep?.DroppedFrames ?? 0) + (avi?.DroppedFrames ?? 0);
        SetStatus(dropped == 0 ? "录像已保存" : $"录像已保存，丢弃 {dropped} 帧", StatusKind.Ready);
    }

    private void VirtualMediaButton_Click(object? sender, RoutedEventArgs e)
    {
        if (virtualMediaWindow is { IsVisible: true })
        {
            virtualMediaWindow.Activate();
            return;
        }

        virtualMediaWindow = new VirtualMediaWindow(mediaController);
        virtualMediaWindow.Closed += (_, _) => virtualMediaWindow = null;
        virtualMediaWindow.Show(this);
    }

    private async void PowerMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } menuItem ||
            !Enum.TryParse<KvmPowerAction>(tag, out var action))
        {
            return;
        }

        var label = menuItem.Header?.ToString() ?? action.ToString();
        var confirmation = action switch
        {
            KvmPowerAction.PowerOn => "确认向服务器发送开机命令？",
            KvmPowerAction.GracefulPowerOff => "确认请求操作系统正常关机？",
            KvmPowerAction.Restart => "确认立即重启服务器？未保存的数据可能丢失。",
            KvmPowerAction.ForcedPowerCycle => "确认强制断电后重新上电？该操作不同于普通重启，可能造成文件系统损坏和数据丢失。",
            _ => "确认立即强制关机？未保存的数据将丢失。",
        };
        if (!await MessageDialog.ConfirmAsync(
                this,
                LocalizationManager.Translate("确认电源操作"),
                LocalizationManager.Translate(confirmation),
                dangerous: action is KvmPowerAction.PowerOff or KvmPowerAction.ForcedPowerCycle))
        {
            return;
        }

        await RunCommandAsync(
            () => session.SendPowerAsync(action, lifetime.Token).AsTask(),
            LocalizationManager.Format("电源命令已发送：{0}", label));
    }

    private void FullscreenButton_Click(object? sender, RoutedEventArgs e)
    {
        fullscreen = !fullscreen;
        WindowState = fullscreen ? WindowState.FullScreen : WindowState.Normal;
    }

    private void HelpButton_Click(object? sender, RoutedEventArgs e) => new HelpWindow().Show(this);

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

    private async Task RunCommandAsync(Func<Task> command, string success)
    {
        try
        {
            await command();
            SetStatus(success, StatusKind.Ready);
        }
        catch (Exception exception)
        {
            SetStatus($"操作失败：{exception.Message}", StatusKind.Error);
        }
    }

    private async Task HandleSessionFailureAsync(
        BladeConsoleRuntime runtime,
        KvmClientSession failed,
        Exception failure)
    {
        byte[]? token = null;
        KvmClientSession? replacement = null;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                runtime.ConnectionFailed = true;
                if (ReferenceEquals(activeRuntime, runtime))
                {
                    SetStatus(
                        LocalizationManager.Format(
                            "刀片 {0} 连接中断，正在尝试恢复：{1}",
                            runtime.State.BladeNumber,
                            failure.Message),
                        StatusKind.Warning);
                    ViewerOverlay.IsVisible = true;
                    OverlayText.Text = LocalizationManager.Translate("正在恢复连接");
                }
            });
            token = failed.CopyReconnectToken();
            replacement = await supervisor.ReconnectAsync(
                token,
                failed.ReconnectAsync,
                async (connected, cancellationToken) =>
                    await runtime.MediaController.ReplaceKvmSessionAsync(
                        connected,
                        restoreMountedMedia: true,
                        cancellationToken),
                lifetime.Token).ConfigureAwait(false);

            runtime.Lifetime.Cancel();
            DetachSessionEvents(failed);
            await failed.DisposeAsync().ConfigureAwait(false);
            runtime.Lifetime.Dispose();
            runtime.Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            chassisCoordinator.ReplaceSession(runtime.State.BladeNumber, failed, replacement);
            runtime.Session = replacement;
            runtime.ConnectionFailed = false;
            runtime.Decoder.Reset();
            runtime.LatestFrame = null;
            runtime.LatestPixels = null;
            runtime.FrameBuffers.Reset();
            replacement = null;
            AttachSessionEvents(runtime.Session);
            runtime.FrameConsumer = ConsumeFramesAsync(runtime, runtime.Lifetime.Token);
            runtime.DiagnosticsConsumer = ConsumeDiagnosticsAsync(runtime, runtime.Lifetime.Token);
            if (runtime.State.BladeNumber == primaryBladeNumber)
            {
                chassisManagementSession = runtime.Session;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(activeRuntime, runtime))
                {
                    SelectRuntime(runtime, updateCoordinator: false);
                    ViewerOverlay.IsVisible = true;
                    OverlayText.Text = LocalizationManager.Translate("连接已恢复，等待视频");
                    SetStatus("KVM 连接已自动恢复", StatusKind.Ready);
                }
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                SetStatus($"自动恢复失败：{exception.Message}", StatusKind.Error));
        }
        finally
        {
            if (replacement is not null)
            {
                await replacement.DisposeAsync();
            }

            if (token is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(token);
            }
        }
    }

    private void Supervisor_ProgressChanged(object? sender, KvmReconnectProgress progress)
    {
        var message = progress.State switch
        {
            "connecting" => $"正在恢复 KVM（{progress.Attempt}/{progress.MaximumAttempts}）",
            "restoring-media" => "KVM 已连接，正在恢复虚拟介质",
            "retrying" => $"恢复失败，准备重试（{progress.Attempt}/{progress.MaximumAttempts}）",
            _ => "正在恢复 KVM",
        };
        Dispatcher.UIThread.Post(() => SetStatus(message, StatusKind.Warning));
    }

    private void ApplyCapabilities()
    {
        var permissions = session.Permissions;
        var canInput = CanSendRemoteInput;
        MouseModeButton.IsEnabled = canInput &&
            session.Capabilities.InputModes != IbmcKvm.Protocol.Profiles.KvmInputModes.None;
        ShowLocalPointerButton.IsEnabled = canInput;
        SynchronizeMouseButton.IsEnabled = canInput;
        KeyboardMenuButton.IsEnabled = canInput;
        ReleaseKeysButton.IsEnabled = canInput;
        VideoQualityButton.IsEnabled = canInput && session.Capabilities.SupportsVideoQuality;
        ColorDepthButton.IsEnabled = canInput && session.Capabilities.ColorDepths.Length > 1;
        RecordButton.IsEnabled = canInput && session.Capabilities.SupportsRecording;
        VirtualMediaButton.IsEnabled = permissions.CanUseVirtualMedia;
        PowerMenuButton.IsEnabled = permissions.CanControlPower;
        ToolTip.SetTip(
            PowerMenuButton,
            LocalizationManager.Translate(permissions.CanControlPower ? "电源操作" : "当前账户没有电源控制权限"));
        ToolTip.SetTip(
            VirtualMediaButton,
            LocalizationManager.Translate(permissions.CanUseVirtualMedia
                ? "虚拟软驱与虚拟光驱"
                : "当前账户没有虚拟介质权限"));
        SetMouseModeSelection(
            session.CurrentMouseMode == KvmMouseMode.Absolute ? PointerMode.Absolute : PointerMode.Relative);
        UpdateConsoleSettingsPresentation();
        UpdateInputAvailability();
    }

    private void SetMouseModeSelection(PointerMode mode)
    {
        pointerMode = mode;
        UpdateMenuSelection(MouseModeButton.ContextMenu, mode switch
        {
            PointerMode.Relative => "Relative",
            PointerMode.Captured => "Captured",
            _ => "Absolute",
        });
    }

    private void UpdateConsoleSettingsPresentation()
    {
        var mouseLabel = pointerMode switch
        {
            PointerMode.Relative => LocalizationManager.Translate("相对"),
            PointerMode.Captured => LocalizationManager.Translate("捕获"),
            _ => LocalizationManager.Translate("绝对"),
        };
        UpdateMenuSelection(MouseModeButton.ContextMenu, pointerMode.ToString());
        ToolTip.SetTip(MouseModeButton, $"{LocalizationManager.Translate("鼠标模式")}：{mouseLabel}");

        var quality = session.CurrentVideoQuality.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateMenuSelection(VideoQualityButton.ContextMenu, quality);
        ToolTip.SetTip(VideoQualityButton, $"{LocalizationManager.Translate("图像清晰度")}：{quality}");

        var depth = session.CurrentColorDepth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateMenuSelection(
            ColorDepthButton.ContextMenu,
            depth,
            tag => byte.TryParse(tag, out var value) && session.Capabilities.ColorDepths.Contains(value));
        var depthLabel = session.CurrentColorDepth switch
        {
            2 => "8-bit",
            1 => "7-bit",
            0 => "6-bit",
            _ => "4-bit",
        };
        ToolTip.SetTip(ColorDepthButton, $"{LocalizationManager.Translate("颜色位数")}：{depthLabel}");
    }

    private static void UpdateMenuSelection(
        ContextMenu? menu,
        string selectedTag,
        Func<string, bool>? isEnabled = null)
    {
        if (menu is null)
        {
            return;
        }

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var tag = item.Tag as string ?? string.Empty;
            item.IsChecked = string.Equals(tag, selectedTag, StringComparison.Ordinal);
            if (isEnabled is not null)
            {
                item.IsEnabled = isEnabled(tag);
            }
        }
    }

    private void CenterPointer()
    {
        if (!captureActive)
        {
            return;
        }

        var target = VideoHost.PointToScreen(new Point(VideoHost.Bounds.Width / 2, VideoHost.Bounds.Height / 2));
        var origin = Position;
        pointerController.Center(
            TryGetPlatformHandle()?.Handle ?? 0,
            target.X - origin.X,
            target.Y - origin.Y);
    }

    private void ReleasePointerCapture()
    {
        pointerController.Release();
        captureActive = false;
        ApplyLocalPointerCursor();
    }

    private void ApplyLocalPointerCursor() =>
        VideoHost.Cursor = showLocalPointer && !captureActive
            ? new Cursor(StandardCursorType.Arrow)
            : new Cursor(StandardCursorType.None);

    private static sbyte ToDelta(double value) =>
        checked((sbyte)Math.Clamp(Math.Round(value), sbyte.MinValue, sbyte.MaxValue));

    private static bool TryGetModifier(KeyEventArgs e, out HidModifiers modifier) =>
        e.PhysicalKey != PhysicalKey.None
            ? AvaloniaHidKeyMap.TryGetModifier(e.PhysicalKey, out modifier)
            : AvaloniaHidKeyMap.TryGetModifier(e.Key, out modifier);

    private static bool TryGetUsage(KeyEventArgs e, out byte usage) =>
        e.PhysicalKey != PhysicalKey.None
            ? AvaloniaHidKeyMap.TryGetUsage(e.PhysicalKey, out usage)
            : AvaloniaHidKeyMap.TryGetUsage(e.Key, out usage);

    private static bool IsLockKey(KeyEventArgs e) => e.PhysicalKey != PhysicalKey.None
        ? AvaloniaHidKeyMap.IsLockKey(e.PhysicalKey)
        : AvaloniaHidKeyMap.IsLockKey(e.Key);

    private static int GetKeyIdentity(KeyEventArgs e) => e.PhysicalKey != PhysicalKey.None
        ? (int)e.PhysicalKey
        : int.MinValue + (int)e.Key;

    private void SetStatus(string message, StatusKind kind)
    {
        var translated = LocalizationManager.Translate(message);
        StatusMessageText.Text = translated;
        InputStatusText.Text = translated;
        InputStatusDot.Fill = Brush.Parse(kind switch
        {
            StatusKind.Ready => "#25B979",
            StatusKind.Warning => "#D39B35",
            StatusKind.Error => "#D55A50",
            _ => "#829089",
        });
        StatusMessageBorder.BorderBrush = InputStatusDot.Fill;
    }

    private bool CanSendRemoteInput => activeRuntime is { } runtime &&
                                       ChassisUiState.CanRouteInput(
                                           runtime.Mode,
                                           chassisCoordinator.IsSplitViewEnabled) &&
                                       session.Permissions.CanControlKvm;

    private void UpdateInputAvailability()
    {
        var message = CanSendRemoteInput
            ? latestFrame is null
                ? "已连接，等待视频"
                : keyboardArmed ? "输入已启用" : "已连接，等待画面焦点"
            : chassisCoordinator.IsSplitViewEnabled
                ? "分屏视图为只读；关闭分屏后可向选中刀片输入"
                : activeRuntime?.Mode == KvmBladeSessionMode.Monitor
                    ? "当前刀片为只读监视会话"
                    : session.Permissions.CanControlKvm
                        ? "已连接，等待画面焦点"
                        : "当前账户没有 KVM 控制权限";
        InputStatusText.Text = LocalizationManager.Translate(message);
        InputStatusDot.Fill = Brush.Parse(CanSendRemoteInput ? "#25B979" : "#77827D");
    }

    private void AttachSessionEvents(KvmClientSession source)
    {
        source.VideoSettingsChanged += Session_VideoSettingsChanged;
        source.RemoteLockKeysChanged += Session_RemoteLockKeysChanged;
        source.PermissionsChanged += Session_PermissionsChanged;
        source.PrivilegeDenied += Session_PrivilegeDenied;
    }

    private void DetachSessionEvents(KvmClientSession source)
    {
        source.VideoSettingsChanged -= Session_VideoSettingsChanged;
        source.RemoteLockKeysChanged -= Session_RemoteLockKeysChanged;
        source.PermissionsChanged -= Session_PermissionsChanged;
        source.PrivilegeDenied -= Session_PrivilegeDenied;
    }

    private void Session_VideoSettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(sender, session))
            {
                UpdateConsoleSettingsPresentation();
            }
        });

    private void Session_RemoteLockKeysChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(sender, session))
            {
                UpdateRemoteLockIndicators(session.RemoteLockKeys);
            }
        });

    private void Session_PermissionsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(sender, session))
            {
                ApplyCapabilities();
            }
        });

    private void Session_PrivilegeDenied(object? sender, KvmPrivilegeDeniedEventArgs e) =>
        Dispatcher.UIThread.Post(async () =>
        {
            var message = e.Operation switch
            {
                KvmPrivilegeOperation.Power => "当前账户没有执行电源操作的权限。",
                KvmPrivilegeOperation.VirtualMedia => "当前账户没有使用虚拟软驱或虚拟光驱的权限。",
                _ => $"iBMC 拒绝了操作（状态 {e.State}）。",
            };
            SetStatus(message, StatusKind.Error);
            ApplyCapabilities();
            await MessageDialog.ShowAsync(this, LocalizationManager.Translate("权限不足"), LocalizationManager.Translate(message));
        });

    private async Task RequestRemoteLockKeysAsync(
        KvmClientSession source,
        CancellationToken cancellationToken)
    {
        try
        {
            await source.RequestKeyboardStateAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
                SetStatus(LocalizationManager.Format("远端锁定键状态查询失败：{0}", exception.Message), StatusKind.Warning));
        }
    }

    private async Task RefreshRemoteLockKeysAfterInputAsync(
        KvmClientSession source,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var delay in new[] { 80, 180, 350 })
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(source, session))
                {
                    return;
                }

                await RequestRemoteLockKeysAsync(source, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void UpdateRemoteLockIndicators(RemoteLockKeys state)
    {
        var on = Brush.Parse("#F0C674");
        var off = Brush.Parse("#59635F");
        NumLockIndicator.Fill = state.HasFlag(RemoteLockKeys.NumLock) ? on : off;
        CapsLockIndicator.Fill = state.HasFlag(RemoteLockKeys.CapsLock) ? on : off;
        ScrollLockIndicator.Fill = state.HasFlag(RemoteLockKeys.ScrollLock) ? on : off;
    }

    private void ToolbarRevealZone_PointerEntered(object? sender, PointerEventArgs e)
    {
        toolbarHideTimer.Stop();
        FloatingToolbar.IsVisible = true;
        ToolbarHiddenHandle.IsVisible = false;
    }

    private void FloatingToolbar_PointerEntered(object? sender, PointerEventArgs e)
    {
        pointerOverToolbar = true;
        toolbarHideTimer.Stop();
    }

    private void FloatingToolbar_PointerExited(object? sender, PointerEventArgs e)
    {
        pointerOverToolbar = false;
        ScheduleToolbarHide();
    }

    private void PinToolbarButton_Click(object? sender, RoutedEventArgs e)
    {
        toolbarPinned = PinToolbarButton.IsChecked == true;
        ToolTip.SetTip(
            PinToolbarButton,
            LocalizationManager.Translate(toolbarPinned
                ? "取消固定，鼠标离开后自动隐藏"
                : "固定工具栏，始终显示"));
        FloatingToolbar.IsVisible = true;
        ToolbarHiddenHandle.IsVisible = false;
        ScheduleToolbarHide();
    }

    private void ScheduleToolbarHide()
    {
        toolbarHideTimer.Stop();
        if (!toolbarPinned && !pointerOverToolbar)
        {
            toolbarHideTimer.Start();
        }
    }

    private void ToolbarHideTimer_Tick(object? sender, EventArgs e)
    {
        toolbarHideTimer.Stop();
        if (!toolbarPinned && !pointerOverToolbar)
        {
            FloatingToolbar.IsVisible = false;
            ToolbarHiddenHandle.IsVisible = true;
        }
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        closing = true;
        lifetime.Cancel();
        ReleasePointerCapture();
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        if (!closing)
        {
            lifetime.Cancel();
        }

        supervisor.ProgressChanged -= Supervisor_ProgressChanged;
        toolbarHideTimer.Stop();
        pointerController.Dispose();
        virtualMediaWindow?.Close();
        await StopRecordingAsync();
        var runtimes = bladeRuntimes.Values.ToArray();
        foreach (var runtime in runtimes)
        {
            DetachSessionEvents(runtime.Session);
            runtime.Lifetime.Cancel();
        }

        foreach (var runtime in runtimes)
        {
            await runtime.MediaController.DisposeAsync();
        }

        try
        {
            await chassisCoordinator.DisposeAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"KVM session cleanup failed: {exception}");
        }
        foreach (var runtime in runtimes)
        {
            await runtime.AwaitConsumerAsync();
            runtime.FrameBuffers.Dispose();
        }

        bladeRuntimes.Clear();
        lifetime.Dispose();
    }

    private enum PointerMode
    {
        Absolute,
        Relative,
        Captured,
    }

    private enum StatusKind
    {
        Neutral,
        Ready,
        Warning,
        Error,
    }

    private sealed class BladeConsoleRuntime(
        ChassisBladeState state,
        KvmClientSession session,
        KvmBladeSessionMode mode,
        string endpointDisplay,
        VirtualMediaController mediaController)
    {
        public ChassisBladeState State { get; set; } = state;

        public KvmClientSession Session { get; set; } = session;

        public KvmBladeSessionMode Mode { get; } = mode;

        public string EndpointDisplay { get; } = endpointDisplay;

        public VirtualMediaController MediaController { get; } = mediaController;

        public CancellationTokenSource Lifetime { get; set; } = new();

        public Task? FrameConsumer { get; set; }

        public Task? DiagnosticsConsumer { get; set; }

        public BlockVideoDecoder Decoder { get; } = new();

        public DoubleBuffer<WriteableBitmap> FrameBuffers { get; } = new();

        public WriteableBitmap? Bitmap => FrameBuffers.Front;

        public EncodedVideoFrame? LatestFrame { get; set; }

        public byte[]? LatestPixels { get; set; }

        public Image? SplitImage { get; set; }

        public Stopwatch FrameClock { get; } = Stopwatch.StartNew();

        public int RenderedFrames { get; set; }

        public long ReportedFrameErrors { get; set; }

        public bool ConnectionFailed { get; set; }

        public async Task AwaitConsumerAsync()
        {
            try
            {
                if (FrameConsumer is not null)
                {
                    await FrameConsumer;
                }

                if (DiagnosticsConsumer is not null)
                {
                    await DiagnosticsConsumer;
                }
            }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                Lifetime.Dispose();
            }
        }
    }

    private sealed class UnsupportedPointerController(string reason) : IPointerController
    {
        public bool IsSupported => false;

        public string UnsupportedReason => reason;

        public bool TryCapture(nint windowHandle) => false;

        public void Release()
        {
        }

        public void Center(nint windowHandle, int x, int y)
        {
        }

        public void Dispose()
        {
        }
    }
}
