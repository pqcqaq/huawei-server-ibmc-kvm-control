using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace IbmcKvm.DesktopSmoke;

internal sealed record AutomationInspection(
    IReadOnlyList<string> MissingAutomationIds,
    IReadOnlyList<string> OutsideInteractiveControls,
    int InteractiveControlCount);

internal static class DesktopAutomation
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint KeyUp = 0x0002;
    private const ushort EscapeVirtualKey = 0x1B;

    public static bool BringToForeground(nint windowHandle) => SetForegroundWindow(windowHandle);

    public static void MoveTo(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            throw new InvalidOperationException("Windows rejected the desktop pointer move.");
        }
    }

    public static void ClickAt(int x, int y)
    {
        MoveTo(x, y);

        Send(
        [
            new Input
            {
                Type = InputMouse,
                Data = new InputUnion { Mouse = new MouseInput { Flags = MouseLeftDown } },
            },
            new Input
            {
                Type = InputMouse,
                Data = new InputUnion { Mouse = new MouseInput { Flags = MouseLeftUp } },
            },
        ]);
    }

    public static void PressEscape()
    {
        Send(
        [
            new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = EscapeVirtualKey } },
            },
            new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput { VirtualKey = EscapeVirtualKey, Flags = KeyUp },
                },
            },
        ]);
    }

    public static Task<AutomationInspection> InspectWindowAsync(
        nint windowHandle,
        IReadOnlyCollection<string> requiredAutomationIds,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => InspectWindow(windowHandle, requiredAutomationIds, cancellationToken),
            cancellationToken);

    private static AutomationInspection InspectWindow(
        nint windowHandle,
        IReadOnlyCollection<string> requiredAutomationIds,
        CancellationToken cancellationToken)
    {
        var window = AutomationElement.FromHandle(windowHandle) ??
                     throw new InvalidOperationException("UI Automation could not resolve the console window.");
        var bounds = window.Current.BoundingRectangle;
        var elements = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var seenAutomationIds = new HashSet<string>(StringComparer.Ordinal);
        var outside = new List<string>();
        var interactiveCount = 0;
        var interactiveTypes = new HashSet<ControlType>
        {
            ControlType.Button,
            ControlType.CheckBox,
            ControlType.ComboBox,
            ControlType.List,
            ControlType.ListItem,
            ControlType.MenuItem,
            ControlType.TabItem,
        };

        for (var index = 0; index < elements.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = elements[index];
            var automationId = element.Current.AutomationId;
            if (!string.IsNullOrEmpty(automationId))
            {
                seenAutomationIds.Add(automationId);
            }

            if (!element.Current.IsOffscreen && interactiveTypes.Contains(element.Current.ControlType))
            {
                interactiveCount++;
                var elementBounds = element.Current.BoundingRectangle;
                if (elementBounds.Width > 0 && elementBounds.Height > 0 &&
                    (elementBounds.Left < bounds.Left - 1 ||
                     elementBounds.Top < bounds.Top - 1 ||
                     elementBounds.Right > bounds.Right + 1 ||
                     elementBounds.Bottom > bounds.Bottom + 1))
                {
                    outside.Add($"{element.Current.ControlType.ProgrammaticName}: {element.Current.Name}");
                }
            }
        }

        var missing = requiredAutomationIds
            .Where(id => !seenAutomationIds.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new AutomationInspection(
            missing,
            outside.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            interactiveCount);
    }

    private static void Send(Input[] inputs)
    {
        var sent = SendInput(checked((uint)inputs.Length), inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"Windows accepted {sent} of {inputs.Length} synthetic inputs.");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
