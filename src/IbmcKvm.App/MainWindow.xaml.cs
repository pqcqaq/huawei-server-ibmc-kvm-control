using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IbmcKvm.App.Input;
using IbmcKvm.App.Localization;
using IbmcKvm.App.Recording;
using IbmcKvm.App.Ui;
using IbmcKvm.Core.Input;
using IbmcKvm.Core.Recording;
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
    private static readonly Brush RemoteLockOnBrush = CreateFrozenBrush(Color.FromRgb(240, 198, 116));
    private static readonly Brush RemoteLockOffBrush = CreateFrozenBrush(Color.FromRgb(89, 99, 95));
    private static readonly Duration ToolbarAnimationDuration = new(TimeSpan.FromMilliseconds(160));
    private readonly HidKeyboardState keyboard = new();
    private readonly FloatingToolbarState toolbarState = new();
    private readonly KvmSessionSupervisor sessionSupervisor = new();
    private readonly ConsolePointerState pointerState = new();
    private readonly DispatcherTimer toolbarHideTimer;
    private readonly Dictionary<int, byte> pressedVirtualKeys = [];
    private readonly Dictionary<byte, BladeConsoleRuntime> bladeRuntimes = [];
    private readonly ObservableCollection<ChassisBladePresentation> chassisItems = [];
    private readonly ObservableCollection<ChassisBladePresentation> bladeTabs = [];
    private readonly ChassisConsoleCoordinator<KvmClientSession> chassisCoordinator;
    private KvmClientSession chassisManagementSession;
    private readonly byte primaryBladeNumber;
    private readonly bool exclusiveChassisQueries;
    private KvmClientSession? session;
    private BladeConsoleRuntime? activeRuntime;
    private ChassisSnapshot? chassisSnapshot;
    private VirtualMediaController? virtualMediaController;
    private VirtualMediaWindow? virtualMediaWindow;
    private ConsoleRecorder? recorder;
    private AviConsoleRecorder? aviRecorder;
    private CancellationTokenSource? sessionLifetime;
    private CancellationTokenSource? reconnectLifetime;
    private Task? frameConsumer;
    private Task? diagnosticsConsumer;
    private WriteableBitmap? bitmap;
    private EncodedVideoFrame? latestFrame;
    private byte mouseButtons;
    private ushort lastMouseX;
    private ushort lastMouseY;
    private Point? lastRelativePoint;
    private long lastMouseSend;
    private bool fullScreen;
    private bool hasLastMousePosition;
    private bool applyingBladeSelection;
    private RemoteKeyboardLayout keyboardLayout = RemoteKeyboardLayout.UnitedStates;
    private bool connectionFailed;
    private int disconnectStarted;
    private int disposeStarted;
    private int toolbarAnimationVersion;
    private int reconnectStarted;
    private RemoteInputState remoteInputState = RemoteInputState.Disconnected;
    private WindowStyle previousWindowStyle;
    private WindowState previousWindowState;
    private ResizeMode previousResizeMode;

    public MainWindow(
        KvmClientSession connectedSession,
        string endpointDisplay,
        bool settingsPersisted,
        bool exclusiveChassisQueries = false)
    {
        ArgumentNullException.ThrowIfNull(connectedSession);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointDisplay);
        InitializeComponent();
        this.exclusiveChassisQueries = exclusiveChassisQueries;
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
        ChassisBladeList.ItemsSource = chassisItems;
        BladeTabsList.ItemsSource = bladeTabs;
        session = connectedSession;
        sessionSupervisor.ProgressChanged += SessionSupervisor_ProgressChanged;
        pointerState.SetMode(
            connectedSession.CurrentMouseMode == KvmMouseMode.Relative
                ? ConsolePointerMode.Relative
                : ConsolePointerMode.Absolute);
        UpdateConsoleSettingsPresentation(connectedSession);
        ApplyLocalPointerCursor();
        var initialRuntime = AddBladeRuntime(
            initialState,
            connectedSession,
            KvmBladeSessionMode.Control,
            endpointDisplay);
        SelectRuntime(initialRuntime, updateCoordinator: false);
        toolbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        toolbarHideTimer.Tick += ToolbarHideTimer_Tick;
        toolbarState.SetPinned(isPinned: true);
        ApplyToolbarState();
        ApplySessionPermissions(connectedSession.Permissions);
        ConnectedEndpointText.Text = endpointDisplay;
        SetStatus(
            settingsPersisted
                ? LocalizationManager.Format("已连接 {0}", endpointDisplay)
                : LocalizationManager.Format("已连接 {0}，本地设置未能更新", endpointDisplay),
            settingsPersisted ? InputReadyBrush : Brushes.Goldenrod);
        UpdateRemoteInputStatus();
        if (connectedSession.Capabilities.SupportsChassis)
        {
            _ = RefreshChassisAsync(silent: true);
        }
    }

    private async Task ConsumeFramesAsync(BladeConsoleRuntime runtime, CancellationToken cancellationToken)
    {
        var sourceSession = runtime.Session;
        try
        {
            await foreach (var frame in sourceSession.ReadFramesAsync(cancellationToken))
            {
                var activeRecorder = ReferenceEquals(activeRuntime, runtime)
                    ? Volatile.Read(ref recorder)
                    : null;
                try
                {
                    activeRecorder?.TryRecord(frame, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                catch (ObjectDisposedException)
                {
                    // Stop can race one already-delivered frame; recording is best effort.
                }
                byte[] pixels;
                try
                {
                    pixels = await Task.Run(() => runtime.Decoder.Decode(frame), cancellationToken);
                }
                catch (InvalidDataException exception)
                {
                    runtime.Decoder.Reset();
                    await sourceSession.RequestFullFrameAsync(cancellationToken);
                    await Dispatcher.InvokeAsync(() => SetStatus(
                        LocalizationManager.Format("视频解码：{0}", exception.Message),
                        Brushes.Goldenrod));
                    continue;
                }

                var activeAviRecorder = ReferenceEquals(activeRuntime, runtime)
                    ? Volatile.Read(ref aviRecorder)
                    : null;
                try
                {
                    activeAviRecorder?.TryRecord(frame, pixels);
                }
                catch (ObjectDisposedException)
                {
                    // Stop can race one decoded frame; recording is best effort.
                }
                await Dispatcher.InvokeAsync(() => DisplayFrame(runtime, frame, pixels));
            }

            if (!cancellationToken.IsCancellationRequested && sourceSession.Failure is not null)
            {
                await HandleSessionFailureAsync(runtime, sourceSession, sourceSession.Failure);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && sourceSession.Failure is not null)
        {
            await HandleSessionFailureAsync(runtime, sourceSession, sourceSession.Failure);
        }
    }

    private async Task ConsumeDiagnosticsAsync(BladeConsoleRuntime runtime, CancellationToken cancellationToken)
    {
        var sourceSession = runtime.Session;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var diagnostics = sourceSession.GetDiagnostics();
                await Dispatcher.InvokeAsync(() =>
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

                    if (!string.IsNullOrEmpty(diagnostics.LastFrameError))
                    {
                        SetStatus(LocalizationManager.Format("视频协议：{0}", diagnostics.LastFrameError), Brushes.Goldenrod);
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void DisplayFrame(BladeConsoleRuntime runtime, EncodedVideoFrame frame, byte[] bgraPixels)
    {
        if (runtime.Bitmap is null ||
            runtime.Bitmap.PixelWidth != frame.Width ||
            runtime.Bitmap.PixelHeight != frame.Height)
        {
            runtime.Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            if (runtime.SplitImage is not null)
            {
                runtime.SplitImage.Source = runtime.Bitmap;
            }
        }

        runtime.Bitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            bgraPixels,
            checked(frame.Width * 4),
            0);
        runtime.LatestFrame = frame;
        if (!ReferenceEquals(activeRuntime, runtime))
        {
            return;
        }

        bitmap = runtime.Bitmap;
        latestFrame = runtime.LatestFrame;
        RemoteImage.Source = runtime.Bitmap;
        ViewerOverlay.Visibility = Visibility.Collapsed;
        ScreenshotButton.IsEnabled = true;
        UpdateRemoteInputStatus();

        runtime.RenderedFrames++;
        var elapsed = runtime.FrameClock.Elapsed.TotalSeconds;
        if (elapsed >= 1)
        {
            VideoMetricsText.Text = $"{frame.Width} × {frame.Height}   {runtime.RenderedFrames / elapsed:0.0} fps";
            runtime.RenderedFrames = 0;
            runtime.FrameClock.Restart();
        }
    }

    private BladeConsoleRuntime AddBladeRuntime(
        ChassisBladeState state,
        KvmClientSession connectedSession,
        KvmBladeSessionMode mode,
        string endpointDisplay)
    {
        var runtime = new BladeConsoleRuntime(
            state,
            connectedSession,
            mode,
            endpointDisplay,
            new VirtualMediaController(connectedSession));
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
        virtualMediaController = runtime.MediaController;
        sessionLifetime = runtime.Lifetime;
        frameConsumer = runtime.FrameConsumer;
        diagnosticsConsumer = runtime.DiagnosticsConsumer;
        bitmap = runtime.Bitmap;
        latestFrame = runtime.LatestFrame;
        connectionFailed = runtime.ConnectionFailed;
        RemoteImage.Source = runtime.Bitmap;
        ViewerOverlay.Visibility = runtime.LatestFrame is null ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotButton.IsEnabled = runtime.LatestFrame is not null;
        ConnectedEndpointText.Text = runtime.EndpointDisplay;
        pointerState.SetMode(
            runtime.Session.CurrentMouseMode == KvmMouseMode.Relative
                ? ConsolePointerMode.Relative
                : ConsolePointerMode.Absolute);
        UpdateConsoleSettingsPresentation(runtime.Session);
        ApplyLocalPointerCursor();
        ApplySessionPermissions(runtime.Session.Permissions);
        UpdateRemoteLockIndicators(runtime.Session.RemoteLockKeys);
        RefreshBladePresentations();
        UpdateBladeTabSelection();
        UpdateSplitView();
        UpdateRemoteInputStatus();
    }

    private void AttachSessionEvents(KvmClientSession sourceSession)
    {
        sourceSession.VideoSettingsChanged += Session_VideoSettingsChanged;
        sourceSession.RemoteLockKeysChanged += Session_RemoteLockKeysChanged;
        sourceSession.PermissionsChanged += Session_PermissionsChanged;
        sourceSession.PrivilegeDenied += Session_PrivilegeDenied;
    }

    private async Task RefreshChassisAsync(bool silent)
    {
        if (!chassisManagementSession.Capabilities.SupportsChassis ||
            Volatile.Read(ref disconnectStarted) != 0)
        {
            return;
        }

        try
        {
            var snapshot = await chassisManagementSession.RefreshChassisAsync(
                exclusiveChassisQueries,
                TimeSpan.FromSeconds(3),
                sessionLifetime?.Token ?? CancellationToken.None);
            await Dispatcher.InvokeAsync(() =>
            {
                chassisSnapshot = snapshot;
                ChassisButton.Visibility = Visibility.Visible;
                RefreshBladePresentations();
                if (!silent)
                {
                    SetStatus("机箱状态已刷新", InputReadyBrush);
                }
            });
        }
        catch (Exception exception) when (silent &&
                                          exception is TimeoutException or InvalidDataException or
                                              NotSupportedException or IOException)
        {
            // A standalone BMC can use the same KVM profile without implementing chassis commands.
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disconnectStarted) != 0)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
                SetStatus(LocalizationManager.Format("机箱刷新失败：{0}", exception.Message), InputFailedBrush));
        }
    }

    private void RefreshBladePresentations()
    {
        var connected = chassisCoordinator.Sessions.Select(slot => slot.BladeNumber).ToArray();
        var states = chassisSnapshot?.Blades ??
                     bladeRuntimes.Values.Select(runtime => runtime.State).OrderBy(state => state.BladeNumber).ToImmutableArray();
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
        BladeTabsBar.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;
        SplitViewButton.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;
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

    private async void BladeTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
        SetStatus(LocalizationManager.Format("已选择刀片 {0}", runtime.State.BladeNumber), InputReadyBrush);
    }

    private void ChassisBladeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectedBladeActions();

    private void UpdateSelectedBladeActions()
    {
        var selected = ChassisBladeList.SelectedItem as ChassisBladePresentation;
        ConnectBladeButton.IsEnabled = selected?.CanConnect == true;
        MonitorBladeButton.IsEnabled = selected?.CanMonitor == true;
    }

    private void ChassisButton_Click(object sender, RoutedEventArgs e)
    {
        ChassisPanel.Visibility = ChassisPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void RefreshChassisButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshChassisAsync(silent: false);

    private async void ConnectBladeButton_Click(object sender, RoutedEventArgs e) =>
        await ConnectSelectedBladeAsync(KvmBladeSessionMode.Control);

    private async void MonitorBladeButton_Click(object sender, RoutedEventArgs e) =>
        await ConnectSelectedBladeAsync(KvmBladeSessionMode.Monitor);

    private async Task ConnectSelectedBladeAsync(KvmBladeSessionMode mode)
    {
        if (ChassisBladeList.SelectedItem is not ChassisBladePresentation selected ||
            chassisSnapshot is null)
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
            Brushes.Goldenrod);
        try
        {
            var slot = await chassisCoordinator.ConnectAsync(
                state,
                mode,
                sessionLifetime?.Token ?? CancellationToken.None);
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
                InputReadyBrush);
        }
        catch (Exception exception)
        {
            SetStatus(LocalizationManager.Format("刀片连接失败：{0}", exception.Message), InputFailedBrush);
        }
        finally
        {
            RefreshBladePresentations();
            UpdateSelectedBladeActions();
        }
    }

    private async void DisconnectBladeButton_Click(object sender, RoutedEventArgs e)
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
        await chassisCoordinator.DisconnectAsync(runtime.State.BladeNumber);
        await runtime.AwaitConsumersAsync();
        bladeRuntimes.Remove(runtime.State.BladeNumber);
        if (chassisCoordinator.SelectedBladeNumber is { } selected &&
            bladeRuntimes.TryGetValue(selected, out var replacement))
        {
            SelectRuntime(replacement, updateCoordinator: false);
        }

        RefreshBladePresentations();
        UpdateSplitView();
        SetStatus(LocalizationManager.Format("刀片 {0} 会话已关闭", runtime.State.BladeNumber), InputReadyBrush);
    }

    private void SplitViewButton_Click(object sender, RoutedEventArgs e)
    {
        chassisCoordinator.SetSplitView(SplitViewButton.IsChecked == true);
        UpdateSplitView();
        UpdateRemoteInputStatus();
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
            SplitVideoGrid.Visibility = Visibility.Collapsed;
            RemoteImage.Visibility = Visibility.Visible;
            ViewerOverlay.Visibility = latestFrame is null ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        SplitVideoGrid.RowDefinitions.Add(new RowDefinition());
        SplitVideoGrid.RowDefinitions.Add(new RowDefinition());
        SplitVideoGrid.ColumnDefinitions.Add(new ColumnDefinition());
        SplitVideoGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var index = 0;
        foreach (var runtime in bladeRuntimes.Values.OrderBy(runtime => runtime.State.BladeNumber))
        {
            var image = new Image
            {
                Source = runtime.Bitmap,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
            };
            runtime.SplitImage = image;
            var label = new TextBlock
            {
                Text = LocalizationManager.Format(
                    "刀片 {0} · {1}",
                    runtime.State.BladeNumber,
                    LocalizationManager.Translate(runtime.Mode == KvmBladeSessionMode.Monitor ? "监视" : "控制")),
                Foreground = Brushes.White,
                Background = CreateFrozenBrush(Color.FromArgb(210, 26, 33, 30)),
                Padding = new Thickness(7, 4, 7, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                FontSize = 10,
            };
            var cell = new Grid();
            cell.Children.Add(image);
            cell.Children.Add(label);
            var border = new Border
            {
                BorderBrush = runtime == activeRuntime ? InputReadyBrush : CreateFrozenBrush(Color.FromRgb(62, 74, 69)),
                BorderThickness = new Thickness(runtime == activeRuntime ? 2 : 1),
                Margin = new Thickness(3),
                Child = cell,
            };
            Grid.SetRow(border, index / 2);
            Grid.SetColumn(border, index % 2);
            SplitVideoGrid.Children.Add(border);
            index++;
        }

        RemoteImage.Visibility = Visibility.Collapsed;
        ViewerOverlay.Visibility = Visibility.Collapsed;
        SplitVideoGrid.Visibility = Visibility.Visible;
    }

    private static string FormatBladeEndpoint(ChassisBladeState state)
    {
        var host = state.UsesManagementAddress
            ? LocalizationManager.Translate("机箱转发")
            : state.Address?.ToString() ?? LocalizationManager.Translate("未知地址");
        return state.Port is { } port ? $"{host}:{port}" : host;
    }

    private async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref disconnectStarted, 1) != 0)
        {
            return;
        }

        virtualMediaWindow?.Close();
        virtualMediaWindow = null;
        CancelReconnect();
        await StopRecordingAsync();
        virtualMediaController = null;
        var runtimes = bladeRuntimes.Values.ToArray();
        foreach (var runtime in runtimes)
        {
            CancelSafely(runtime.Lifetime);
            DetachSessionEvents(runtime.Session);
        }

        session = null;
        sessionLifetime = null;
        frameConsumer = null;
        diagnosticsConsumer = null;
        foreach (var runtime in runtimes)
        {
            await runtime.MediaController.DisposeAsync();
        }

        await chassisCoordinator.DisposeAsync();
        foreach (var runtime in runtimes)
        {
            await runtime.AwaitConsumersAsync();
        }
        bladeRuntimes.Clear();

        keyboard.Clear();
        pressedVirtualKeys.Clear();
        mouseButtons = 0;
        hasLastMousePosition = false;
        Mouse.Capture(null);
        activeRuntime = null;
        bitmap = null;
        latestFrame = null;
        RemoteImage.Source = null;
        ViewerOverlay.Visibility = Visibility.Visible;
        VideoMetricsText.Text = "无视频信号";
        UpdateRemoteInputStatus();
    }

    private async void VideoHost_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && pointerState.IsCaptureActive)
        {
            ReleaseCapturedPointer();
            SetStatus("已释放捕获鼠标", InputReadyBrush);
            e.Handled = true;
            return;
        }

        if (!CanSendRemoteInput)
        {
            return;
        }

        var virtualKey = GetVirtualKey(e);
        var activeSession = session;
        if (WindowsVirtualKeyMap.TryGetModifier(virtualKey, out var modifier))
        {
            if (keyboard.SetModifier(modifier, pressed: true))
            {
                e.Handled = true;
                await SendKeyboardSafelyAsync(keyboard.CreateReport());
            }

            return;
        }

        if (!TryMapKey(virtualKey, out var usage))
        {
            return;
        }

        var isRepeated = !pressedVirtualKeys.TryAdd(virtualKey, usage);
        e.Handled = true;
        if (isRepeated && WindowsVirtualKeyMap.IsLockKey(virtualKey))
        {
            return;
        }

        await SendKeyboardSafelyAsync(keyboard.CreateKeyPressReport(usage));
        if (activeSession is not null && WindowsVirtualKeyMap.IsLockKey(virtualKey))
        {
            var cancellationToken = activeRuntime?.Lifetime.Token ?? CancellationToken.None;
            _ = RefreshRemoteLockKeysAfterInputAsync(activeSession, cancellationToken);
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
            : pressedVirtualKeys.Remove(virtualKey);
        if (changed)
        {
            e.Handled = true;
            await SendKeyboardSafelyAsync(keyboard.CreateReport());
        }
    }

    private void VideoHost_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdateRemoteInputStatus();

    private async void VideoHost_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        await ReleaseRemoteInputAsync();
        UpdateRemoteInputStatus();
    }

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
        if (pointerState.Mode == ConsolePointerMode.Captured)
        {
            ActivateCapturedPointer();
        }
        else
        {
            Mouse.Capture(VideoHost, CaptureMode.Element);
        }
        e.Handled = true;
        if (pointerState.IsCaptureActive)
        {
            await session.SendRelativeMouseAsync(mouseButtons, 0, 0);
        }
        else
        {
            await SendMouseAtCurrentPositionAsync(e.GetPosition(VideoHost), 0);
        }
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
        if (mouseButtons == 0 && !pointerState.IsCaptureActive)
        {
            Mouse.Capture(null);
        }
        e.Handled = true;
        if (pointerState.IsCaptureActive)
        {
            await session.SendRelativeMouseAsync(mouseButtons, 0, 0);
            return;
        }

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
        if (activeSession is null)
        {
            return;
        }

        try
        {
            if (activeSession.CurrentMouseMode == KvmMouseMode.Relative)
            {
                if (pointerState.IsCaptureActive)
                {
                    var center = new Point(VideoHost.ActualWidth / 2, VideoHost.ActualHeight / 2);
                    var capturedDeltaX = checked((sbyte)Math.Clamp(
                        Math.Round(point.X - center.X),
                        sbyte.MinValue,
                        sbyte.MaxValue));
                    var capturedDeltaY = checked((sbyte)Math.Clamp(
                        Math.Round(point.Y - center.Y),
                        sbyte.MinValue,
                        sbyte.MaxValue));
                    await activeSession.SendRelativeMouseAsync(
                        mouseButtons,
                        capturedDeltaX,
                        capturedDeltaY,
                        wheel);
                    CenterCapturedPointer();
                    return;
                }

                var previous = lastRelativePoint;
                lastRelativePoint = point;
                var deltaX = previous is null
                    ? (sbyte)0
                    : checked((sbyte)Math.Clamp(Math.Round(point.X - previous.Value.X), sbyte.MinValue, sbyte.MaxValue));
                var deltaY = previous is null
                    ? (sbyte)0
                    : checked((sbyte)Math.Clamp(Math.Round(point.Y - previous.Value.Y), sbyte.MinValue, sbyte.MaxValue));
                await activeSession.SendRelativeMouseAsync(mouseButtons, deltaX, deltaY, wheel);
                return;
            }

            if (!TryMapPointer(point, out var x, out var y))
            {
                return;
            }

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
            if (activeSession.CurrentMouseMode == KvmMouseMode.Relative)
            {
                await activeSession.SendRelativeMouseAsync(mouseButtons, 0, 0);
            }
            else
            {
                await activeSession.SendAbsoluteMouseAsync(mouseButtons, lastMouseX, lastMouseY);
            }
        }
        catch (Exception exception)
        {
            MarkConnectionFailed(exception, "输入发送失败");
        }
    }

    private async Task ReleaseRemoteInputAsync()
    {
        var activeSession = session;
        var releaseMouse = mouseButtons != 0;
        mouseButtons = 0;
        ReleaseCapturedPointer();
        var releaseKeyboard = keyboard.Clear();
        pressedVirtualKeys.Clear();
        if (activeSession is null)
        {
            return;
        }

        try
        {
            await activeSession.SendKeyboardAsync(releaseKeyboard);
            if (releaseMouse)
            {
                if (activeSession.CurrentMouseMode == KvmMouseMode.Relative)
                {
                    await activeSession.SendRelativeMouseAsync(0, 0, 0);
                }
                else
                {
                    await activeSession.SendAbsoluteMouseAsync(0, lastMouseX, lastMouseY);
                }
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

    private bool TryMapKey(int virtualKey, out byte usage)
    {
        return WindowsVirtualKeyMap.TryGetUsage(
            virtualKey,
            keyboardLayout,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
            out usage);
    }

    private void KeyboardMenuButton_Click(object sender, RoutedEventArgs e)
        => ContextMenuButton_Click(sender, e);

    private void ContextMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private async void PresetCombinationMenuItem_Click(object sender, RoutedEventArgs e)
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
            await SendCombinationAsync(combination, preset == "KeyboardReset" ? "远端键盘已重置" : "组合键已发送");
        }
    }

    private async void CustomCombinationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ReleaseRemoteInputAsync();
        var dialog = new CustomKeyCombinationWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Combination is { } combination)
        {
            await SendCombinationAsync(combination, "自定义组合键已发送");
        }
    }

    private async Task SendCombinationAsync(HidKeyCombination combination, string successMessage)
    {
        var activeSession = session;
        if (activeSession is null)
        {
            return;
        }

        await ReleaseRemoteInputAsync();
        try
        {
            await activeSession.SendKeyCombinationAsync(combination);
            SetStatus(successMessage, InputReadyBrush);
        }
        catch (Exception exception)
        {
            MarkConnectionFailed(exception, "组合键发送失败");
        }
        finally
        {
            VideoHost.Focus();
        }
    }

    private async void KeyboardLayoutMenuItem_Click(object sender, RoutedEventArgs e)
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
        SetStatus(LocalizationManager.Format("键盘布局已切换为 {0}", label), InputReadyBrush);
        VideoHost.Focus();
    }

    private async void ReleaseKeysButton_Click(object sender, RoutedEventArgs e) =>
        await SendKeyboardSafelyAsync(keyboard.Clear());

    private async void PowerOnButton_Click(object sender, RoutedEventArgs e) =>
        await SendPowerAsync(KvmPowerAction.PowerOn, "确认向服务器发送开机命令？");

    private async void GracefulPowerOffButton_Click(object sender, RoutedEventArgs e) =>
        await SendPowerAsync(KvmPowerAction.GracefulPowerOff, "确认请求操作系统正常关机？");

    private async void RestartButton_Click(object sender, RoutedEventArgs e) =>
        await SendPowerAsync(KvmPowerAction.Restart, "确认立即重启服务器？未保存的数据可能丢失。");

    private async void ForcedPowerCycleButton_Click(object sender, RoutedEventArgs e) =>
        await SendPowerAsync(
            KvmPowerAction.ForcedPowerCycle,
            "确认强制断电后重新上电？该操作不同于普通重启，可能造成文件系统损坏和数据丢失。");

    private async void PowerOffButton_Click(object sender, RoutedEventArgs e) =>
        await SendPowerAsync(KvmPowerAction.PowerOff, "确认立即强制关机？未保存的数据将丢失。");

    private async Task SendPowerAsync(KvmPowerAction action, string confirmation)
    {
        var activeSession = session;
        if (activeSession is null || MessageBox.Show(
            this,
            LocalizationManager.Translate(confirmation),
            LocalizationManager.Translate("确认电源操作"),
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
            SetStatus(LocalizationManager.Format("电源命令失败：{0}", exception.Message), InputFailedBrush);
            MessageBox.Show(
                this,
                exception.Message,
                LocalizationManager.Translate("电源命令失败"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
            Filter = LocalizationManager.Translate("PNG 图像 (*.png)|*.png"),
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
        SetStatus(LocalizationManager.Format("截图已保存：{0}", Path.GetFileName(dialog.FileName)), InputReadyBrush);
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

    private async void Window_Deactivated(object? sender, EventArgs e)
    {
        await ReleaseRemoteInputAsync();
        UpdateRemoteInputStatus();
    }

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
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        toolbarHideTimer.Stop();
        sessionSupervisor.ProgressChanged -= SessionSupervisor_ProgressChanged;
        CancelReconnect();
        foreach (var runtime in bladeRuntimes.Values)
        {
            CancelSafely(runtime.Lifetime);
        }
        sessionLifetime = null;
        virtualMediaWindow?.Close();
        virtualMediaWindow = null;
        GC.SuppressFinalize(this);
    }

    private async Task HandleSessionFailureAsync(
        BladeConsoleRuntime runtime,
        KvmClientSession failedSession,
        Exception failure)
    {
        if (Volatile.Read(ref disconnectStarted) != 0 ||
            Interlocked.CompareExchange(ref reconnectStarted, 1, 0) != 0)
        {
            return;
        }

        var reconnectCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref reconnectLifetime, reconnectCancellation);
        if (Volatile.Read(ref disconnectStarted) != 0)
        {
            reconnectCancellation.Cancel();
        }

        byte[]? token = null;
        KvmClientSession? pendingReplacement = null;
        Exception? mediaRestoreFailure = null;
        try
        {
            DetachSessionEvents(failedSession);
            await Dispatcher.InvokeAsync(() =>
            {
                runtime.ConnectionFailed = true;
                if (ReferenceEquals(activeRuntime, runtime))
                {
                    connectionFailed = true;
                    SetStatus(
                        LocalizationManager.Format(
                            "刀片 {0} 连接中断，正在尝试恢复：{1}",
                            runtime.State.BladeNumber,
                            failure.Message),
                        Brushes.Goldenrod);
                    UpdateRemoteInputStatus();
                }
            });

            token = failedSession.CopyReconnectToken();
            pendingReplacement = await sessionSupervisor.ReconnectAsync(
                token,
                failedSession.ReconnectAsync,
                async (replacement, cancellationToken) =>
                {
                    try
                    {
                        await runtime.MediaController.ReplaceKvmSessionAsync(
                            replacement,
                            restoreMountedMedia: true,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        mediaRestoreFailure = exception;
                    }
                    catch (OperationCanceledException)
                    {
                        await replacement.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                },
                reconnectCancellation.Token).ConfigureAwait(false);

            var activated = false;
            await Dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref disconnectStarted) == 0)
                {
                    ActivateReconnectedSession(runtime, failedSession, pendingReplacement!, mediaRestoreFailure);
                    activated = true;
                }
            });
            if (!activated)
            {
                return;
            }

            pendingReplacement = null;
            await failedSession.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reconnectCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                runtime.ConnectionFailed = true;
                if (ReferenceEquals(activeRuntime, runtime))
                {
                    connectionFailed = true;
                    SetStatus(
                        exception is KvmReconnectException reconnectException
                            ? LocalizationManager.Format(
                                "KVM 自动恢复失败：已尝试 {0} 次，请检查网络和服务器状态。",
                                reconnectException.AttemptCount)
                            : LocalizationManager.Format("KVM 自动恢复失败：{0}", exception.Message),
                        InputFailedBrush);
                    UpdateRemoteInputStatus();
                }
            });
        }
        finally
        {
            if (pendingReplacement is not null)
            {
                await pendingReplacement.DisposeAsync().ConfigureAwait(false);
            }

            if (token is not null)
            {
                CryptographicOperations.ZeroMemory(token);
            }

            Interlocked.CompareExchange(ref reconnectLifetime, null, reconnectCancellation);
            reconnectCancellation.Dispose();
            Interlocked.Exchange(ref reconnectStarted, 0);
        }
    }

    private void CancelReconnect()
    {
        var cancellation = Interlocked.Exchange(ref reconnectLifetime, null);
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion owns disposal; a concurrent window close only needs best-effort cancellation.
        }
    }

    private static void CancelSafely(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent reconnect completion can already own and dispose this runtime lifetime.
        }
    }

    private void ActivateReconnectedSession(
        BladeConsoleRuntime runtime,
        KvmClientSession failedSession,
        KvmClientSession reconnected,
        Exception? mediaRestoreFailure)
    {
        chassisCoordinator.ReplaceSession(runtime.State.BladeNumber, failedSession, reconnected);
        runtime.Lifetime.Cancel();
        runtime.Lifetime.Dispose();
        runtime.Lifetime = new CancellationTokenSource();
        runtime.Session = reconnected;
        runtime.ConnectionFailed = false;
        runtime.Decoder.Reset();
        runtime.LatestFrame = null;
        runtime.Bitmap = null;
        AttachSessionEvents(reconnected);
        runtime.FrameConsumer = ConsumeFramesAsync(runtime, runtime.Lifetime.Token);
        runtime.DiagnosticsConsumer = ConsumeDiagnosticsAsync(runtime, runtime.Lifetime.Token);
        _ = RequestRemoteLockKeysAsync(reconnected, runtime.Lifetime.Token);
        if (runtime.State.BladeNumber == primaryBladeNumber)
        {
            chassisManagementSession = reconnected;
        }

        if (ReferenceEquals(activeRuntime, runtime))
        {
            session = reconnected;
            sessionLifetime = runtime.Lifetime;
            frameConsumer = runtime.FrameConsumer;
            diagnosticsConsumer = runtime.DiagnosticsConsumer;
            bitmap = null;
            latestFrame = null;
            connectionFailed = false;
            RemoteImage.Source = null;
            ViewerOverlay.Visibility = Visibility.Visible;
            ApplySessionPermissions(reconnected.Permissions);
            SetStatus(
                mediaRestoreFailure is null
                    ? "KVM 连接已自动恢复"
                    : LocalizationManager.Format(
                        "KVM 已恢复，但虚拟介质恢复失败：{0}",
                        mediaRestoreFailure.Message),
                mediaRestoreFailure is null ? InputReadyBrush : Brushes.Goldenrod);
            UpdateRemoteInputStatus();
        }
    }

    private void DetachSessionEvents(KvmClientSession sourceSession)
    {
        sourceSession.VideoSettingsChanged -= Session_VideoSettingsChanged;
        sourceSession.RemoteLockKeysChanged -= Session_RemoteLockKeysChanged;
        sourceSession.PermissionsChanged -= Session_PermissionsChanged;
        sourceSession.PrivilegeDenied -= Session_PrivilegeDenied;
    }

    private void SessionSupervisor_ProgressChanged(object? sender, KvmReconnectProgress progress)
    {
        var message = progress.State switch
        {
            "connecting" => LocalizationManager.Format(
                "正在恢复 KVM（{0}/{1}）",
                progress.Attempt,
                progress.MaximumAttempts),
            "restoring-media" => "KVM 已连接，正在恢复虚拟介质",
            "retrying" => LocalizationManager.Format(
                "恢复失败，准备重试（{0}/{1}）",
                progress.Attempt,
                progress.MaximumAttempts),
            "connected" => "KVM 恢复成功",
            _ => "正在恢复 KVM",
        };
        _ = Dispatcher.InvokeAsync(() => SetStatus(message, Brushes.Goldenrod));
    }

    private void ConsoleRoot_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.GetPosition(ConsoleRoot).Y <= 112)
        {
            var transition = toolbarState.Reveal();
            if (transition != FloatingToolbarTransition.None)
            {
                ApplyToolbarState(transition);
            }

            toolbarHideTimer.Stop();
        }
    }

    private void ToolbarRevealZone_MouseEnter(object sender, MouseEventArgs e)
    {
        var transition = toolbarState.Reveal();
        if (transition != FloatingToolbarTransition.None)
        {
            ApplyToolbarState(transition);
        }

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
        var transition = toolbarState.HideAfterPointerLeaves(FloatingToolbar.IsMouseOver);
        if (transition != FloatingToolbarTransition.None)
        {
            ApplyToolbarState(transition);
        }
    }

    private void ApplyToolbarState(FloatingToolbarTransition transition = FloatingToolbarTransition.None)
    {
        PinToolbarButton.IsChecked = toolbarState.IsPinned;
        PinToolbarButton.ToolTip = LocalizationManager.Translate(toolbarState.IsPinned
            ? "取消固定，鼠标离开后自动隐藏"
            : "固定工具栏，始终显示");

        if (transition == FloatingToolbarTransition.None || !SystemParameters.ClientAreaAnimation)
        {
            ApplyToolbarStateImmediately();
            return;
        }

        if (transition == FloatingToolbarTransition.Show)
        {
            AnimateToolbarShow();
        }
        else
        {
            AnimateToolbarHide();
        }
    }

    private void ApplyToolbarStateImmediately()
    {
        toolbarAnimationVersion++;
        FloatingToolbar.BeginAnimation(OpacityProperty, null);
        ToolbarTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        ToolbarHiddenHandle.BeginAnimation(OpacityProperty, null);
        FloatingToolbar.Visibility = toolbarState.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        FloatingToolbar.IsHitTestVisible = toolbarState.IsVisible;
        FloatingToolbar.Opacity = toolbarState.IsVisible ? 1 : 0;
        ToolbarTranslateTransform.Y = toolbarState.IsVisible ? 0 : -10;
        ToolbarHiddenHandle.Visibility = toolbarState.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        ToolbarHiddenHandle.Opacity = toolbarState.IsVisible ? 0 : 1;
    }

    private void AnimateToolbarShow()
    {
        var version = ++toolbarAnimationVersion;
        FloatingToolbar.Visibility = Visibility.Visible;
        FloatingToolbar.IsHitTestVisible = true;
        FloatingToolbar.Opacity = 0;
        ToolbarTranslateTransform.Y = -10;

        ToolbarHiddenHandle.BeginAnimation(OpacityProperty, null);
        ToolbarHiddenHandle.Visibility = Visibility.Collapsed;
        ToolbarHiddenHandle.Opacity = 0;

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(1, ToolbarAnimationDuration) { EasingFunction = easing };
        var slide = new DoubleAnimation(0, ToolbarAnimationDuration) { EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            if (version != toolbarAnimationVersion || !toolbarState.IsVisible)
            {
                return;
            }

            FloatingToolbar.BeginAnimation(OpacityProperty, null);
            ToolbarTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            FloatingToolbar.Opacity = 1;
            ToolbarTranslateTransform.Y = 0;
        };
        FloatingToolbar.BeginAnimation(OpacityProperty, fade);
        ToolbarTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void AnimateToolbarHide()
    {
        var version = ++toolbarAnimationVersion;
        FloatingToolbar.IsHitTestVisible = false;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, ToolbarAnimationDuration) { EasingFunction = easing };
        var slide = new DoubleAnimation(-10, ToolbarAnimationDuration) { EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            if (version != toolbarAnimationVersion || toolbarState.IsVisible)
            {
                return;
            }

            FloatingToolbar.Visibility = Visibility.Collapsed;
            FloatingToolbar.BeginAnimation(OpacityProperty, null);
            ToolbarTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            FloatingToolbar.Opacity = 0;
            ToolbarTranslateTransform.Y = -10;

            ToolbarHiddenHandle.Visibility = Visibility.Visible;
            ToolbarHiddenHandle.Opacity = 0;
            ToolbarHiddenHandle.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
        };
        FloatingToolbar.BeginAnimation(OpacityProperty, fade);
        ToolbarTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void PowerMenuButton_Click(object sender, RoutedEventArgs e)
        => ContextMenuButton_Click(sender, e);

    private void HelpButton_Click(object sender, RoutedEventArgs e) =>
        new HelpWindow { Owner = this }.Show();

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (recorder is not null || aviRecorder is not null)
        {
            await StopRecordingAsync();
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = LocalizationManager.Translate(
                "原厂录像 (*.rep)|*.rep|Motion JPEG AVI (*.avi)|*.avi|所有文件 (*.*)|*.*"),
            DefaultExt = ".rep",
            AddExtension = true,
            FileName = $"ibmc-{DateTime.Now:yyyyMMdd-HHmmss}.rep",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var exportAvi = string.Equals(
            Path.GetExtension(dialog.FileName),
            ".avi",
            StringComparison.OrdinalIgnoreCase);
        if (exportAvi && latestFrame is null)
        {
            SetStatus("收到第一帧视频后才能开始 AVI 录像", Brushes.Goldenrod);
            return;
        }

        try
        {
            var stream = new FileStream(
                dialog.FileName,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read,
                64 * 1024,
                useAsync: true);
            if (exportAvi)
            {
                Volatile.Write(
                    ref aviRecorder,
                    new AviConsoleRecorder(stream, latestFrame!.Width, latestFrame.Height));
            }
            else
            {
                Volatile.Write(ref recorder, new ConsoleRecorder(new RepRecordingWriter(stream)));
            }

            await session!.StartRecordingAsync();
            RecordButton.ToolTip = LocalizationManager.Translate("停止本地录像");
            SetStatus("正在录像", InputReadyBrush);
        }
        catch (Exception exception)
        {
            await StopRecordingAsync();
            SetStatus(LocalizationManager.Format("录像启动失败：{0}", exception.Message), InputFailedBrush);
        }
    }

    private async Task StopRecordingAsync()
    {
        var activeRecorder = Interlocked.Exchange(ref recorder, null);
        var activeAviRecorder = Interlocked.Exchange(ref aviRecorder, null);
        if (activeRecorder is null && activeAviRecorder is null)
        {
            return;
        }

        try
        {
            try
            {
                if (session is not null)
                {
                    await session.StopRecordingAsync();
                }
            }
            finally
            {
                if (activeRecorder is not null)
                {
                    await activeRecorder.DisposeAsync();
                }

                if (activeAviRecorder is not null)
                {
                    await activeAviRecorder.DisposeAsync();
                }
            }

            var droppedFrames = (activeRecorder?.DroppedFrames ?? 0) +
                                (activeAviRecorder?.DroppedFrames ?? 0);
            var failure = activeRecorder?.Failure ?? activeAviRecorder?.Failure;
            SetStatus(
                droppedFrames == 0
                    ? "录像已保存"
                    : LocalizationManager.Format("录像已保存，丢弃 {0} 帧", droppedFrames),
                failure is null ? InputReadyBrush : Brushes.Goldenrod);
        }
        catch (Exception exception)
        {
            SetStatus(LocalizationManager.Format("录像停止失败：{0}", exception.Message), InputFailedBrush);
        }
        finally
        {
            RecordButton.ToolTip = LocalizationManager.Translate("开始本地录像");
        }
    }

    private async void MouseModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var activeSession = session;
        if (activeSession is null || sender is not MenuItem { Tag: string modeTag })
        {
            return;
        }

        var selectedMode = modeTag switch
        {
            "Relative" => ConsolePointerMode.Relative,
            "Captured" => ConsolePointerMode.Captured,
            _ => ConsolePointerMode.Absolute,
        };
        if (pointerState.Mode == selectedMode)
        {
            UpdateConsoleSettingsPresentation(activeSession);
            return;
        }

        var previousMode = pointerState.Mode;
        var protocolMode = selectedMode == ConsolePointerMode.Absolute
            ? KvmMouseMode.Absolute
            : KvmMouseMode.Relative;

        MouseModeButton.IsEnabled = false;
        try
        {
            await ReleaseRemoteInputAsync();
            if (activeSession.CurrentMouseMode != protocolMode)
            {
                await activeSession.SetMouseModeAsync(protocolMode);
            }

            pointerState.SetMode(selectedMode);
            ApplyLocalPointerCursor();
            lastRelativePoint = null;
            hasLastMousePosition = false;
            UpdateConsoleSettingsPresentation(activeSession);
            SetStatus(
                selectedMode switch
                {
                    ConsolePointerMode.Captured => "已切换到捕获鼠标；单击画面捕获，按 Esc 释放",
                    ConsolePointerMode.Relative => "已切换到相对鼠标",
                    _ => "已切换到绝对鼠标",
                },
                InputReadyBrush);
        }
        catch (Exception exception)
        {
            SetStatus(LocalizationManager.Format("鼠标模式切换失败：{0}", exception.Message), InputFailedBrush);
            pointerState.SetMode(previousMode);
            UpdateConsoleSettingsPresentation(activeSession);
            ApplyLocalPointerCursor();
        }
        finally
        {
            if (ReferenceEquals(session, activeSession))
            {
                ApplySessionPermissions(activeSession.Permissions);
            }
        }
    }

    private void ShowLocalPointerButton_Click(object sender, RoutedEventArgs e)
    {
        pointerState.SetShowLocalPointer(ShowLocalPointerButton.IsChecked == true);
        ShowLocalPointerButton.ToolTip = LocalizationManager.Translate(pointerState.ShowLocalPointer
            ? "隐藏本地鼠标指针"
            : "显示本地鼠标指针");
        ApplyLocalPointerCursor();
    }

    private async void SynchronizeMouseButton_Click(object sender, RoutedEventArgs e)
    {
        var activeSession = session;
        if (activeSession is null)
        {
            return;
        }

        SynchronizeMouseButton.IsEnabled = false;
        try
        {
            await activeSession.SynchronizeMouseAsync();
            lastRelativePoint = null;
            if (pointerState.IsCaptureActive)
            {
                CenterCapturedPointer();
            }

            SetStatus("远端鼠标同步命令已发送", InputReadyBrush);
        }
        catch (Exception exception)
        {
            SetStatus(LocalizationManager.Format("鼠标同步失败：{0}", exception.Message), InputFailedBrush);
        }
        finally
        {
            SynchronizeMouseButton.IsEnabled = activeSession.Permissions.CanControlKvm;
        }
    }

    private void ActivateCapturedPointer()
    {
        if (pointerState.Mode != ConsolePointerMode.Captured || pointerState.IsCaptureActive)
        {
            return;
        }

        if (Mouse.Capture(VideoHost, CaptureMode.Element) && pointerState.BeginCapture())
        {
            lastRelativePoint = null;
            ApplyLocalPointerCursor();
            CenterCapturedPointer();
            SetStatus("鼠标已捕获；按 Esc 释放", InputReadyBrush);
        }
    }

    private void ReleaseCapturedPointer()
    {
        pointerState.ReleaseCapture();
        Mouse.Capture(null);
        lastRelativePoint = null;
        ApplyLocalPointerCursor();
    }

    private void CenterCapturedPointer()
    {
        if (!pointerState.IsCaptureActive || VideoHost.ActualWidth <= 0 || VideoHost.ActualHeight <= 0)
        {
            return;
        }

        CursorPositionService.TryMoveTo(VideoHost.PointToScreen(
            new Point(VideoHost.ActualWidth / 2, VideoHost.ActualHeight / 2)));
    }

    private void ApplyLocalPointerCursor() =>
        VideoHost.Cursor = pointerState.IsLocalPointerVisible ? Cursors.Arrow : Cursors.None;

    private void UpdateConsoleSettingsPresentation(KvmClientSession sourceSession)
    {
        var mouseModeTag = pointerState.Mode.ToString();
        var mouseModeLabel = pointerState.Mode switch
        {
            ConsolePointerMode.Relative => LocalizationManager.Translate("相对"),
            ConsolePointerMode.Captured => LocalizationManager.Translate("捕获"),
            _ => LocalizationManager.Translate("绝对"),
        };
        UpdateMenuSelection(MouseModeButton.ContextMenu, mouseModeTag);
        MouseModeButton.ToolTip = FormatSettingToolTip("鼠标模式", mouseModeLabel);

        var qualityOptions = ConsoleVideoSettings.QualityOptions;
        var qualityIndex = ConsoleVideoSettings.FindIndex(qualityOptions, sourceSession.CurrentVideoQuality);
        var qualityLabel = qualityIndex >= 0
            ? qualityOptions[qualityIndex].Label
            : sourceSession.CurrentVideoQuality.ToString(CultureInfo.InvariantCulture);
        UpdateMenuSelection(
            VideoQualityButton.ContextMenu,
            sourceSession.CurrentVideoQuality.ToString(CultureInfo.InvariantCulture));
        VideoQualityButton.ToolTip = FormatSettingToolTip("图像清晰度", qualityLabel);

        var depthOption = ConsoleVideoSettings.ColorDepthOptions.FirstOrDefault(
            option => option.Value == sourceSession.CurrentColorDepth);
        var depthLabel = string.IsNullOrEmpty(depthOption.Label)
            ? sourceSession.CurrentColorDepth.ToString(CultureInfo.InvariantCulture)
            : depthOption.Label;
        UpdateMenuSelection(
            ColorDepthButton.ContextMenu,
            sourceSession.CurrentColorDepth.ToString(CultureInfo.InvariantCulture),
            tag => byte.TryParse(tag, out var depth) && sourceSession.Capabilities.ColorDepths.Contains(depth));
        ColorDepthButton.ToolTip = FormatSettingToolTip("颜色位数", depthLabel);
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

    private static string FormatSettingToolTip(string setting, string value) =>
        $"{LocalizationManager.Translate(setting)}：{value}";

    private async void VideoQualityMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (session is null || sender is not MenuItem { Tag: string qualityTag } ||
            !byte.TryParse(qualityTag, out var quality))
        {
            return;
        }

        var activeSession = session;
        if (activeSession.CurrentVideoQuality == quality)
        {
            UpdateConsoleSettingsPresentation(activeSession);
            return;
        }

        VideoQualityButton.IsEnabled = false;
        try
        {
            await activeSession.SetVideoQualityAsync(quality, committed: true);
            if (ReferenceEquals(session, activeSession))
            {
                UpdateConsoleSettingsPresentation(activeSession);
            }

            SetStatus(LocalizationManager.Format("图像清晰度已设为 {0}", quality), InputReadyBrush);
        }
        catch (Exception exception)
        {
            SetStatus(LocalizationManager.Format("图像清晰度设置失败：{0}", exception.Message), InputFailedBrush);
            if (ReferenceEquals(session, activeSession))
            {
                UpdateConsoleSettingsPresentation(activeSession);
            }
        }
        finally
        {
            if (ReferenceEquals(session, activeSession))
            {
                ApplySessionPermissions(activeSession.Permissions);
            }
        }
    }

    private async void ColorDepthMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (session is null || sender is not MenuItem { Tag: string depthTag } ||
            !byte.TryParse(depthTag, out var depth))
        {
            return;
        }

        var activeSession = session;
        if (!activeSession.Capabilities.ColorDepths.Contains(depth) || activeSession.CurrentColorDepth == depth)
        {
            UpdateConsoleSettingsPresentation(activeSession);
            return;
        }

        ColorDepthButton.IsEnabled = false;
        try
        {
            await activeSession.SetColorDepthAsync(depth);
            if (ReferenceEquals(session, activeSession))
            {
                UpdateConsoleSettingsPresentation(activeSession);
            }

            SetStatus(
                LocalizationManager.Format(
                    "颜色位数已设为 {0}",
                    ConsoleVideoSettings.ColorDepthOptions.First(option => option.Value == depth).Label),
                InputReadyBrush);
        }
        catch (Exception exception)
        {
            SetStatus(LocalizationManager.Format("颜色位数设置失败：{0}", exception.Message), InputFailedBrush);
            if (ReferenceEquals(session, activeSession))
            {
                UpdateConsoleSettingsPresentation(activeSession);
            }
        }
        finally
        {
            if (ReferenceEquals(session, activeSession))
            {
                ApplySessionPermissions(activeSession.Permissions);
            }
        }
    }

    private void Session_VideoSettingsChanged(object? sender, EventArgs e)
    {
        if (sender is not KvmClientSession changedSession)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(session, changedSession))
            {
                return;
            }

            UpdateConsoleSettingsPresentation(changedSession);
        });
    }

    private async Task RequestRemoteLockKeysAsync(
        KvmClientSession sourceSession,
        CancellationToken cancellationToken)
    {
        try
        {
            await sourceSession.RequestKeyboardStateAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
                SetStatus(LocalizationManager.Format("远端锁定键状态查询失败：{0}", exception.Message), Brushes.Goldenrod));
        }
    }

    private async Task RefreshRemoteLockKeysAfterInputAsync(
        KvmClientSession sourceSession,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!ReferenceEquals(session, sourceSession) || connectionFailed)
        {
            return;
        }

        await RequestRemoteLockKeysAsync(sourceSession, cancellationToken);
    }

    private void Session_RemoteLockKeysChanged(object? sender, EventArgs e)
    {
        if (sender is not KvmClientSession changedSession)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(session, changedSession))
            {
                UpdateRemoteLockIndicators(changedSession.RemoteLockKeys);
            }
        });
    }

    private void Session_PermissionsChanged(object? sender, EventArgs e)
    {
        if (sender is not KvmClientSession changedSession)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(session, changedSession))
            {
                ApplySessionPermissions(changedSession.Permissions);
            }
        });
    }

    private void Session_PrivilegeDenied(object? sender, KvmPrivilegeDeniedEventArgs e)
    {
        var message = e.Operation switch
        {
            KvmPrivilegeOperation.Power => "当前账户没有执行电源操作的权限。",
            KvmPrivilegeOperation.VirtualMedia => "当前账户没有使用虚拟软驱或虚拟光驱的权限。",
            _ => LocalizationManager.Format("iBMC 拒绝了操作（状态 {0}）。", e.State),
        };
        _ = Dispatcher.InvokeAsync(() =>
        {
            SetStatus(message, InputFailedBrush);
            if (e.Operation == KvmPrivilegeOperation.VirtualMedia)
            {
                virtualMediaWindow?.Close();
            }

            MessageBox.Show(
                this,
                LocalizationManager.Translate(message),
                LocalizationManager.Translate("权限不足"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        });
    }

    private void ApplySessionPermissions(KvmSessionPermissions permissions)
    {
        if (session is null)
        {
            return;
        }

        var availability = ConsolePermissionUiRules.Resolve(permissions, session.Capabilities);
        MouseModeButton.IsEnabled = availability.MouseMode;
        ShowLocalPointerButton.IsEnabled = availability.Input;
        SynchronizeMouseButton.IsEnabled = availability.Input;
        VideoQualityButton.IsEnabled = availability.VideoQuality;
        ColorDepthButton.IsEnabled = availability.ColorDepth;
        RecordButton.IsEnabled = availability.Recording;
        KeyboardMenuButton.IsEnabled = availability.Keyboard;
        ReleaseKeysButton.IsEnabled = availability.Keyboard;
        PowerMenuButton.IsEnabled = availability.Power;
        VirtualMediaButton.IsEnabled = availability.VirtualMedia;
        PowerMenuButton.ToolTip = LocalizationManager.Translate(
            availability.Power ? "电源操作" : "当前账户没有电源控制权限");
        VirtualMediaButton.ToolTip = LocalizationManager.Translate(availability.VirtualMedia
            ? "虚拟软驱与虚拟光驱"
            : "当前账户没有虚拟介质权限");
        UpdateRemoteInputStatus();
    }

    private void UpdateRemoteLockIndicators(RemoteLockKeys state)
    {
        NumLockIndicator.Fill = state.HasFlag(RemoteLockKeys.NumLock) ? RemoteLockOnBrush : RemoteLockOffBrush;
        CapsLockIndicator.Fill = state.HasFlag(RemoteLockKeys.CapsLock) ? RemoteLockOnBrush : RemoteLockOffBrush;
        ScrollLockIndicator.Fill = state.HasFlag(RemoteLockKeys.ScrollLock) ? RemoteLockOnBrush : RemoteLockOffBrush;
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
        if (activeRuntime is { } runtime &&
            !ChassisUiState.CanRouteInput(runtime.Mode, chassisCoordinator.IsSplitViewEnabled))
        {
            return RemoteInputState.ConnectedInactive;
        }

        if (session is { Permissions.CanControlKvm: false })
        {
            return RemoteInputState.ConnectedInactive;
        }

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
        var presentation = nextState switch
        {
            RemoteInputState.Disconnected => (InputDisconnectedBrush, "未连接"),
            RemoteInputState.ConnectionFailed => (InputFailedBrush, "连接失败"),
            RemoteInputState.Ready => (InputReadyBrush, "输入已启用"),
            _ => (InputInactiveBrush, GetInactiveInputStatus()),
        };
        (InputStatusDot.Fill, InputStatusText.Text) =
            (presentation.Item1, LocalizationManager.Translate(presentation.Item2));

        if (shouldRelease)
        {
            _ = ReleaseRemoteInputAsync();
        }
    }

    private string GetInactiveInputStatus()
    {
        if (chassisCoordinator.IsSplitViewEnabled)
        {
            return "分屏视图为只读；关闭分屏后可向选中刀片输入";
        }

        if (latestFrame is null)
        {
            return "已连接，等待视频";
        }

        if (activeRuntime?.Mode == KvmBladeSessionMode.Monitor)
        {
            return "当前刀片为只读监视会话";
        }

        if (session is { Permissions.CanControlKvm: false })
        {
            return "当前账户没有 KVM 控制权限";
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
        if (activeRuntime is not null)
        {
            activeRuntime.ConnectionFailed = true;
        }
        SetStatus(
            LocalizationManager.Format("{0}：{1}", LocalizationManager.Translate(header), exception.Message),
            InputFailedBrush);
        UpdateRemoteInputStatus();
    }

    private void SetStatus(string message, Brush accent)
    {
        StatusMessageText.Text = LocalizationManager.Translate(message);
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

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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

        public WriteableBitmap? Bitmap { get; set; }

        public EncodedVideoFrame? LatestFrame { get; set; }

        public Image? SplitImage { get; set; }

        public Stopwatch FrameClock { get; } = Stopwatch.StartNew();

        public int RenderedFrames { get; set; }

        public bool ConnectionFailed { get; set; }

        public async Task AwaitConsumersAsync()
        {
            var tasks = new[] { FrameConsumer, DiagnosticsConsumer }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            try
            {
                await Task.WhenAll(tasks);
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

}
