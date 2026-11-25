using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Glassquid.Dock;

internal static partial class TrayIconContextMenuRelay
{
    private static NativeTrayKeyboardGuard? _nativeTrayKeyboardGuard;

    private static void StartNativeTrayKeyboardGuard()
    {
        StopNativeTrayKeyboardGuard();
        var guard = new NativeTrayKeyboardGuard();
        if (guard.TryStart()) _nativeTrayKeyboardGuard = guard;
        else guard.Dispose();
    }

    private static void StopNativeTrayKeyboardGuard()
    {
        var guard = _nativeTrayKeyboardGuard;
        _nativeTrayKeyboardGuard = null;
        guard?.Dispose();
    }

    private static bool IsNativeTrayKeyboardTarget()
    {
        if (!IsNativeOverflowVisible()) return false;
        return IsNativeTrayFocusWindow(GetTrayForegroundWindow());
    }

    internal static bool IsNativeTrayFocusWindow(nint foreground)
    {
        if (!IsNativeTrayKeyboardWindow(foreground)) return false;
        var thread = GetWindowThreadProcessId(foreground, out var foregroundProcess);
        var popup = FindWindow(OverflowWindowClass, null);
        _ = GetWindowThreadProcessId(popup != 0 ? popup : FindWindow("Shell_TrayWnd", null), out var popupProcess);
        if (foregroundProcess == 0 || foregroundProcess != popupProcess) return false;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info)) return false;
        // A XAML flyout can itself report menu mode. Exempt an actual Win32 app
        // context menu, rather than treating the flyout's own mode as a selection.
        var contextMenuOpen = false;
        _ = EnumThreadWindows(thread, (window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var className = new char[64];
            var length = GetClassName(window, className, className.Length);
            if (length > 0 && new string(className, 0, length) == "#32768")
            {
                contextMenuOpen = true;
                return false;
            }
            return true;
        }, 0);
        return !contextMenuOpen && (info.MenuOwner == 0 || IsNativeTrayKeyboardWindow(info.MenuOwner));
    }

    private static bool IsNativeTrayKeyboardWindow(nint window)
    {
        for (var depth = 0; window != 0 && depth < 8; depth++)
        {
            if (IsNativeTrayWindow(window)) return true;
            var root = GetAncestor(window, 2);
            window = GetTrayWindowOwner(root == 0 ? window : root, 4); // GW_OWNER
        }
        return false;
    }

    private sealed class NativeTrayKeyboardGuard : IDisposable
    {
        private readonly NativeTrayKeyboardPolicy _policy = new();
        private readonly TrayKeyboardHookProc _callback;
        private nint _hook;

        public NativeTrayKeyboardGuard() => _callback = OnKey;

        public bool TryStart()
        {
            _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0); // WH_KEYBOARD_LL
            if (_hook == 0) Log($"Tray keyboard guard unavailable: {Marshal.GetLastWin32Error()}.");
            return _hook != 0;
        }

        private nint OnKey(int code, nint message, nint data)
        {
            try
            {
                if (code >= 0 && (int)message is 0x0100 or 0x0101)
                {
                    var key = Marshal.ReadInt32(data);
                    if (NativeTrayKeyboardPolicy.IsRelevantKey(key))
                    {
                        var unmodified = (GetTrayAsyncKeyState(0x11) & 0x8000) == 0
                            && (GetTrayAsyncKeyState(0x12) & 0x8000) == 0
                            && (GetTrayAsyncKeyState(0x5B) & 0x8000) == 0
                            && (GetTrayAsyncKeyState(0x5C) & 0x8000) == 0;
                        var scoped = unmodified && IsNativeTrayKeyboardTarget();
                        var consumed = _policy.ShouldConsume(key, (int)message == 0x0100, scoped);
                        if (key == 0x0D && (int)message == 0x0100 && IsNativeOverflowVisible())
                            _ = Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                                new Action(() => Log($"Tray Enter: nativeFocus={scoped}, consumedDefaultSelection={consumed}.")));
                        if (consumed) return 1;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine($"[Glassquid tray keyboard] {ex.Message}");
            }
            return CallNextHookEx(_hook, code, message, data);
        }

        public void Dispose()
        {
            if (_hook != 0) _ = UnhookWindowsHookEx(_hook);
            _hook = 0;
            GC.KeepAlive(_callback);
        }
    }

    private delegate nint TrayKeyboardHookProc(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int kind, TrayKeyboardHookProc callback, nint module, uint thread);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetTrayForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short GetTrayAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern nint GetTrayWindowOwner(nint window, uint command);
}

/// <summary>Opening the tray is not an explicit selection of its remembered app.</summary>
internal sealed class NativeTrayKeyboardPolicy
{
    private readonly HashSet<int> _consumed = [];
    private bool _selectionMade;

    internal static bool IsRelevantKey(int key) => key is 0x0D or 0x20 or 0x09 or >= 0x23 and <= 0x28;

    internal bool ShouldConsume(int key, bool down, bool trayHasFocus)
    {
        if (!down) return _consumed.Remove(key);
        if (_consumed.Contains(key)) return true;
        if (!trayHasFocus)
        {
            _selectionMade = false;
            return false;
        }
        if (key is 0x09 or >= 0x23 and <= 0x28) _selectionMade = true;
        if (key is not (0x0D or 0x20) || _selectionMade) return false;
        _consumed.Add(key);
        return true;
    }
}
