using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Glassquid.Dock;

/// <summary>
/// Coordinates Alt+Tab interception via a low-level keyboard hook.
/// Only active when Glassquid taskbar/dock mode is enabled;
/// in Windows 11 mode, it yields entirely to Windows native Alt+Tab switcher.
/// </summary>
public sealed class AltTabController
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const uint VkTab = 0x09;
    private const uint VkMenu = 0x12;
    private const uint VkLMenu = 0xA4;
    private const uint VkRMenu = 0xA5;
    private const uint VkEscape = 0x1B;
    private const uint VkReturn = 0x0D;
    private const uint VkSpace = 0x20;
    private const int VkShift = 0x10;

    private readonly Func<bool> _isGlassquidMode;
    private readonly Dispatcher _dispatcher;
    private readonly Func<nint, BitmapSource?> _iconResolver;
    private readonly HookProc _hookCallback;
    private nint _hook;
    private AltTabWindow? _activeWindow;
    private bool _isDisposed;

    public AltTabController(
        Func<bool> isGlassquidMode,
        Dispatcher dispatcher,
        Func<nint, BitmapSource?> iconResolver)
    {
        _isGlassquidMode = isGlassquidMode ?? (() => false);
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _iconResolver = iconResolver ?? (_ => null);
        _hookCallback = LowLevelKeyboardProc;
    }

    public void Start()
    {
        if (_hook != 0 || _isDisposed) return;
        _hook = SetWindowsHookEx(WhKeyboardLl, _hookCallback, GetModuleHandle(null), 0);
    }

    public void Stop()
    {
        if (_hook != 0)
        {
            _ = UnhookWindowsHookEx(_hook);
            _hook = 0;
        }

        if (_activeWindow != null)
        {
            try
            {
                _activeWindow.Close();
            }
            catch
            {
                // Ignore cleanup exceptions
            }
            _activeWindow = null;
        }
    }

    private nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && lParam != 0)
        {
            var kbd = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var vk = kbd.vkCode;
            var msg = (int)wParam;
            var isDown = msg is WmKeyDown or WmSysKeyDown;
            var isUp = msg is WmKeyUp or WmSysKeyUp;

            // In Windows 11 mode, never intercept: leave native Windows 11 Alt+Tab completely untouched!
            if (_isGlassquidMode())
            {
                var altDown = (kbd.flags & 0x20) != 0 || (GetAsyncKeyState((int)VkMenu) & 0x8000) != 0;

                // Intercept Alt + Tab
                if (vk == VkTab && altDown)
                {
                    if (isDown)
                    {
                        var shiftDown = (GetAsyncKeyState(VkShift) & 0x8000) != 0;
                        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => HandleTab(shiftDown));
                    }
                    return 1; // Consume Tab key event
                }

                // If AltTab switcher window is currently open
                if (_activeWindow != null && _activeWindow.IsLoaded)
                {
                    if (isDown)
                    {
                        if (vk == VkEscape)
                        {
                            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Dismiss(activate: false));
                            return 1;
                        }

                        if (vk is 0x25 or 0x26 or 0x27 or 0x28) // Left, Up, Right, Down
                        {
                            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => _activeWindow?.NavigateDirection(vk));
                            return 1;
                        }

                        if (vk is VkReturn or VkSpace)
                        {
                            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Dismiss(activate: true));
                            return 1;
                        }
                    }

                    // On Alt key release, switch to selected window and close
                    if (isUp && vk is VkMenu or VkLMenu or VkRMenu)
                    {
                        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Dismiss(activate: true));
                        // Allow Windows to process the keyup event so Alt does not stick
                    }
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandleTab(bool shiftDown)
    {
        if (_isDisposed) return;

        if (_activeWindow == null)
        {
            var currentProcessId = (uint)Environment.ProcessId;
            var windows = AltTabWindow.GetAltTabWindows(currentProcessId);
            if (windows.Count == 0) return;

            var initialIndex = 0;
            if (shiftDown)
            {
                initialIndex = windows.Count - 1;
            }
            else if (windows.Count > 1)
            {
                // By default in Alt+Tab, index 1 is the previously active window
                initialIndex = 1;
            }

            _activeWindow = new AltTabWindow(
                windows,
                _iconResolver,
                initialIndex,
                onDismissed: () => _activeWindow = null);

            _activeWindow.Show();
        }
        else
        {
            if (shiftDown)
            {
                _activeWindow.SelectPrevious();
            }
            else
            {
                _activeWindow.SelectNext();
            }
        }
    }

    private void Dismiss(bool activate)
    {
        if (_activeWindow != null)
        {
            var win = _activeWindow;
            _activeWindow = null;

            if (activate)
            {
                win.CommitAndClose();
            }
            else
            {
                win.Close();
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
