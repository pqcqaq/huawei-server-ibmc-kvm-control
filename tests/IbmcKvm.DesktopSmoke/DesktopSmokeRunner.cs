using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using IbmcKvm.App;
using IbmcKvm.Core.Session;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.DesktopSmoke;

internal sealed class DesktopSmokeRunner(Application application, string outputDirectory)
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };

    private static readonly string[] RequiredConsoleAutomationIds =
    [
        "MouseModeComboBox",
        "ShowLocalPointerButton",
        "SynchronizeMouseButton",
        "VideoQualityComboBox",
        "ColorDepthComboBox",
        "VirtualMediaButton",
        "KeyboardMenuButton",
        "PowerMenuButton",
        "DisconnectButton",
    ];

    private readonly List<string> checks = [];
    private readonly List<SmokeScenarioEvidence> scenarios = [];

    public async Task RunAsync()
    {
        await RunAdminControlsAsync();
        await RunUserPermissionsAsync();
        await RunReconnectSuccessAsync();
        await RunReconnectFailureAsync();
        var report = new DesktopSmokeReport(
            DateTimeOffset.Now,
            Environment.OSVersion.VersionString,
            scenarios,
            checks);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "report.json"),
            JsonSerializer.Serialize(report, ReportJsonOptions));
        Console.WriteLine($"Desktop smoke verification passed: {checks.Count} checks across {scenarios.Count} scenarios.");
        Console.WriteLine(Path.Combine(outputDirectory, "report.json"));
    }

    private async Task RunAdminControlsAsync()
    {
        await using var server = new LoopbackKvmServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var session = await ConnectAsync(server, KvmPrivilegeLevel.Administrator, timeout.Token);
        var window = ShowConsole(session, "loopback-admin");
        var captures = new List<WindowCaptureEvidence>();
        try
        {
            var videoHost = FindNamed<Grid>(window, "VideoHost");
            var remoteImage = FindNamed<Image>(window, "RemoteImage");
            var status = FindNamed<TextBlock>(window, "StatusMessageText");
            var inputStatus = FindNamed<TextBlock>(window, "InputStatusText");
            var mouseMode = FindNamed<ComboBox>(window, "MouseModeComboBox");
            var showPointer = FindNamed<ToggleButton>(window, "ShowLocalPointerButton");
            var synchronizeMouse = FindNamed<Button>(window, "SynchronizeMouseButton");
            var videoQuality = FindNamed<ComboBox>(window, "VideoQualityComboBox");
            var colorDepth = FindNamed<ComboBox>(window, "ColorDepthComboBox");
            var keyboardButton = FindNamed<Button>(window, "KeyboardMenuButton");
            var powerButton = FindNamed<Button>(window, "PowerMenuButton");
            var virtualMediaButton = FindNamed<Button>(window, "VirtualMediaButton");

            await WaitForAsync(() => remoteImage.Source is not null, "the first loopback video frame", timeout.Token);
            await ActivateViewerAsync(window, videoHost, inputStatus, timeout.Token);
            Check(powerButton.IsEnabled && virtualMediaButton.IsEnabled, "Administrator power and media controls are enabled.");

            var inspection = await DesktopAutomation.InspectWindowAsync(
                DesktopCapture.GetHandle(window),
                RequiredConsoleAutomationIds,
                timeout.Token);
            Check(inspection.MissingAutomationIds.Count == 0, "UI Automation resolves every required console control.");
            Check(inspection.OutsideInteractiveControls.Count == 0, "Visible interactive console controls stay inside the window.");
            Check(inspection.InteractiveControlCount >= 12, "UI Automation exposes the console toolbar controls.");

            mouseMode.SelectedIndex = 1;
            await WaitForAsync(
                () => session.CurrentMouseMode == KvmMouseMode.Relative && mouseMode.IsEnabled,
                "relative mouse selection",
                timeout.Token);
            Check(
                server.Commands.Any(static payload => payload.SequenceEqual(new byte[] { 0x24, 0, 1, 0, 0 })),
                "Relative mouse selection sends command 0x24.");

            mouseMode.SelectedIndex = 2;
            await WaitForAsync(
                () => status.Text.Contains("捕获鼠标", StringComparison.Ordinal),
                "captured mouse selection",
                timeout.Token);
            await ActivateViewerAsync(window, videoHost, inputStatus, timeout.Token);
            var clickPoint = videoHost.PointToScreen(new Point(videoHost.ActualWidth / 2, videoHost.ActualHeight / 2));
            DesktopAutomation.ClickAt((int)Math.Round(clickPoint.X), (int)Math.Round(clickPoint.Y));
            await WaitForAsync(
                () => status.Text.Contains("鼠标已捕获", StringComparison.Ordinal),
                "captured pointer activation",
                timeout.Token);
            Check(ReferenceEquals(videoHost.Cursor, Cursors.None), "Captured mouse hides the local pointer.");
            DesktopAutomation.PressEscape();
            await WaitForAsync(
                () => status.Text.Contains("已释放捕获鼠标", StringComparison.Ordinal),
                "Escape pointer release",
                timeout.Token);
            Check(ReferenceEquals(videoHost.Cursor, Cursors.Arrow), "Escape releases capture and restores the pointer.");

            showPointer.IsChecked = false;
            RaiseClick(showPointer);
            Check(ReferenceEquals(videoHost.Cursor, Cursors.None), "The local-pointer toggle hides the pointer.");
            showPointer.IsChecked = true;
            RaiseClick(showPointer);
            Check(ReferenceEquals(videoHost.Cursor, Cursors.Arrow), "The local-pointer toggle restores the pointer.");

            RaiseClick(synchronizeMouse);
            await WaitForAsync(
                () => status.Text.Contains("同步命令已发送", StringComparison.Ordinal),
                "relative mouse synchronization",
                timeout.Token);
            await WaitForAsync(
                () => server.Commands.Count(static payload =>
                    payload.SequenceEqual(new byte[] { 0x05, 1, 0, 0x81, 0x81, 0 })) >= 15,
                "fifteen received mouse synchronization reports",
                timeout.Token);
            Check(
                server.Commands.Count(static payload =>
                    payload.SequenceEqual(new byte[] { 0x05, 1, 0, 0x81, 0x81, 0 })) >= 15,
                "Relative synchronization sends fifteen source-compatible reports.");

            videoQuality.SelectedValue = (byte)60;
            await WaitForAsync(
                () => session.CurrentVideoQuality == 60 && videoQuality.IsEnabled,
                "DQT quality confirmation",
                timeout.Token);
            Check(
                server.Commands.Any(static payload =>
                    payload.Length >= 5 && payload[0] == 0x27 && payload[2] == 70 && payload[3] == 1),
                "The DQT selector sends and confirms command 0x27/0x28.");

            colorDepth.SelectedValue = (byte)2;
            await WaitForAsync(
                () => session.CurrentColorDepth == 2 && colorDepth.IsEnabled,
                "six-bit color selection",
                timeout.Token);
            Check(
                server.Commands.Any(static payload => payload.SequenceEqual(new byte[] { 0x1B, 1, 2 })),
                "The color selector sends the six-bit command.");

            var numLock = FindNamed<System.Windows.Shapes.Ellipse>(window, "NumLockIndicator");
            var capsLock = FindNamed<System.Windows.Shapes.Ellipse>(window, "CapsLockIndicator");
            var scrollLock = FindNamed<System.Windows.Shapes.Ellipse>(window, "ScrollLockIndicator");
            await WaitForAsync(
                () => HasColor(numLock.Fill, Color.FromRgb(240, 198, 116)),
                "remote lock-state response",
                timeout.Token);
            Check(
                HasColor(capsLock.Fill, Color.FromRgb(89, 99, 95)) &&
                HasColor(scrollLock.Fill, Color.FromRgb(240, 198, 116)),
                "Remote Num/Scroll indicators turn on while Caps remains off.");
            Check(
                mouseMode.SelectedIndex == 2 && Equals(videoQuality.SelectedValue, (byte)60) &&
                Equals(colorDepth.SelectedValue, (byte)2),
                "Mouse, DQT, and color selections are visible in the console.");
            await ActivateViewerAsync(window, videoHost, inputStatus, timeout.Token);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(100, timeout.Token);
            captures.Add(DesktopCapture.Save150Percent(window, outputDirectory, "admin-controls-150.png"));

            var keyboardItems = EnumerateMenuItems(keyboardButton.ContextMenu).ToArray();
            foreach (var preset in new[] { "CtrlShift", "CtrlEscape", "CtrlAltDelete", "AltTab", "CtrlSpace", "KeyboardReset" })
            {
                Check(
                    keyboardItems.Any(item => string.Equals(item.Tag as string, preset, StringComparison.Ordinal)),
                    $"The keyboard menu exposes {preset}.");
            }

            Check(
                keyboardItems.Any(item => Equals(item.Header, "自定义组合键…")),
                "The keyboard menu exposes the custom combination editor.");
            var japaneseLayout = keyboardItems.Single(item => string.Equals(item.Tag as string, "Japanese", StringComparison.Ordinal));
            RaiseClick(japaneseLayout);
            await WaitForAsync(() => japaneseLayout.IsChecked, "Japanese keyboard layout selection", timeout.Token);
            Check(status.Text.Contains("日本語", StringComparison.Ordinal), "The desktop reports the selected Japanese layout.");

            var customWindow = new CustomKeyCombinationWindow { Owner = window };
            customWindow.Show();
            await WaitForAsync(() => customWindow.IsVisible, "custom key window", timeout.Token);
            for (var index = 1; index <= 6; index++)
            {
                var selector = FindNamed<ComboBox>(customWindow, $"Key{index}ComboBox");
                Check(selector.Items.Count > 10, $"Custom key slot {index} exposes HID choices.");
                Check(IsInsideWindow(customWindow, selector), $"Custom key slot {index} is visible inside the window.");
            }

            var customInspection = await DesktopAutomation.InspectWindowAsync(
                DesktopCapture.GetHandle(customWindow),
                Enumerable.Range(1, 6).Select(static index => $"Key{index}ComboBox").ToArray(),
                timeout.Token);
            Check(customInspection.MissingAutomationIds.Count == 0, "UI Automation resolves all six custom key slots.");
            Check(customInspection.OutsideInteractiveControls.Count == 0, "Custom-key controls stay inside the window.");

            captures.Add(DesktopCapture.Save150Percent(customWindow, outputDirectory, "admin-custom-keys-150.png"));
            customWindow.Close();
            await WaitForAsync(() => !customWindow.IsVisible, "custom key window close", timeout.Token);
            await Task.Delay(100, timeout.Token);

            var powerItems = EnumerateMenuItems(powerButton.ContextMenu).ToArray();
            foreach (var powerAction in new[] { "开机", "正常关机", "重启", "强制断电重启", "强制关机" })
            {
                Check(
                    powerItems.Any(item => Equals(item.Header, powerAction)),
                    $"The power menu exposes {powerAction} without invoking it.");
            }

            Check(
                server.Commands.All(static payload => !IsPowerCommand(payload[0])),
                "Desktop inspection emits no power or USB-reset command.");

            var ctrlAltDelete = keyboardItems.Single(item => string.Equals(item.Tag as string, "CtrlAltDelete", StringComparison.Ordinal));
            RaiseClick(ctrlAltDelete);
            await WaitForAsync(
                () => server.Commands.Any(static payload =>
                    payload.SequenceEqual(new byte[] { 0x03, 1, 5, 0, 0x4C, 0, 0, 0, 0, 0 })),
                "Ctrl+Alt+Delete report",
                timeout.Token);
            Check(
                server.Commands.Any(static payload =>
                    payload.SequenceEqual(new byte[] { 0x03, 1, 0, 0, 0, 0, 0, 0, 0, 0 })),
                "The special-key preset always sends its release report.");

            Check(
                mouseMode.SelectedIndex == 2 && Equals(videoQuality.SelectedValue, (byte)60) &&
                Equals(colorDepth.SelectedValue, (byte)2),
                "Mouse, DQT, and color selections remain visible after keyboard dialogs.");

            scenarios.Add(new SmokeScenarioEvidence(
                "admin-controls",
                captures,
                inspection.InteractiveControlCount,
                server.Commands.Count));
        }
        finally
        {
            await CloseConsoleAsync(window, session);
            server.ThrowIfFailed();
        }
    }

    private async Task RunUserPermissionsAsync()
    {
        await using var server = new LoopbackKvmServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var session = await ConnectAsync(server, KvmPrivilegeLevel.User, timeout.Token);
        var window = ShowConsole(session, "loopback-user");
        try
        {
            await WaitForAsync(
                () => FindNamed<Image>(window, "RemoteImage").Source is not null,
                "user loopback video",
                timeout.Token);
            var power = FindNamed<Button>(window, "PowerMenuButton");
            var media = FindNamed<Button>(window, "VirtualMediaButton");
            var keyboard = FindNamed<Button>(window, "KeyboardMenuButton");
            var mouse = FindNamed<ComboBox>(window, "MouseModeComboBox");
            Check(!power.IsEnabled, "User privilege disables the unavailable power control.");
            Check(
                media.IsEnabled && keyboard.IsEnabled && mouse.IsEnabled,
                "User privilege retains permitted KVM input and virtual-media controls.");
            Check(
                Equals(power.ToolTip, "当前账户没有电源控制权限") &&
                Equals(media.ToolTip, "虚拟软驱与虚拟光驱"),
                "Privilege-aware tooltips distinguish denied and available controls.");
            Check(
                server.Commands.All(static payload => !IsPowerCommand(payload[0])),
                "User-permission inspection emits no power or USB-reset command.");

            var inspection = await DesktopAutomation.InspectWindowAsync(
                DesktopCapture.GetHandle(window),
                RequiredConsoleAutomationIds,
                timeout.Token);
            Check(inspection.MissingAutomationIds.Count == 0, "UI Automation resolves user-console controls.");
            Check(inspection.OutsideInteractiveControls.Count == 0, "User-console controls stay inside the window.");
            var captures = new[]
            {
                DesktopCapture.Save150Percent(window, outputDirectory, "user-permissions-150.png"),
            };
            scenarios.Add(new SmokeScenarioEvidence(
                "user-permissions",
                captures,
                inspection.InteractiveControlCount,
                server.Commands.Count));
        }
        finally
        {
            await CloseConsoleAsync(window, session);
            server.ThrowIfFailed();
        }
    }

    private async Task RunReconnectSuccessAsync()
    {
        await using var server = new LoopbackKvmServer(LoopbackFailureMode.ReconnectSucceeds);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = await ConnectAsync(server, KvmPrivilegeLevel.Administrator, timeout.Token);
        var window = ShowConsole(session, "loopback-reconnect-success");
        try
        {
            var remoteImage = FindNamed<Image>(window, "RemoteImage");
            var status = FindNamed<TextBlock>(window, "StatusMessageText");
            await WaitForAsync(() => remoteImage.Source is not null, "initial reconnect video", timeout.Token);
            await server.TriggerFailureAsync();
            await WaitForAsync(
                () => status.Text.Contains("恢复", StringComparison.Ordinal),
                "visible reconnect progress",
                timeout.Token);
            var captures = new List<WindowCaptureEvidence>
            {
                DesktopCapture.Save150Percent(window, outputDirectory, "reconnect-progress-150.png"),
            };
            await WaitForAsync(
                () => status.Text.Contains("KVM 连接已自动恢复", StringComparison.Ordinal),
                "successful desktop reconnect",
                timeout.Token);
            Check(server.ConnectionCount == 2, "The desktop reconnects to the same loopback endpoint once.");
            Check(remoteImage.Source is not null, "The reconnected desktop receives a fresh video frame.");
            Check(
                server.Commands.All(static payload => !IsPowerCommand(payload[0])),
                "Reconnect-success inspection emits no power or USB-reset command.");
            captures.Add(DesktopCapture.Save150Percent(window, outputDirectory, "reconnect-success-150.png"));
            scenarios.Add(new SmokeScenarioEvidence(
                "reconnect-success",
                captures,
                0,
                server.Commands.Count));
        }
        finally
        {
            await CloseConsoleAsync(window, session);
            server.ThrowIfFailed();
        }
    }

    private async Task RunReconnectFailureAsync()
    {
        await using var server = new LoopbackKvmServer(LoopbackFailureMode.ReconnectFails);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = await ConnectAsync(server, KvmPrivilegeLevel.Administrator, timeout.Token);
        var window = ShowConsole(session, "loopback-reconnect-failure");
        try
        {
            var status = FindNamed<TextBlock>(window, "StatusMessageText");
            await WaitForAsync(
                () => FindNamed<Image>(window, "RemoteImage").Source is not null,
                "failure-injection video",
                timeout.Token);
            await server.TriggerFailureAsync();
            await WaitForAsync(
                () => status.Text.Contains("KVM 自动恢复失败", StringComparison.Ordinal),
                "final reconnect failure state",
                timeout.Token);
            Check(server.ConnectionCount == 1, "Failed recovery never reports a false second connection.");
            Check(
                status.Text.Contains("3 attempts", StringComparison.OrdinalIgnoreCase) ||
                status.Text.Contains('3'),
                "The final reconnect message preserves the bounded retry detail.");
            Check(
                server.Commands.All(static payload => !IsPowerCommand(payload[0])),
                "Reconnect-failure inspection emits no power or USB-reset command.");
            var captures = new[]
            {
                DesktopCapture.Save150Percent(window, outputDirectory, "reconnect-failure-150.png"),
            };
            scenarios.Add(new SmokeScenarioEvidence(
                "reconnect-failure",
                captures,
                0,
                server.Commands.Count));
        }
        finally
        {
            await CloseConsoleAsync(window, session);
            server.ThrowIfFailed();
        }
    }

    private MainWindow ShowConsole(KvmClientSession session, string endpoint)
    {
        var window = new MainWindow(session, endpoint, settingsPersisted: true)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        application.MainWindow = window;
        window.Show();
        window.Activate();
        return window;
    }

    private static async Task ActivateViewerAsync(
        MainWindow window,
        Grid videoHost,
        TextBlock inputStatus,
        CancellationToken cancellationToken)
    {
        var handle = DesktopCapture.GetHandle(window);
        _ = DesktopAutomation.BringToForeground(handle);
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
        Keyboard.Focus(videoHost);
        var center = videoHost.PointToScreen(new Point(videoHost.ActualWidth / 2, videoHost.ActualHeight / 2));
        DesktopAutomation.MoveTo((int)Math.Round(center.X), (int)Math.Round(center.Y));
        await WaitForAsync(
            () => window.IsActive && videoHost.IsKeyboardFocusWithin &&
                  inputStatus.Text.Contains("输入已启用", StringComparison.Ordinal),
            "focused input-ready viewer",
            cancellationToken);
    }

    private static async Task<KvmClientSession> ConnectAsync(
        LoopbackKvmServer server,
        KvmPrivilegeLevel privilege,
        CancellationToken cancellationToken) =>
        await KvmClientSession.ConnectAsync(
            new KvmConnectionOptions(
                "127.0.0.1",
                server.Port,
                7,
                Privilege: (int)privilege,
                KeyboardEncoding: KvmKeyboardEncoding.LegacyPlain),
            cancellationToken);

    private static async Task CloseConsoleAsync(
        MainWindow window,
        KvmClientSession session)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (window.IsVisible)
        {
            window.Close();
        }

        try
        {
            await WaitForAsync(
                () => session.State == KvmSessionState.Closed,
                "console shutdown",
                cleanup.Token);
            await Task.Delay(200, cleanup.Token);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static T FindNamed<T>(FrameworkElement root, string name)
        where T : FrameworkElement =>
        root.FindName(name) as T ??
        throw new InvalidOperationException($"The window does not contain {typeof(T).Name} '{name}'.");

    private static IEnumerable<MenuItem> EnumerateMenuItems(ContextMenu? menu)
    {
        if (menu is null)
        {
            yield break;
        }

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var nested in EnumerateMenuItems(item))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(MenuItem parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var nested in EnumerateMenuItems(item))
            {
                yield return nested;
            }
        }
    }

    private static void RaiseClick(ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static void RaiseClick(MenuItem item) =>
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));

    private static bool HasColor(Brush? brush, Color expected) =>
        brush is SolidColorBrush solid && solid.Color == expected;

    private static bool IsPowerCommand(byte command) =>
        command is 0x20 or 0x21 or 0x22 or 0x23 or 0x25 or 0x30;

    private static bool IsInsideWindow(Window window, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        return bounds.Left >= 0 && bounds.Top >= 0 &&
               bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight;
    }

    private void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Desktop smoke check failed: {description}");
        }

        checks.Add(description);
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!condition())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(25, cancellationToken);
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {description}.", exception);
        }
    }
}

internal sealed record SmokeScenarioEvidence(
    string Name,
    IReadOnlyList<WindowCaptureEvidence> Captures,
    int InteractiveControlCount,
    int WireCommandCount);

internal sealed record DesktopSmokeReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    IReadOnlyList<SmokeScenarioEvidence> Scenarios,
    IReadOnlyList<string> Checks);
