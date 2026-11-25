using System.Runtime.InteropServices;
using Microsoft.Win32;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>
/// Hides Windows taskbar windows while Glassquid is active and restores the user's
/// native taskbar state when Glassquid releases them.
/// </summary>
public sealed class TaskbarVisibilityController : IDisposable
{
    private static readonly HashSet<string> TaskbarClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    private const string RecoveryKeyPath = @"Software\Glassquid\RuntimeState";
    private const string RecoveryValueName = "WindowsTaskbarHidden";
    private const string AutoHideRecoveryValueName = "AutoHideForcedByGlassquid";
    private const string LegacyRecoveryValueName = "WindowsTaskbarAppBarState";
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private readonly object _gate = new();
    private readonly HashSet<nint> _taskbarsToRestore = [];
    private readonly Timer _refreshTimer;
    private TaskbarShowWatcher? _taskbarShowWatcher;
    private string? _recoveryError;
    private volatile int _refreshSuspensionCount;
    private volatile bool _hidden;
    private volatile bool _disposed;
    private bool _autoHideForcedByUs;

    /// <summary>Whether Glassquid currently owns the hidden-taskbar state.</summary>
    public bool IsHidden
    {
        get { lock (_gate) return _hidden; }
    }

    /// <summary>An error encountered while restoring state left by a previous interrupted run.</summary>
    public string? RecoveryError => _recoveryError;

    /// <summary>
    /// Optional predicate to suppress hiding taskbars while Glassquid is presenting
    /// an Explorer shell popup (e.g. native tray overflow) that depends on the taskbar HWND.
    /// </summary>
    public Func<bool>? ShouldSuppressHide { get; set; }

    public TaskbarVisibilityController()
    {
        _refreshTimer = new Timer(RefreshTaskbars, null, Timeout.Infinite, Timeout.Infinite);
        _recoveryError = RestorePersistedState();
    }

    /// <summary>Hide or restore taskbar windows. When hiding, auto-hide is
    /// temporarily forced ON so that the desktop work area expands to the full
    /// screen and app windows fill the space vacated by the taskbar.</summary>
    public string? SetHidden(bool hidden)
    {
        lock (_gate)
        {
            if (_disposed) return "Bộ điều khiển taskbar đã dừng.";

            if (!_hidden)
            {
                _recoveryError = RestorePersistedState();
                if (_recoveryError is not null) return _recoveryError;
            }

            return hidden ? HideTaskbars() : RestoreTaskbars();
        }
    }

    /// <summary>
    /// Temporarily pauses the watchdog that re-hides Explorer taskbars. This is used
    /// while Glassquid invokes Windows' native tray chevron on an invisible taskbar.
    /// </summary>
    public IDisposable SuspendRefresh()
    {
        lock (_gate)
        {
            if (_disposed || !_hidden) return EmptySuspension.Instance;

            _refreshSuspensionCount++;
            _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
            return new RefreshSuspension(this);
        }
    }

    private string? HideTaskbars()
    {
        if (_hidden)
        {
            StartWatchingTaskbarShows();
            var hideError = HideCurrentTaskbars();
            if (hideError is not null) return hideError;
            _refreshTimer.Change(1000, 1000);
            return null;
        }

        var taskbars = FindTaskbarWindows();
        if (taskbars.Count == 0) return "Không tìm thấy cửa sổ taskbar của Windows.";

        _taskbarsToRestore.Clear();
        foreach (var window in taskbars)
            _taskbarsToRestore.Add(window);

        try
        {
            SavePersistedState();
        }
        catch (Exception ex) when (IsRecoverableStateException(ex))
        {
            _taskbarsToRestore.Clear();
            return "Không thể lưu trạng thái taskbar ban đầu: " + ex.Message;
        }

        // Force auto-hide ON so that Windows expands the work area to the full
        // screen. All maximized/snapped app windows will immediately fill the
        // space vacated by the hidden taskbar.
        if ((GetAppBarState() & Shell32.AbsAutoHide) == 0)
        {
            var autoHideError = ApplyAppBarState(GetAppBarState() | Shell32.AbsAutoHide);
            if (autoHideError is not null)
            {
                _taskbarsToRestore.Clear();
                try { ClearPersistedState(); }
                catch (Exception ex) when (IsRecoverableStateException(ex))
                {
                    return autoHideError + " Không thể xóa dữ liệu khôi phục taskbar: " + ex.Message;
                }
                return autoHideError;
            }
            _autoHideForcedByUs = true;
            SaveAutoHideRecovery();
        }

        // Listen before hiding taskbar HWNDs so Explorer cannot flash them back on screen.
        _hidden = true;
        StartWatchingTaskbarShows();
        var hideTaskbarError = HideCurrentTaskbars();
        if (hideTaskbarError is not null)
        {
            var rollbackError = RestoreTaskbars();
            return rollbackError is null
                ? hideTaskbarError
                : hideTaskbarError + " Khôi phục trạng thái taskbar trước đó cũng thất bại: " + rollbackError;
        }

        _refreshTimer.Change(1000, 1000);
        return null;
    }

    private string? RestoreTaskbars()
    {
        if (!_hidden && _taskbarsToRestore.Count == 0) return null;

        // Stop every hide path before making Explorer visible. WinEvent callbacks
        // do not take _gate, so a queued callback must already see the new state.
        _hidden = false;
        _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
        StopWatchingTaskbarShows();

        // Restore the user's native preference first. Explorer can recreate or
        // reposition taskbar HWNDs while applying it, so show the final set after.
        if (_autoHideForcedByUs || HasAutoHideRecovery())
        {
            var autoHideError = ApplyAppBarState(GetAppBarState() & ~Shell32.AbsAutoHide);
            if (autoHideError is not null) return ResumeHiddenAfterRestoreFailure(autoHideError);
        }

        var visibilityError = RestoreCurrentTaskbars();
        if (visibilityError is not null)
        {
            if (_autoHideForcedByUs)
                _ = ApplyAppBarState(GetAppBarState() | Shell32.AbsAutoHide);
            return ResumeHiddenAfterRestoreFailure(visibilityError);
        }

        if (_autoHideForcedByUs || HasAutoHideRecovery())
        {
            _autoHideForcedByUs = false;
            ClearAutoHideRecovery();
        }

        try
        {
            ClearPersistedState();
        }
        catch (Exception ex) when (IsRecoverableStateException(ex))
        {
            _hidden = false;
            return "Đã khôi phục taskbar nhưng không xóa được dữ liệu khôi phục: " + ex.Message;
        }

        _hidden = false;
        _taskbarsToRestore.Clear();
        _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return null;
    }

    private string ResumeHiddenAfterRestoreFailure(string error)
    {
        _hidden = true;
        StartWatchingTaskbarShows();
        _ = HideCurrentTaskbars();
        _refreshTimer.Change(1000, 1000);
        return error;
    }

    private void RefreshTaskbars(object? state)
    {
        lock (_gate)
        {
            if (_disposed || !_hidden || _refreshSuspensionCount > 0) return;
            if (ShouldSuppressHide?.Invoke() == true) return;
            _ = HideCurrentTaskbars();
        }
    }

    private void OnTaskbarShown(nint window)
    {
        if (_disposed || !_hidden || _refreshSuspensionCount > 0 || window == 0) return;
        if (ShouldSuppressHide?.Invoke() == true) return;
        if (!TaskbarClasses.Contains(User32.ReadClassName(window))) return;

        // EVENT_OBJECT_SHOW arrives when Explorer recreates or reveals a taskbar.
        // Hide it in the event callback so the watchdog is only a recovery fallback.
        if (User32.IsWindow(window) && User32.IsWindowVisible(window))
            _ = User32.ShowWindow(window, SwHide);
    }

    private void StartWatchingTaskbarShows()
    {
        if (_taskbarShowWatcher is not null) return;
        var watcher = new TaskbarShowWatcher(OnTaskbarShown);
        if (watcher.Start())
        {
            _taskbarShowWatcher = watcher;
            return;
        }

        watcher.Dispose();
    }

    private void StopWatchingTaskbarShows()
    {
        var watcher = _taskbarShowWatcher;
        _taskbarShowWatcher = null;
        watcher?.Dispose();
    }

    private void ResumeRefresh()
    {
        lock (_gate)
        {
            if (_refreshSuspensionCount > 0) _refreshSuspensionCount--;
            if (!_disposed && _hidden && _refreshSuspensionCount == 0)
                _refreshTimer.Change(1000, 1000);
        }
    }

    private string? HideCurrentTaskbars()
    {
        var windows = FindTaskbarWindows();
        if (windows.Count == 0) return "Không tìm thấy cửa sổ taskbar của Windows.";
        foreach (var window in windows)
        {
            _taskbarsToRestore.Add(window);

            if (User32.IsWindowVisible(window)) _ = User32.ShowWindow(window, SwHide);
            if (User32.IsWindowVisible(window))
                return "Windows không cho phép Glassquid ẩn một cửa sổ taskbar.";
        }

        return null;
    }

    private string? RestoreCurrentTaskbars()
    {
        RestoreOverflowWindowTransparency();

        foreach (var window in FindTaskbarWindows())
            _taskbarsToRestore.Add(window);

        var nativeAutoHide = (GetAppBarState() & Shell32.AbsAutoHide) != 0;
        foreach (var window in _taskbarsToRestore.ToArray())
        {
            if (!User32.IsWindow(window) || !TaskbarClasses.Contains(User32.ReadClassName(window)))
            {
                _taskbarsToRestore.Remove(window);
                continue;
            }

            // A previously sampled false bit can come from Explorer recreation
            // or the tray relay. Normal Windows mode must restore opacity and remove WS_EX_LAYERED for all
            // taskbars and their child windows (Windows 11 XAML Island host etc.),
            // then release SW_HIDE. Native auto-hide then manages their screen positions.
            RestoreWindowTreeTransparencyAndVisibility(window);
            _ = User32.ShowWindow(window, SwShowNoActivate);
            if (!nativeAutoHide && !User32.IsWindowVisible(window))
                return "Không thể khôi phục trạng thái hiển thị taskbar của Windows.";
        }

        return null;
    }

    private static string? RestorePersistedState()
    {
        uint? legacyState;
        bool needsVisibilityRecovery;
        bool needsAutoHideRecovery;
        try
        {
            legacyState = ReadLegacyPersistedState();
            needsVisibilityRecovery = HasPersistedVisibilityRecovery();
            needsAutoHideRecovery = HasAutoHideRecovery();
        }
        catch (Exception ex) when (IsRecoverableStateException(ex))
        {
            return "Không thể đọc dữ liệu khôi phục taskbar: " + ex.Message;
        }

        // Older Glassquid versions temporarily changed ABM_AUTOHIDE as part of
        // hiding the taskbar. Recover that legacy snapshot once and then keep the
        // native preference separate from Glassquid's taskbar visibility option.
        if (legacyState is not null)
        {
            var error = ApplyAppBarState(legacyState.Value);
            if (error is not null) return "Không thể khôi phục trạng thái taskbar trước đó: " + error;
        }

        // Glassquid forces auto-hide ON while the taskbar is hidden so that
        // Windows expands the work area to full screen. If Glassquid crashed
        // while auto-hide was forced, restore it to OFF now.
        if (needsAutoHideRecovery)
        {
            var error = ApplyAppBarState(GetAppBarState() & ~Shell32.AbsAutoHide);
            if (error is not null) return "Không thể khôi phục thiết lập tự ẩn taskbar trước đó: " + error;
            ClearAutoHideRecovery();
        }

        if (!needsVisibilityRecovery && legacyState is null && !needsAutoHideRecovery) return null;

        // Recover taskbar HWNDs and overflow flyouts that may have remained explicitly hidden or transparent after a crash.
        RestoreOverflowWindowTransparency();
        foreach (var window in FindTaskbarWindows())
        {
            RestoreWindowTreeTransparencyAndVisibility(window);
            if (!User32.IsWindowVisible(window)) _ = User32.ShowWindow(window, SwShowNoActivate);
        }

        try
        {
            ClearPersistedState();
            ClearLegacyPersistedState();
        }
        catch (Exception ex) when (IsRecoverableStateException(ex))
        {
            return "Đã khôi phục taskbar nhưng không xóa được dữ liệu khôi phục: " + ex.Message;
        }

        return null;
    }

    private static void RestoreWindowTreeTransparencyAndVisibility(nint root)
    {
        // 1. In Windows 11, taskbar controls (XAML Islands, Start, etc.) are hosted in child windows.
        // If they were made layered with alpha = 0 (e.g. by tray relay or stealth mode), restore them to full opacity.
        User32.EnumChildWindows(root, (child, param) =>
        {
            RestoreSingleWindowTransparency(child);
            User32.ShowWindow(child, SwShowNoActivate);
            return 1;
        }, 0);

        // 2. Restore the root taskbar window
        RestoreSingleWindowTransparency(root);
        User32.ShowWindow(root, SwShowNoActivate);
    }

    private static void RestoreSingleWindowTransparency(nint window)
    {
        var exStyle = User32.GetWindowLong(window, User32.GwlExStyle);
        var hasLayeredBit = (exStyle & User32.WsExLayered) != 0;
        var hasLayerAttrs = User32.GetLayeredWindowAttributes(window, out _, out _, out _);

        if (hasLayeredBit || hasLayerAttrs)
        {
            _ = User32.SetLayeredWindowAttributes(window, 0, 255, User32.LwaAlpha);
            if (hasLayeredBit)
            {
                _ = User32.SetWindowLong(window, User32.GwlExStyle, exStyle & ~User32.WsExLayered);
            }
            _ = Dpi32.SetWindowPos(window, 0, 0, 0, 0, 0,
                User32.SwpNoSize | User32.SwpNoMove | User32.SwpNoZOrder | User32.SwpNoActivate | User32.SwpFrameChanged);
        }
    }

    private static void RestoreOverflowWindowTransparency()
    {
        var overflow = User32.FindWindow("TopLevelWindowForOverflowXamlIsland", null);
        if (overflow != 0 && User32.IsWindow(overflow))
        {
            RestoreSingleWindowTransparency(overflow);
        }
    }

    private static uint GetAppBarState()
    {
        var data = new Shell32.AppBarData
        {
            Size = checked((uint)Marshal.SizeOf<Shell32.AppBarData>()),
        };
        return unchecked((uint)Shell32.SHAppBarMessage(Shell32.AbmGetState, ref data));
    }

    private static string? ApplyAppBarState(uint state)
    {
        var taskbar = User32.FindWindow("Shell_TrayWnd", null);
        if (taskbar == 0) return "Không tìm thấy taskbar chính của Windows.";

        var data = new Shell32.AppBarData
        {
            Size = checked((uint)Marshal.SizeOf<Shell32.AppBarData>()),
            Window = taskbar,
            Parameter = (nint)state,
        };

        // ABM_SETSTATE returns TRUE by contract, so verify the state through ABM_GETSTATE.
        _ = Shell32.SHAppBarMessage(Shell32.AbmSetState, ref data);
        var actualState = GetAppBarState();
        return (actualState & Shell32.AbsAutoHide) == (state & Shell32.AbsAutoHide)
            ? null
            : "Windows chưa áp dụng được trạng thái tự ẩn taskbar.";
    }

    private static uint? ReadLegacyPersistedState()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RecoveryKeyPath, writable: false);
        if (key?.GetValue(LegacyRecoveryValueName) is not int state) return null;
        return unchecked((uint)state);
    }

    private static bool HasPersistedVisibilityRecovery()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RecoveryKeyPath, writable: false);
        return key?.GetValue(RecoveryValueName) is int marker && marker != 0;
    }

    private static void SavePersistedState()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RecoveryKeyPath, writable: true)
            ?? throw new InvalidOperationException("Không thể tạo khóa khôi phục taskbar.");
        key.SetValue(RecoveryValueName, 1, RegistryValueKind.DWord);
    }

    private static void ClearPersistedState()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RecoveryKeyPath, writable: true);
        key?.DeleteValue(RecoveryValueName, throwOnMissingValue: false);
    }

    private static void ClearLegacyPersistedState()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RecoveryKeyPath, writable: true);
        key?.DeleteValue(LegacyRecoveryValueName, throwOnMissingValue: false);
    }

    private static void SaveAutoHideRecovery()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RecoveryKeyPath, writable: true);
            key?.SetValue(AutoHideRecoveryValueName, 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void ClearAutoHideRecovery()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RecoveryKeyPath, writable: true);
            key?.DeleteValue(AutoHideRecoveryValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    private static bool HasAutoHideRecovery()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RecoveryKeyPath, writable: false);
            return key?.GetValue(AutoHideRecoveryValueName) is int v && v != 0;
        }
        catch { return false; }
    }

    private static List<nint> FindTaskbarWindows()
    {
        var windows = new List<nint>();
        User32.EnumWindows((window, _) =>
        {
            if (TaskbarClasses.Contains(User32.ReadClassName(window))) windows.Add(window);
            return 1;
        }, 0);
        return windows;
    }

    private static bool IsRecoverableStateException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or InvalidOperationException
            or PlatformNotSupportedException;

    private sealed class RefreshSuspension(TaskbarVisibilityController owner) : IDisposable
    {
        private TaskbarVisibilityController? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ResumeRefresh();
    }

    private sealed class EmptySuspension : IDisposable
    {
        public static EmptySuspension Instance { get; } = new();
        public void Dispose() { }
    }

    /// <summary>Receives Explorer taskbar-show events on a dedicated message-loop thread.</summary>
    private sealed class TaskbarShowWatcher : IDisposable
    {
        private readonly Action<nint> _onTaskbarShown;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private readonly List<nint> _hooks = [];
        private readonly User32.WinEventProc _callback;
        private uint _threadId;
        private bool _installed;
        private volatile bool _disposed;

        public TaskbarShowWatcher(Action<nint> onTaskbarShown)
        {
            _onTaskbarShown = onTaskbarShown;
            _callback = OnWinEvent;
            _thread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "Glassquid taskbar visibility watcher",
            };
        }

        public bool Start()
        {
            try
            {
                _thread.Start();
                if (!_ready.Wait(TimeSpan.FromSeconds(3))) return false;
                return _installed;
            }
            catch (Exception ex) when (ex is ThreadStateException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_thread.IsAlive && _threadId != 0)
                _ = User32.PostThreadMessage(_threadId, User32.WmQuit, 0, 0);
            if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(2))) return;
            _ready.Dispose();
        }

        private void HookThreadMain()
        {
            try
            {
                _threadId = Kernel32.GetCurrentThreadId();
                _ = User32.PeekMessage(out _, 0, 0, 0, User32.PmNoRemove);
                if (_disposed) return;
                AddHook(User32.EventObjectShow);
                // Explorer can reveal an auto-hidden secondary taskbar by moving its
                // existing window onscreen without toggling WS_VISIBLE.
                AddHook(User32.EventObjectLocationChange);
                _installed = _hooks.Count > 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _installed = false;
            }
            finally
            {
                _ready.Set();
            }

            if (!_installed) return;

            try
            {
                while (!_disposed && User32.GetMessage(out var message, 0, 0, 0) > 0)
                {
                    User32.TranslateMessage(in message);
                    User32.DispatchMessage(in message);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // The timer remains as a fallback if the native event source stops.
            }
            finally
            {
                foreach (var hook in _hooks)
                    _ = User32.UnhookWinEvent(hook);
                _hooks.Clear();
            }
        }

        private void OnWinEvent(
            nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time)
        {
            if (_disposed
                || eventType is not (User32.EventObjectShow or User32.EventObjectLocationChange)
                || window == 0)
                return;

            try
            {
                _onTaskbarShown(window);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never allow a managed exception to cross the native callback boundary.
            }
        }

        private void AddHook(uint eventType)
        {
            var hook = User32.SetWinEventHook(
                eventType,
                eventType,
                0,
                _callback,
                0,
                0,
                User32.WineventOutOfContext);
            if (hook != 0) _hooks.Add(hook);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_hidden || _taskbarsToRestore.Count > 0)
                _ = RestoreTaskbars();
            StopWatchingTaskbarShows();
            _disposed = true;
            _refreshTimer.Dispose();
            ShouldSuppressHide = null;
        }
    }
}
