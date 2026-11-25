using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Glassquid.Dock;

internal static partial class TrayIconContextMenuRelay
{
    private static readonly object OverflowPresentationGate = new();
    private static NativeOverflowPresentationGuard? _overflowPresentationGuard;
    private static CancellationTokenSource? _overflowMotionCancellation;
    private static DispatcherTimer? _overflowRestoreTimer;
    private static bool _overflowPresentationPending;
    private static bool _overflowPresentationClosing;
    private static NativeRect? _overflowDockBounds;
    private static long _overflowShownAt;
    private static long _overflowRestoreRequestedAt;

    internal static bool IsNativeOverflowClosing => _overflowPresentationClosing;

    internal static void PrepareNativeOverflowForDock()
    {
        CloseNativeTrayGlass();
        StartNativeTrayKeyboardGuard();
        CancelNativeOverflowMotion();
        StopNativeOverflowPresentationGuard();
        _overflowRestoreTimer?.Stop();
        _overflowPresentationClosing = false;
        lock (OverflowPresentationGate)
        {
            _overflowPresentationPending = true;
            _overflowShownAt = 0;
            GuardNativeOverflowPresentation();
        }

        // This thread keeps receiving CREATE/SHOW events while the existing shell
        // invocation waits synchronously for Explorer's UI Automation provider.
        var taskbar = FindWindow("Shell_TrayWnd", null);
        _ = GetWindowThreadProcessId(taskbar, out var explorerProcessId);
        if (explorerProcessId == 0) return;
        var guard = new NativeOverflowPresentationGuard(explorerProcessId);
        if (guard.Start()) _overflowPresentationGuard = guard;
        else guard.Dispose();
    }

    private static void GuardNativeOverflowPresentation()
    {
        var hwnd = FindWindow(OverflowWindowClass, null);
        if (hwnd == 0 || !IsWindow(hwnd)) return;
        _ = GetWindowRect(hwnd, out var bounds);
        CaptureNativeOverflowStyle(hwnd, bounds);
        if (MakeNativeOverflowNearlyInvisible(hwnd)) SetNativeOverflowOpacity(hwnd, 0);
    }

    private static void OnNativeOverflowPresentationEvent(nint hwnd, uint eventType)
    {
        var className = new char[128];
        var length = GetClassName(hwnd, className, className.Length);
        if (length <= 0 || new string(className, 0, length) != OverflowWindowClass) return;
        lock (OverflowPresentationGate)
        {
            if (!_overflowPresentationPending) return;
            if (eventType == 0x8002 && _overflowShownAt == 0)
                _overflowShownAt = Stopwatch.GetTimestamp();
            GuardNativeOverflowPresentation();
        }
    }

    internal static async Task<bool> AnimateNativeOverflowOpenAsync(SystemTrayBarWindow dock, int anchorX, int anchorY)
    {
        CancelNativeOverflowMotion();
        using var cancellation = new CancellationTokenSource();
        _overflowMotionCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            // Only the layout must settle: the shared glass supplies the visible
            // pixels, so Explorer's hidden entrance animation need not finish.
            var startedAt = Stopwatch.GetTimestamp();
            var previousSize = (Width: 0, Height: 0);
            var stableSamples = 0;
            while (Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds < 160)
            {
                token.ThrowIfCancellationRequested();
                var window = FindWindow(OverflowWindowClass, null);
                if (window == 0 || !IsWindowVisible(window)) return false;
                if (GetWindowRect(window, out var sample) && sample.Width > 0 && sample.Height > 0)
                {
                    var size = (sample.Width, sample.Height);
                    stableSamples = size == previousSize ? stableSamples + 1 : 0;
                    previousSize = size;
                    if (stableSamples >= 1) break;
                }
                await Task.Delay(16, token);
            }

            nint hwnd;
            NativeRect target;
            lock (OverflowPresentationGate)
            {
                GuardNativeOverflowPresentation();
                if (!TryStyleAndPositionNativeOverflow(anchorX, anchorY)) return false;
                hwnd = _styledOverflowHwnd;
                if (!GetWindowRect(hwnd, out target)) return false;
                _overflowDockBounds = target;
            }
            StopNativeOverflowPresentationGuard();
            var hasGlass = TryCreateNativeTrayGlass(dock, hwnd, target,
                out var usedSettledLayout, out var layoutSignature);
            if (hasGlass)
            {
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                if (!usedSettledLayout)
                {
                    // A first-open layout has no settled snapshot. Sample Explorer
                    // while the glass remains transparent; reveal only after the
                    // native cells stop moving, so no visible end-of-animation resize occurs.
                    var previousSignature = layoutSignature;
                    var layoutStableSamples = 0;
                    var settleWatch = Stopwatch.StartNew();
                    while (layoutStableSamples < 2 && settleWatch.Elapsed < TimeSpan.FromMilliseconds(500))
                    {
                        await Task.Delay(32, token);
                        token.ThrowIfCancellationRequested();
                        if (!GetWindowRect(hwnd, out var settledBounds)
                            || !TryCreateNativeTrayGlass(dock, hwnd, settledBounds,
                                out _, out var nextSignature, refresh: true)) continue;
                        if (string.Equals(previousSignature, nextSignature, StringComparison.Ordinal)) layoutStableSamples++;
                        else layoutStableSamples = 0;
                        previousSignature = nextSignature;
                    }
                    if (layoutStableSamples < 2) NativeTrayLayouts.Clear();
                    await Dispatcher.Yield(DispatcherPriority.Loaded);
                }
            }
            else
            {
                // The uncommon native-artwork fallback still needs its XAML
                // entrance to finish before it becomes visible.
                var shownAt = Interlocked.Read(ref _overflowShownAt);
                var remaining = 180 - Stopwatch.GetElapsedTime(shownAt == 0 ? startedAt : shownAt).TotalMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), token);
            }
            var distance = Math.Max(1, (int)Math.Round(10 * GetDpiForWindow(hwnd) / 96d));
            var presented = await NativeTrayPopupAnimation.RunAsync(
                SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(170) : TimeSpan.Zero,
                progress =>
                {
                    if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
                    var eased = 1 - Math.Pow(1 - progress, 3);
                    if (_nativeTrayGlass is not null)
                        return SetNativeOverflowOpacity(hwnd, eased, NativeTrayGlassWindow.MotionInset * (1 - eased));
                    _ = SetWindowPos(hwnd, 0, target.Left,
                        target.Top + (int)Math.Round(distance * (1 - eased)), 0, 0,
                        SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
                    return SetNativeOverflowOpacity(hwnd, eased);
                }, token);
            if (presented && _nativeTrayGlass is not null)
            {
                token.ThrowIfCancellationRequested();
                if (!EnableNativeTrayInput(hwnd)) return false;
                _ = SetNativeOverflowOpacity(hwnd, 1);
            }
            return presented;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            if (ReferenceEquals(_overflowMotionCancellation, cancellation))
            {
                StopNativeOverflowPresentationGuard();
                _overflowMotionCancellation = null;
            }
        }
    }

    internal static async Task AnimateNativeOverflowCloseAsync()
    {
        CancelNativeOverflowMotion();
        StopNativeOverflowPresentationGuard();
        var hwnd = _styledOverflowHwnd;
        if (hwnd == 0 || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var currentBounds)) return;
        var start = _overflowDockBounds ?? currentBounds;
        _nativeTrayGlass?.SetNativeInputReady(false);
        using var cancellation = new CancellationTokenSource();
        _overflowMotionCancellation = cancellation;
        var distance = Math.Max(1, (int)Math.Round(10 * GetDpiForWindow(hwnd) / 96d));
        try
        {
            _ = await NativeTrayPopupAnimation.RunAsync(
                SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(140) : TimeSpan.Zero,
                progress =>
                {
                    if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
                    var eased = progress * progress * progress;
                    if (_nativeTrayGlass is not null)
                        return SetNativeOverflowOpacity(hwnd, 1 - eased, NativeTrayGlassWindow.MotionInset * eased);
                    _ = SetWindowPos(hwnd, 0, start.Left, start.Top + (int)Math.Round(distance * eased), 0, 0,
                        SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
                    return SetNativeOverflowOpacity(hwnd, 1 - eased);
                }, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_overflowMotionCancellation, cancellation))
                _overflowMotionCancellation = null;
        }
    }

    private static bool SetNativeOverflowOpacity(nint hwnd, double opacity, double visualOffset = 0)
    {
        if (_stealthOverflowHwnd != hwnd) return false;
        if (_nativeTrayGlass is not null)
        {
            UpdateNativeTrayGlassFrame(hwnd, opacity, visualOffset);
            // Alpha 1 keeps Explorer's own icon cells hit-testable. The matching
            // dock glass below supplies all visible pixels and handles no clicks.
            var alpha = opacity > 0 && _nativeTrayGlass is { NativeInputReady: true } ? (byte)1 : (byte)0;
            if (_nativeTrayInputAlpha == alpha) return true;
            if (!SetLayeredWindowAttributes(hwnd, _stealthOverflowOriginalColorKey, alpha,
                    _stealthOverflowOriginalLayerFlags | LWA_ALPHA)) return false;
            _nativeTrayInputAlpha = alpha;
            return true;
        }
        return SetLayeredWindowAttributes(hwnd, _stealthOverflowOriginalColorKey,
            (byte)Math.Clamp((int)Math.Round(_stealthOverflowOriginalAlpha * opacity), 0, 255),
            _stealthOverflowOriginalLayerFlags | LWA_ALPHA);
    }

    private static NativeRect GetNativeOverflowAnchorMonitor(int anchorX, int anchorY)
    {
        var monitor = MonitorFromPoint(new POINT { X = anchorX, Y = anchorY }, 2);
        var info = new OverflowMonitorInfo { Size = (uint)Marshal.SizeOf<OverflowMonitorInfo>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref info)) return info.Bounds;
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        return new NativeRect
        {
            Left = left,
            Top = top,
            Right = left + Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
            Bottom = top + Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN)),
        };
    }

    private static void HideStyledNativeOverflowBeforeClose()
    {
        if (_styledOverflowHwnd != 0 && IsWindow(_styledOverflowHwnd))
        {
            _overflowPresentationClosing = true;
            _ = SetNativeOverflowOpacity(_styledOverflowHwnd, 0);
        }
    }

    private static void CancelNativeOverflowMotion()
    {
        var cancellation = _overflowMotionCancellation;
        _overflowMotionCancellation = null;
        cancellation?.Cancel();
    }

    private static void StopNativeOverflowPresentationGuard()
    {
        lock (OverflowPresentationGate) _overflowPresentationPending = false;
        var guard = _overflowPresentationGuard;
        _overflowPresentationGuard = null;
        guard?.Dispose();
    }

    private static bool DeferNativeOverflowRestoreWhileVisible()
    {
        if (_styledOverflowHwnd == 0 || !IsNativeOverflowVisible()) return false;
        _overflowRestoreRequestedAt = Stopwatch.GetTimestamp();
        if (_overflowRestoreTimer is null)
        {
            _overflowRestoreTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(32),
            };
            _overflowRestoreTimer.Tick += (_, _) =>
            {
                // UIA icon children disappear before the exit pixels finish. Wait
                // for the actual native window, with a bounded recovery for Explorer.
                if (IsNativeOverflowVisible()
                    && Stopwatch.GetElapsedTime(_overflowRestoreRequestedAt).TotalMilliseconds < 1000) return;
                _overflowRestoreTimer.Stop();
                RestoreNativeOverflowStyleNow();
            };
        }
        _overflowRestoreTimer.Start();
        return true;
    }

    private sealed class NativeOverflowPresentationGuard(uint explorerProcessId) : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly OverflowWinEventProc _callback = OnWinEvent;
        private Thread? _thread;
        private uint _threadId;
        private nint _hook;

        public bool Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Glassquid tray presentation guard" };
            _thread.Start();
            return _ready.Wait(TimeSpan.FromSeconds(1)) && _hook != 0;
        }

        private void Run()
        {
            _threadId = GetCurrentThreadId();
            _ = PeekMessage(out _, 0, 0, 0, 0);
            _hook = SetWinEventHook(0x8000, 0x8002, 0, _callback, explorerProcessId, 0, 0);
            _ready.Set();
            if (_hook == 0) return;
            try
            {
                while (GetMessage(out var message, 0, 0, 0) > 0)
                {
                    _ = TranslateMessage(in message);
                    _ = DispatchMessage(in message);
                }
            }
            finally { _ = UnhookWinEvent(_hook); }
        }

        private static void OnWinEvent(nint hook, uint eventType, nint hwnd,
            int objectId, int childId, uint eventThread, uint eventTime)
        {
            if (eventType is not (0x8000 or 0x8002) || hwnd == 0 || objectId != 0 || childId != 0) return;
            try { OnNativeOverflowPresentationEvent(hwnd, eventType); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debug.WriteLine($"[Glassquid tray presentation] {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_thread?.IsAlive == true && _threadId != 0)
                _ = PostThreadMessage(_threadId, 0x0012, 0, 0);
            if (_thread?.IsAlive == true && !_thread.Join(TimeSpan.FromMilliseconds(250))) return;
            _ready.Dispose();
        }
    }

    private delegate void OverflowWinEventProc(nint hook, uint eventType, nint hwnd,
        int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct OverflowHookMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public POINT Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OverflowMonitorInfo
    {
        public uint Size;
        public NativeRect Bounds;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        OverflowWinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern int GetMessage(out OverflowHookMessage message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out OverflowHookMessage message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in OverflowHookMessage message);
    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in OverflowHookMessage message);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref OverflowMonitorInfo info);
}
