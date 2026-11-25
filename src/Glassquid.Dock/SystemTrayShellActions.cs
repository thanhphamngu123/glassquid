using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Glassquid.Dock;

internal static class SystemTrayShellActions
{
    private const uint InputMouse = 0;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint AppBarGetTaskbarPosition = 0x00000005;
    private const uint AppBarEdgeTop = 0;
    private const uint AppBarEdgeLeft = 1;
    private const uint AppBarEdgeRight = 2;
    private const uint AppBarEdgeBottom = 3;
    private const int AutoHideRevealTimeoutMilliseconds = 1600;
    private const int AutoHideRevealPollMilliseconds = 25;
    private const int TaskbarQuickSettingsOffsetDips = 166;
    private const int TaskbarNotificationsRightInsetDips = 30;

    public static bool OpenQuickSettings() => ClickTaskbarTrayButton(TaskbarTrayButton.QuickSettings);

    public static bool OpenNotifications() => ClickTaskbarTrayButton(TaskbarTrayButton.Notifications);

    private static bool ClickTaskbarTrayButton(TaskbarTrayButton button)
    {
        foreach (var taskbar in FindTaskbarWindows())
        {
            var trayNotify = FindWindowEx(taskbar, 0, "TrayNotifyWnd", null);
            if (trayNotify == 0 || !GetWindowRect(trayNotify, out var trayBounds)) continue;

            var hasAppBarPosition = TryGetTaskbarPosition(taskbar, out var appBar);
            var edge = hasAppBarPosition ? appBar.Edge : AppBarEdgeBottom;
            var dpi = GetDpiForWindow(taskbar);
            if (dpi == 0) dpi = 96;

            if (hasAppBarPosition && !IsContainedBy(trayBounds, appBar.Bounds))
            {
                var edgePoint = GetTaskbarRevealPoint(trayBounds, appBar.Bounds, edge, dpi);
                if (!SetCursorPos(edgePoint.X, edgePoint.Y)) continue;

                var elapsed = 0;
                do
                {
                    Thread.Sleep(AutoHideRevealPollMilliseconds);
                    elapsed += AutoHideRevealPollMilliseconds;
                    if (!GetWindowRect(trayNotify, out trayBounds)) break;
                }
                while (!IsContainedBy(trayBounds, appBar.Bounds)
                    && elapsed < AutoHideRevealTimeoutMilliseconds);

                if (!IsContainedBy(trayBounds, appBar.Bounds)) continue;
            }

            var point = GetTaskbarTrayButtonPoint(trayBounds, edge, dpi, button);
            if (ClickAt(point.X, point.Y)) return true;
        }

        return false;
    }

    private static (int X, int Y) GetTaskbarTrayButtonPoint(
        NativeRect trayBounds, uint edge, uint dpi, TaskbarTrayButton button)
    {
        var scale = dpi / 96.0;
        if (edge is AppBarEdgeLeft or AppBarEdgeRight)
        {
            var x = trayBounds.Left + trayBounds.Width / 2;
            var verticalOffset = button switch
            {
                TaskbarTrayButton.QuickSettings => 166,
                _ => 30,
            };
            var y = button == TaskbarTrayButton.Notifications
                ? trayBounds.Bottom - (int)Math.Round(verticalOffset * scale)
                : trayBounds.Top + (int)Math.Round(verticalOffset * scale);
            return (x, Math.Clamp(y, trayBounds.Top + 1, trayBounds.Bottom - 2));
        }

        var xOffset = button switch
        {
            TaskbarTrayButton.QuickSettings => (int)Math.Round(TaskbarQuickSettingsOffsetDips * scale),
            _ => -((int)Math.Round(TaskbarNotificationsRightInsetDips * scale)),
        };
        var xPosition = xOffset >= 0 ? trayBounds.Left + xOffset : trayBounds.Right + xOffset;
        xPosition = Math.Clamp(xPosition, trayBounds.Left + 1, trayBounds.Right - 2);
        return (xPosition, trayBounds.Top + trayBounds.Height / 2);
    }

    private enum TaskbarTrayButton
    {
        QuickSettings,
        Notifications,
    }

    private static (int X, int Y) GetTaskbarRevealPoint(
        NativeRect trayBounds,
        NativeRect appBarBounds,
        uint edge,
        uint dpi)
    {
        var trayPoint = GetTrayRevealPoint(trayBounds, edge, dpi);
        return edge switch
        {
            AppBarEdgeTop => (trayPoint.X, appBarBounds.Top),
            AppBarEdgeLeft => (appBarBounds.Left, trayPoint.Y),
            AppBarEdgeRight => (appBarBounds.Right - 1, trayPoint.Y),
            _ => (trayPoint.X, appBarBounds.Bottom - 1),
        };
    }

    private static (int X, int Y) GetTrayRevealPoint(NativeRect trayBounds, uint edge, uint dpi)
    {
        var inset = (int)Math.Round(18 * dpi / 96.0);
        return edge is AppBarEdgeLeft or AppBarEdgeRight
            ? (trayBounds.Left + trayBounds.Width / 2, trayBounds.Top + inset)
            : (trayBounds.Left + inset, trayBounds.Top + trayBounds.Height / 2);
    }

    private static bool IsContainedBy(NativeRect inner, NativeRect outer) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top
        && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static bool TryGetTaskbarPosition(nint taskbar, out NativeAppBarData appBar)
    {
        appBar = new NativeAppBarData
        {
            Size = Marshal.SizeOf<NativeAppBarData>(),
            Window = taskbar,
        };
        return SHAppBarMessage(AppBarGetTaskbarPosition, ref appBar) != 0
            && appBar.Bounds.Width > 0 && appBar.Bounds.Height > 0;
    }

    private static nint[] FindTaskbarWindows()
    {
        var windows = new List<nint>();
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != 0) windows.Add(primary);

        EnumWindows((window, _) =>
        {
            var className = new char[128];
            var length = GetClassName(window, className, className.Length);
            var name = length > 0 ? new string(className, 0, length) : string.Empty;
            if (name.Equals("Shell_TrayWnd", StringComparison.Ordinal)
                || name.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal))
            {
                if (!windows.Contains(window)) windows.Add(window);
            }
            return true;
        }, 0);

        return windows.ToArray();
    }

    private static bool ClickAt(int x, int y)
    {
        if (!SetCursorPos(x, y)) return false;
        NativeInput[] inputs =
        [
            MouseInput(MouseLeftDown),
            MouseInput(MouseLeftUp),
        ];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == (uint)inputs.Length;
    }

    private static NativeInput MouseInput(uint flags) => new()
    {
        Type = InputMouse,
        Data = new NativeInputUnion
        {
            Mouse = new NativeMouseInput { Flags = flags },
        },
    };

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowEx(nint parent, nint afterChild, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern nint SHAppBarMessage(uint message, ref NativeAppBarData appBarData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint window, [Out] char[] className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] NativeInput[] inputs, int inputSize);


    private delegate bool EnumWindowsCallback(nint window, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAppBarData
    {
        public int Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Bounds;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeMouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

}
