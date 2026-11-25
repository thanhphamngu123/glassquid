using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Input;

namespace Glassquid.Dock;

/// <summary>
/// Relays a right click from Glassquid's tray flyout to the matching real
/// notification-area icon so that its owning application receives the click.
/// This only opens the native overflow flyout when the target icon is hidden;
/// it does not change any Windows settings.
/// </summary>
internal static partial class TrayIconContextMenuRelay
{
    private const string OverflowWindowClass = "TopLevelWindowForOverflowXamlIsland";
    private const string NotifyItemAutomationId = "NotifyItemIcon";
    private const string ShowHiddenIconsAutomationId = "SystemTrayIcon";
    private const uint InputMouse = 0;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const int NativeOverflowWaitMilliseconds = 1800;
    private const int MenuAppearWaitMilliseconds = 2500;
    private const int MenuCloseWaitMilliseconds = 120000;
    private const int PollMilliseconds = 50;
    private static nint _stealthOverflowHwnd;
    private static int _stealthOverflowOriginalStyle;
    private static bool _stealthOverflowWasLayered;
    private static bool _stealthOverflowHasOriginalLayerAttributes;
    private static uint _stealthOverflowOriginalColorKey;
    private static byte _stealthOverflowOriginalAlpha;
    private static uint _stealthOverflowOriginalLayerFlags;
    private static nint _styledOverflowHwnd;
    private static NativeRect _styledOverflowOriginalBounds;
    private static int _styledOverflowOriginalCornerPreference;
    private static int _styledOverflowOriginalBackdropType;
    private static int _styledOverflowOriginalTransitionsDisabled;
    private static int _styledOverflowOriginalBorderColor;
    private static bool _styledOverflowHasOriginalBounds;
    private static bool _styledOverflowHasOriginalCornerPreference;
    private static bool _styledOverflowHasOriginalBackdropType;
    private static bool _styledOverflowHasOriginalTransitionsDisabled;
    private static bool _styledOverflowHasOriginalBorderColor;
    private static List<TaskbarTransparencyState>? _activeTaskbarTransparencyStates;
    private readonly record struct TaskbarTransparencyState(
        nint Window,
        bool IsTaskbarRoot,
        int OriginalStyle,
        bool WasVisible,
        bool WasLayered,
        uint OriginalColorKey,
        byte OriginalAlpha,
        uint OriginalLayerFlags);
    public enum RelayClickType
    {
        LeftClick,
        DoubleClick,
        RightClick
    }

    public static void ShowNativeContextMenu(TrayIconItem item)
    {
        RelayClick(item, RelayClickType.RightClick);
    }

    /// <summary>Dispatches the app-owned tray callback and waits for its menu.</summary>
    internal static bool RelayManagedContextMenu(
        ManagedShell.WindowsTray.NotifyIcon icon, int processId, int screenX, int screenY)
    {
        ArgumentNullException.ThrowIfNull(icon);
        if (icon.HWnd == 0 || icon.CallbackMessage == 0) return false;

        Log($"Dispatching managed context callback: title='{icon.Title}', hwnd=0x{icon.HWnd.ToInt64():X}, uid={icon.UID}, guid={icon.GUID}, message=0x{icon.CallbackMessage:X}, version={icon.Version}, target=({screenX},{screenY}).");
        var before = EnumerateVisibleWindows().Select(window => window.Handle).ToHashSet();
        var mouse = (uint)((screenX & 0xFFFF) | ((screenY & 0xFFFF) << 16));
        icon.IconMouseDown(MouseButton.Right, mouse, 500);
        icon.IconMouseUp(MouseButton.Right, mouse, 500);

        var menu = WaitForApplicationMenu(processId, screenX, screenY, before, 900);
        if (menu == 0)
        {
            Log($"Managed context callback produced no detected popup for PID {processId}.");
            return false;
        }

        Log($"Managed context callback opened window 0x{menu.ToInt64():X}; waiting for dismissal.");
        WaitForWindowToClose(menu, MenuCloseWaitMilliseconds);
        PostMessage(menu, 0 /* WM_NULL */, 0, 0);
        PostMessage(icon.HWnd, 0 /* WM_NULL */, 0, 0);
        return true;
    }

    public static bool RelayLeftClick(TrayIconItem item)
    {
        return RelayClick(item, RelayClickType.LeftClick);
    }

    public static bool RelayDoubleClick(TrayIconItem item)
    {
        return RelayClick(item, RelayClickType.DoubleClick);
    }

    public static bool RelayClick(TrayIconItem item, RelayClickType clickType)
    {
        ArgumentNullException.ThrowIfNull(item);

        Log($"RelayClick start: '{item.ToolTip}', exe: '{item.ExecutablePath}', pid: {item.ProcessId}, clickType: {clickType}");
        var wasOverflowOpen = IsOverflowOpen();
        Log($"wasOverflowOpen: {wasOverflowOpen}");
        var openedOverflow = false;

        try
        {
            // 1. First check if the icon is directly on the taskbar strip
            NativeRect? iconRect = FindTaskbarIconRectangle(item);
            Log($"FindTaskbarIconRectangle result: {(iconRect.HasValue ? iconRect.Value.ToString() : "null")}");

            // 2. If not on main taskbar, check if the icon is in the native overflow window
            if (iconRect is null)
            {
                if (!wasOverflowOpen)
                {
                    Log("Attempting ToggleNativeOverflow()...");
                    if (ToggleNativeOverflow())
                    {
                        openedOverflow = true;
                        Log("ToggleNativeOverflow succeeded, waiting for overflow to open...");
                        WaitForOverflowWindow(NativeOverflowWaitMilliseconds);
                        Log($"After wait: IsOverflowOpen={IsOverflowOpen()}");
                    }
                    else
                    {
                        Log("ToggleNativeOverflow returned false!");
                    }
                }

                iconRect = FindOverflowIconRectangle(item);
                Log($"FindOverflowIconRectangle result: {(iconRect.HasValue ? iconRect.Value.ToString() : "null")}");
            }

            // 3. Fallback to Shell_NotifyIconGetRect
            if (iconRect is null)
            {
                if (openedOverflow && clickType != RelayClickType.RightClick)
                {
                    CloseNativeOverflow();
                    openedOverflow = false;
                }
                iconRect = TryGetIconRectangle(item);
                Log($"TryGetIconRectangle fallback result: {(iconRect.HasValue ? iconRect.Value.ToString() : "null")}");
            }

            if (iconRect is null || iconRect.Value.Width < 1 || iconRect.Value.Height < 1)
            {
                Log("iconRect is null or empty. RelayClick aborting.");
                return false;
            }

            var bounds = iconRect.Value;
            var x = bounds.Left + bounds.Width / 2;
            var y = bounds.Top + bounds.Height / 2;
            Log($"Target click coordinates: ({x}, {y})");

            var hasOrigPos = GetCursorPos(out var origPos);
            try
            {
                if (SetCursorPos(x, y))
                {
                    Log($"SetCursorPos succeeded. Sending inputs for {clickType}...");
                    if (clickType == RelayClickType.RightClick)
                    {
                        NativeInput[] inputs = [MouseInput(MouseRightDown), MouseInput(MouseRightUp)];
                        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
                        Log($"SendInput sent {sent} of {inputs.Length} inputs");
                        return sent == inputs.Length;
                    }
                    else if (clickType == RelayClickType.DoubleClick)
                    {
                        NativeInput[] click1 = [MouseInput(0x0002), MouseInput(0x0004)];
                        if (SendInput((uint)click1.Length, click1, Marshal.SizeOf<NativeInput>()) != click1.Length)
                            return false;
                        Thread.Sleep(50);
                        NativeInput[] click2 = [MouseInput(0x0002), MouseInput(0x0004)];
                        return SendInput((uint)click2.Length, click2, Marshal.SizeOf<NativeInput>()) == click2.Length;
                    }
                    else // LeftClick
                    {
                        NativeInput[] click = [MouseInput(0x0002), MouseInput(0x0004)];
                        return SendInput((uint)click.Length, click, Marshal.SizeOf<NativeInput>()) == click.Length;
                    }
                }
                Log("SetCursorPos failed!");
                return false;
            }
            finally
            {
                if (clickType != RelayClickType.RightClick && hasOrigPos)
                {
                    SetCursorPos(origPos.X, origPos.Y);
                }
            }
        }
        finally
        {
            if (openedOverflow && clickType != RelayClickType.RightClick)
            {
                CloseNativeOverflow();
            }
            Log("RelayClick finished.");
        }
    }

    /// <summary>
    /// Returns true when Shell places the icon in the overflow area, false when
    /// it is on a taskbar, and null when this icon cannot be resolved.
    /// </summary>
    public static bool? IsInOverflow(TrayIconItem item)
    {
        var rect = TryGetIconRectangle(item);
        return rect is null ? null : !IsInsideTaskbar(rect.Value);
    }

    private static NativeRect? TryGetIconRectangle(TrayIconItem item)
    {
        if (item.NotifyIcon is { } managedIcon)
        {
            if (managedIcon.GUID != Guid.Empty
                && TryGetIconRectangle(0, 0, managedIcon.GUID, out var managedGuidRect))
            {
                return managedGuidRect;
            }

            if (managedIcon.HWnd != 0
                && TryGetIconRectangle(managedIcon.HWnd, managedIcon.UID, Guid.Empty, out var managedUidRect))
            {
                return managedUidRect;
            }
        }

        if (item.IconGuid is { } guid && TryGetIconRectangle(0, 0, guid, out var byGuid))
            return byGuid;

        if (item.Id is not { } id) return null;

        foreach (var process in GetCandidateProcesses(item))
        {
            try
            {
                foreach (ProcessThread thread in process.Threads)
                {
                    var windows = new List<nint>();
                    EnumThreadWindows((uint)thread.Id, (window, _) =>
                    {
                        windows.Add(window);
                        return true;
                    }, 0);

                    foreach (var window in windows)
                    {
                        if (TryGetIconRectangle(window, id, Guid.Empty, out var rect))
                            return rect;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
                or NotSupportedException or UnauthorizedAccessException)
            {
                // The owner can exit while the tray catalog is being refreshed.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static List<Process> GetCandidateProcesses(TrayIconItem item)
    {
        var processes = new List<Process>();
        var yielded = new HashSet<int>();
        Process? catalogProcess = null;
        try
        {
            catalogProcess = Process.GetProcessById(item.ProcessId);
            if (catalogProcess.MainModule?.FileName.Equals(item.ExecutablePath, StringComparison.OrdinalIgnoreCase) == true)
            {
                yielded.Add(catalogProcess.Id);
                processes.Add(catalogProcess);
                catalogProcess = null; // ownership passed to the returned list
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
            or NotSupportedException or UnauthorizedAccessException)
        {
            // Try other processes with the same executable. Electron apps often
            // register the notification icon from a helper process.
        }
        finally
        {
            catalogProcess?.Dispose();
        }

        var executableName = Path.GetFileNameWithoutExtension(item.ExecutablePath);
        foreach (var candidate in Process.GetProcessesByName(executableName))
        {
            Process? process = candidate;
            try
            {
                if (yielded.Contains(process.Id))
                {
                    process.Dispose();
                    continue;
                }

                var path = process.MainModule?.FileName;
                if (path is not null && path.Equals(item.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    yielded.Add(process.Id);
                    processes.Add(process);
                    process = null; // ownership passed to the returned list
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
                or NotSupportedException or UnauthorizedAccessException)
            {
                // Ignore protected and short-lived processes.
            }
            finally
            {
                process?.Dispose();
            }
        }

        return processes;
    }

    private static bool TryGetIconRectangle(nint window, uint id, Guid guid, out NativeRect rect)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = window,
            Id = id,
            Guid = guid,
        };

        if (Shell_NotifyIconGetRect(ref identifier, out rect) == 0)
            return rect.Width > 0 && rect.Height > 0;

        rect = default;
        return false;
    }

    internal static void Log(string message)
    {
        try { File.AppendAllText(@"C:\Users\THANHPHAM\Desktop\relay_debug.log", $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n"); } catch {}
    }

    private static NativeRect? FindOverflowIconRectangle(TrayIconItem item)
    {
        // Shell_NotifyIconGetRect can return a stale slot while Explorer is
        // animating its Windows 11 overflow island. Prefer the live UIA button
        // bounds so the synthetic click lands on the icon the user can see.
        var expectedNames = BuildStrongExpectedNames(item);
        Log($"FindOverflowIconRectangle: expectedNames=[{string.Join(", ", expectedNames)}]");

        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var overflow = FindOverflowWindow();
                if (overflow is not null)
                {
                    var elements = RunWithTimeout(() => overflow.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, NotifyItemAutomationId)),
                        1000, null);
                    if (elements is not null)
                    {
                        Log($"FindOverflowIconRectangle (attempt {attempt + 1}): found {elements.Count} elements");

                        foreach (AutomationElement element in elements)
                        {
                            var current = element.Current;
                            var name = current.Name?.Trim() ?? string.Empty;
                            var helpText = current.HelpText?.Trim() ?? string.Empty;
                            var automationId = current.AutomationId?.Trim() ?? string.Empty;
                            var className = current.ClassName?.Trim() ?? string.Empty;
                            var accessibleText = $"{name} {helpText}";
                            var rect = current.BoundingRectangle;
                            Log($"FindOverflowIconRectangle candidate: name='{name}', help='{helpText}', id='{automationId}', class='{className}', control={current.ControlType?.ProgrammaticName}, rect={rect}");

                            if (MatchesStrongTrayName(accessibleText, expectedNames))
                            {
                                if (!rect.IsEmpty && !double.IsNaN(rect.Left) && !double.IsNaN(rect.Top))
                                {
                                    Log($"FindOverflowIconRectangle MATCHED '{accessibleText}' at {rect}");
                                    return ToNativeRect(rect);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                or COMException or UnauthorizedAccessException)
            {
                Log($"FindOverflowIconRectangle attempt {attempt + 1} exception: {ex.Message}");
            }

            DelayNonBlocking(60);
        }

        var shellRect = TryGetIconRectangle(item);
        if (shellRect is not null && shellRect.Value.Width > 0 && shellRect.Value.Height > 0)
        {
            var overflow = FindOverflowWindow();
            var overflowBounds = overflow?.Current.BoundingRectangle ?? System.Windows.Rect.Empty;
            if (!overflowBounds.IsEmpty && RectIntersects(overflowBounds, shellRect.Value))
            {
                Log($"FindOverflowIconRectangle: using Shell rect inside overflow at {shellRect.Value}");
                return shellRect;
            }

            Log($"FindOverflowIconRectangle: rejected Shell rect outside visible overflow: {shellRect.Value}; overflow={overflowBounds}");
        }

        return null;
    }

    internal static bool IsNativeTrayIconAvailable(TrayIconItem item)
    {
        if (item.NotifyIcon is { HWnd: not 0, CallbackMessage: not 0 }) return true;
        if (TryGetIconRectangle(item) is not null) return true;
        if (FindTaskbarIconRectangle(item) is not null) return true;

        Log($"Filtering stale tray catalog entry: '{item.ToolTip}', exe='{item.ExecutablePath}', PID={item.ProcessId}, UID={item.Id}, GUID={item.IconGuid}; no live Shell/UIA icon found.");
        return false;
    }

    private static HashSet<string> BuildStrongExpectedNames(TrayIconItem item)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddStrongName(item.ToolTip);
        AddStrongName(Path.GetFileNameWithoutExtension(item.ExecutablePath));

        var tooltip = item.ToolTip.Trim();
        var separator = tooltip.IndexOfAny(['—', '–', '(', '[']);
        if (separator > 0) AddStrongName(tooltip[..separator]);

        void AddStrongName(string? value)
        {
            var name = string.Join(' ', (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length >= 5) names.Add(name);
        }

        if (item.ExecutablePath.Contains("unikey", StringComparison.OrdinalIgnoreCase))
        {
            names.Add("UniKey");
            names.Add("Vietnamese mode");
        }

        return names;
    }

    private static bool MatchesStrongTrayName(string accessibleText, HashSet<string> expectedNames)
    {
        var normalized = string.Join(' ', accessibleText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return expectedNames.Any(expected => normalized.Contains(expected, StringComparison.OrdinalIgnoreCase)
            || expected.Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool RectIntersects(System.Windows.Rect bounds, NativeRect rect) =>
        bounds.Left < rect.Right && bounds.Right > rect.Left
        && bounds.Top < rect.Bottom && bounds.Bottom > rect.Top;

    private static NativeRect? FindTaskbarIconRectangle(TrayIconItem item)
    {
        try
        {
            var expectedNames = BuildStrongExpectedNames(item);

            foreach (var taskbarWnd in FindTaskbarWindows())
            {
                var taskbar = AutomationElement.FromHandle(taskbarWnd);
                if (taskbar is null) continue;

                foreach (AutomationElement element in taskbar.FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.AutomationIdProperty, NotifyItemAutomationId)))
                {
                    var current = element.Current;
                    var name = $"{current.Name?.Trim()} {current.HelpText?.Trim()}";
                    if (MatchesStrongTrayName(name, expectedNames))
                    {
                        var rect = current.BoundingRectangle;
                        if (!rect.IsEmpty && !double.IsNaN(rect.Left) && !double.IsNaN(rect.Top))
                            return ToNativeRect(rect);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static bool IsInsideTaskbar(NativeRect rect)
    {
        foreach (var taskbar in FindTaskbarWindows())
        {
            if (!GetWindowRect(taskbar, out var bounds)) continue;
            if (rect.Left >= bounds.Left && rect.Top >= bounds.Top
                && rect.Right <= bounds.Right && rect.Bottom <= bounds.Bottom)
                return true;
        }

        return false;
    }

    internal static AutomationElement? FindOverflowWindow()
    {
        try
        {
            var hwnd = FindWindow(OverflowWindowClass, null);
            if (hwnd != 0)
            {
                return AutomationElement.FromHandle(hwnd);
            }

            var condition = new PropertyCondition(AutomationElement.ClassNameProperty, OverflowWindowClass);
            var element = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
            if (element is not null) return element;

            // Some Windows builds expose a localized or changed class name but
            // retain the stable overflow window title.
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement window in windows)
            {
                var name = window.Current.Name;
                if (name.Contains("overflow", StringComparison.OrdinalIgnoreCase)
                    || (name.Contains("biểu tượng", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("khay", StringComparison.OrdinalIgnoreCase)))
                    return window;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // Explorer may be rebuilding the taskbar automation tree.
        }

        return null;
    }

    internal static bool IsOverflowOpen()
    {
        var overflow = FindOverflowWindow();
        if (overflow is null) return false;
        try
        {
            var items = overflow.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, NotifyItemAutomationId));
            return items.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsNativeOverflowOpen() => IsOverflowOpen();

    internal static bool IsNativeOverflowVisible()
    {
        var hwnd = FindWindow(OverflowWindowClass, null);
        return hwnd != 0 && IsWindowVisible(hwnd)
            && (DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) != 0 || cloaked == 0);
    }

    /// <summary>
    /// Repositions Windows' own hidden tray popup over Glassquid's tray anchor and
    /// opts the transient shell window into the same acrylic, rounded treatment as
    /// the dock. The tray icons and their callbacks remain owned by Explorer.
    /// </summary>
    internal static bool TryStyleAndPositionNativeOverflow(int anchorX, int anchorY)
    {
        var hwnd = FindWindow(OverflowWindowClass, null);
        if (hwnd == 0 || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var bounds)
            || bounds.Width <= 0 || bounds.Height <= 0) return false;

        CaptureNativeOverflowStyle(hwnd, bounds);

        var width = Math.Max(1, bounds.Width);
        var height = Math.Max(1, bounds.Height);
        var monitor = GetNativeOverflowAnchorMonitor(anchorX, anchorY);
        var screenLeft = monitor.Left;
        var screenTop = monitor.Top;
        var screenWidth = monitor.Width;
        var screenHeight = monitor.Height;
        var scale = Math.Max(1, GetDpiForWindow(hwnd)) / 96d;
        var padXPhysical = (int)Math.Round(12 * scale);
        var padYPhysical = (int)Math.Round(8 * scale);
        var x = anchorX - width / 2;
        var y = anchorY - height - 10 - padYPhysical;
        if (y < screenTop + padYPhysical) y = anchorY + 10 + padYPhysical;
        x = Math.Clamp(x, screenLeft + padXPhysical, Math.Max(screenLeft + padXPhysical, screenLeft + screenWidth - width - padXPhysical));
        y = Math.Clamp(y, screenTop + padYPhysical, Math.Max(screenTop + padYPhysical, screenTop + screenHeight - height - padYPhysical));

        // Glassquid's shared dock material supplies the chrome. A second DWM
        // backdrop on Explorer's transparent outer margin produces a thick gray rim.
        var cornerPreference = 2; // DWMWCP_ROUND
        var backdropType = 1; // DWMSBT_NONE
        _ = DwmSetWindowAttribute(hwnd, 33, ref cornerPreference, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, 38, ref backdropType, sizeof(int));
        var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int));

        // Keep one HWND visibility transition and a fixed size. Hiding/showing the
        // XAML island here restarts Explorer's opening animation at a second anchor.
        return SetWindowPos(hwnd, 0, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
    }

    internal static void ResetNativeOverflowAppearance()
    {
        RestoreNativeOverflowStyle();

        // A previous Glassquid process or state transition may have touched
        // Explorer's reused overflow HWND. Return it unconditionally to Windows defaults.
        var hwnd = FindWindow(OverflowWindowClass, null);
        if (hwnd == 0 || !IsWindow(hwnd)) return;

        _ = SetWindowRgn(hwnd, 0, true);

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_LAYERED) != 0 || GetLayeredWindowAttributes(hwnd, out _, out _, out _))
        {
            _ = SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
            _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        var defaultCornerPreference = 0; // DWMWCP_DEFAULT
        var defaultBackdropType = 0; // DWMSBT_AUTO
        _ = DwmSetWindowAttribute(hwnd, 33, ref defaultCornerPreference, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, 38, ref defaultBackdropType, sizeof(int));
        var defaultBorder = -1; // DWMWA_COLOR_DEFAULT
        _ = DwmSetWindowAttribute(hwnd, 34, ref defaultBorder, sizeof(int));
        var transitionsDisabled = 0;
        _ = DwmSetWindowAttribute(hwnd, 3, ref transitionsDisabled, sizeof(int));
    }

    private static void CaptureNativeOverflowStyle(nint hwnd, NativeRect bounds)
    {
        if (_styledOverflowHwnd != hwnd)
        {
            RestoreStyledNativeOverflowWindow();
            _styledOverflowHwnd = hwnd;
        }

        if (!_styledOverflowHasOriginalBounds && bounds.Width > 0 && bounds.Height > 0)
        {
            _styledOverflowOriginalBounds = bounds;
            _styledOverflowHasOriginalBounds = true;
        }
        if (!_styledOverflowHasOriginalCornerPreference)
        {
            _styledOverflowHasOriginalCornerPreference =
                DwmGetWindowAttribute(hwnd, 33, out _styledOverflowOriginalCornerPreference, sizeof(int)) == 0;
        }
        if (!_styledOverflowHasOriginalBackdropType)
        {
            _styledOverflowHasOriginalBackdropType =
                DwmGetWindowAttribute(hwnd, 38, out _styledOverflowOriginalBackdropType, sizeof(int)) == 0;
        }
        if (!_styledOverflowHasOriginalTransitionsDisabled)
        {
            _styledOverflowHasOriginalTransitionsDisabled =
                DwmGetWindowAttribute(hwnd, 3, out _styledOverflowOriginalTransitionsDisabled, sizeof(int)) == 0;
        }
        if (!_styledOverflowHasOriginalBorderColor)
            _styledOverflowHasOriginalBorderColor =
                DwmGetWindowAttribute(hwnd, 34, out _styledOverflowOriginalBorderColor, sizeof(int)) == 0;

        var transitionsDisabled = 1;
        _ = DwmSetWindowAttribute(hwnd, 3, ref transitionsDisabled, sizeof(int));
    }

    private static void RestoreStyledNativeOverflowWindow()
    {
        var hwnd = _styledOverflowHwnd;
        try
        {
            if (hwnd != 0 && IsWindow(hwnd))
            {
                _ = SetWindowRgn(hwnd, 0, true);
                var cornerPreference = _styledOverflowHasOriginalCornerPreference
                    ? _styledOverflowOriginalCornerPreference : 0;
                var backdropType = _styledOverflowHasOriginalBackdropType
                    ? _styledOverflowOriginalBackdropType : 0;
                _ = DwmSetWindowAttribute(hwnd, 33, ref cornerPreference, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, 38, ref backdropType, sizeof(int));
                var borderColor = _styledOverflowHasOriginalBorderColor ? _styledOverflowOriginalBorderColor : -1;
                _ = DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));

                if (_styledOverflowHasOriginalBounds && !IsWindowVisible(hwnd))
                {
                    _ = SetWindowPos(hwnd, 0,
                        _styledOverflowOriginalBounds.Left, _styledOverflowOriginalBounds.Top,
                        _styledOverflowOriginalBounds.Width, _styledOverflowOriginalBounds.Height,
                        SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED);
                }
                if (_styledOverflowHasOriginalTransitionsDisabled)
                {
                    _ = DwmSetWindowAttribute(hwnd, 3,
                        ref _styledOverflowOriginalTransitionsDisabled, sizeof(int));
                }
            }
        }
        catch { }
        finally
        {
            _styledOverflowHwnd = 0;
            _styledOverflowOriginalBounds = default;
            _styledOverflowOriginalCornerPreference = 0;
            _styledOverflowOriginalBackdropType = 0;
            _styledOverflowOriginalTransitionsDisabled = 0;
            _styledOverflowOriginalBorderColor = 0;
            _styledOverflowHasOriginalBounds = false;
            _styledOverflowHasOriginalCornerPreference = false;
            _styledOverflowHasOriginalBackdropType = false;
            _styledOverflowHasOriginalTransitionsDisabled = false;
            _styledOverflowHasOriginalBorderColor = false;
        }
    }

    internal static bool ToggleNativeOverflow(
        bool allowKeyboardFallback = true,
        bool taskbarsMustStayHidden = false,
        bool expectOverflowOpen = true)
    {
        // Glassquid's own chevron is used while Explorer taskbars are hidden. Guard
        // the direct UI Automation invocation too: on some Windows builds it can
        // make Explorer reveal all monitor taskbars even without our ShowWindow call.
        if (!allowKeyboardFallback)
            return TryInvokeChevronWithoutTaskbarFlash(taskbarsMustStayHidden, expectOverflowOpen);

        if (TryInvokeChevronButton(focusChevron: expectOverflowOpen)) return true;

        try
        {
            var hwnd = FindWindow("Shell_TrayWnd", null);
            if (hwnd == 0) return false;
            var taskbar = AutomationElement.FromHandle(hwnd);
            if (taskbar is null) return false;

            var candidates = taskbar.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, ShowHiddenIconsAutomationId));
            foreach (AutomationElement button in candidates)
            {
                if (!button.Current.ControlType.Equals(ControlType.Button)) continue;
                var name = button.Current.Name ?? string.Empty;
                if (IsNativeChevronCandidate(button.Current.AutomationId ?? string.Empty, name))
                {
                    var rect = button.Current.BoundingRectangle;
                    if (!rect.IsEmpty && SetCursorPos((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2)))
                    {
                        NativeInput[] inputs = [MouseInput(0x0002), MouseInput(0x0004)];
                        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == inputs.Length;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or COMException or UnauthorizedAccessException or ArgumentException)
        {
            // If the native button is unavailable, do not synthesize clicks elsewhere.
        }

        // Win+B can focus any remembered notification icon. Reveal the taskbar
        // under the existing transparency guard and invoke only its real chevron.
        return TryInvokeChevronWithoutTaskbarFlash(taskbarsMustStayHidden, expectOverflowOpen);
    }

    internal static nint FindTaskbarHwnd() => FindWindow("Shell_TrayWnd", null);

    internal static bool IsNativeTrayWindow(nint window)
    {
        if (window == 0) return false;
        var root = GetAncestor(window, 2); // GA_ROOT includes tray child controls.
        if (root == 0) root = window;
        var name = new char[128];
        var length = GetClassName(root, name, name.Length);
        if (length <= 0) return false;
        return new string(name, 0, length) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or OverflowWindowClass or "NotifyIconOverflowWindow";
    }

    internal static bool InvokeNativeChevronForScan() => TryInvokeChevronButton();

    internal static bool CloseNativeOverflowForScan()
    {
        if (!IsOverflowOpen()) return true;
        return TryInvokeChevronButton();
    }

    private static bool TryInvokeChevronButton(bool focusChevron = false)
    {
        try
        {
            var hwnd = FindWindow("Shell_TrayWnd", null);
            if (hwnd == 0) return false;
            var taskbar = AutomationElement.FromHandle(hwnd);
            if (taskbar is null) return false;

            var candidates = taskbar.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, ShowHiddenIconsAutomationId));
            foreach (AutomationElement button in candidates)
            {
                if (!button.Current.ControlType.Equals(ControlType.Button)) continue;
                var name = button.Current.Name ?? string.Empty;
                var autoId = button.Current.AutomationId ?? string.Empty;
                if (!IsNativeChevronCandidate(autoId, name)) continue;
                Log($"Chevron candidate: id='{autoId}', name='{name}', rect={button.Current.BoundingRectangle}.");
                if (InvokeChevronElement(button, focusChevron)) return true;
            }

            // Fallback: search by AutomationId "ShowMoreIcons" or similar
            var fallbackCandidates = taskbar.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in fallbackCandidates)
            {
                var name = button.Current.Name ?? string.Empty;
                var autoId = button.Current.AutomationId ?? string.Empty;
                if (IsNativeChevronCandidate(autoId, name))
                {
                    if (InvokeChevronElement(button, focusChevron)) return true;
                    var rect = button.Current.BoundingRectangle;
                    if (!rect.IsEmpty && SetCursorPos((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2)))
                    {
                        NativeInput[] inputs = [MouseInput(0x0002), MouseInput(0x0004)];
                        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == inputs.Length;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or COMException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return false;
    }

    private static bool InvokeChevronElement(AutomationElement button, bool focusChevron)
    {
        if (focusChevron)
        {
            try
            {
                if (button.Current.IsKeyboardFocusable) button.SetFocus();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                // Invocation still works when Explorer declines keyboard focus.
            }
        }
        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern)
            && invokePattern is InvokePattern invoke)
        {
            invoke.Invoke();
            return true;
        }
        if (button.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern)
            && togglePattern is TogglePattern toggle)
        {
            toggle.Toggle();
            return true;
        }
        if (button.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern)
            && expandPattern is ExpandCollapsePattern expand)
        {
            expand.Expand();
            return true;
        }
        return false;
    }

    internal static bool IsNativeChevronCandidate(string automationId, string name) =>
        automationId.Equals("ShowMoreIcons", StringComparison.OrdinalIgnoreCase)
        || automationId.Equals("SystemTrayOverflow", StringComparison.OrdinalIgnoreCase)
        || name.Contains("hidden icons", StringComparison.OrdinalIgnoreCase)
        || name.Contains("more icons", StringComparison.OrdinalIgnoreCase)
        || name.Contains("notification overflow", StringComparison.OrdinalIgnoreCase)
        || name.Equals("chevron", StringComparison.OrdinalIgnoreCase)
        || name.Contains("biểu tượng ẩn", StringComparison.OrdinalIgnoreCase)
        || name.Contains("biểu tượng bị ẩn", StringComparison.OrdinalIgnoreCase);

    private static void SendWinB()
    {
        keybd_event(0x5B, 0, 0, 0); // LWIN down
        keybd_event(0x42, 0, 0, 0); // B down
        keybd_event(0x42, 0, 2, 0); // B up
        keybd_event(0x5B, 0, 2, 0); // LWIN up
    }

    private static bool TryInvokeChevronWithoutTaskbarFlash(bool taskbarsMustStayHidden, bool expectOverflowOpen)
    {
        // Glassquid explicitly hides the taskbar HWNDs, which can remove the chevron
        // from Explorer's UIA tree. Make taskbars transparent while invoking the real
        // control; as a fallback, expose only the primary HWND under the same guard.
        // Keep Explorer's appbar auto-hide flag untouched here: changing it, even briefly,
        // makes Explorer recalculate taskbar/work-area geometry and can resize app windows.
        var primaryTaskbar = FindWindow("Shell_TrayWnd", null);
        if (primaryTaskbar == 0) return false;

        var taskbarRoots = FindTaskbarWindows()
            .Where(IsWindow)
            .Append(primaryTaskbar)
            .Distinct()
            .ToArray();
        var guardedWindows = new List<(nint Window, bool IsTaskbarRoot)>();
        foreach (var root in taskbarRoots)
        {
            guardedWindows.Add((root, true));
            _ = EnumChildWindows(root, (child, _) =>
            {
                guardedWindows.Add((child, false));
                return true;
            }, 0);
        }
        var states = new List<TaskbarTransparencyState>(guardedWindows.Count);
        var hadOriginalCursor = GetCursorPos(out var originalCursor);
        try
        {
            foreach (var (window, isTaskbarRoot) in guardedWindows.Distinct())
            {
                var originalStyle = GetWindowLong(window, GWL_EXSTYLE);
                var wasVisible = IsWindowVisible(window);
                var wasLayered = (originalStyle & WS_EX_LAYERED) != 0;
                uint originalColorKey = 0;
                byte originalAlpha = 255;
                uint originalLayerFlags = 0;
                if (wasLayered && !GetLayeredWindowAttributes(window, out originalColorKey,
                        out originalAlpha, out originalLayerFlags))
                    return false;

                if (wasLayered && originalAlpha == 0)
                {
                    wasLayered = false;
                    originalAlpha = 255;
                    originalStyle &= ~WS_EX_LAYERED;
                }

                states.Add(new TaskbarTransparencyState(window, isTaskbarRoot, originalStyle, wasVisible,
                    wasLayered, originalColorKey, originalAlpha, originalLayerFlags));

                if (!wasLayered)
                {
                    _ = SetWindowLong(window, GWL_EXSTYLE, originalStyle | WS_EX_LAYERED);
                    _ = SetWindowPos(window, 0, 0, 0, 0, 0,
                        SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }

                var transparentFlags = wasLayered ? originalLayerFlags | (uint)LWA_ALPHA : (uint)LWA_ALPHA;
                if (!SetLayeredWindowAttributes(window, originalColorKey, 0, transparentFlags))
                    return false;
            }

            // Ensure primary taskbar is shown with alpha = 0 so its controls exist
            _ = ShowWindow(primaryTaskbar, SW_SHOWNOACTIVATE);
            if (!IsWindowVisible(primaryTaskbar)) return false;

            // 1. Try direct UI Automation invocation
            var invoked = TryInvokeChevronButton(focusChevron: expectOverflowOpen);
            if (invoked)
            {
                if (expectOverflowOpen) WaitForOverflowWindow(250);
                else WaitForOverflowClose(250);
            }

            // 2. If opening and not yet open: taskbar may be auto-hidden and collapsed.
            // Send Win+B to instruct Explorer to unhide/expand the auto-hidden tray
            // and focus the notification chevron. The taskbar is 100% transparent (alpha = 0),
            // so this expansion is completely invisible to the user.
            if (expectOverflowOpen && !IsOverflowOpen())
            {
                SendWinB();
                Thread.Sleep(100);

                // Now that the auto-hidden taskbar has expanded, retry UIA button invoke
                invoked = TryInvokeChevronButton(focusChevron: true);
                if (invoked)
                {
                    WaitForOverflowWindow(300);
                }

                // Do not send activation keys: Win+B may have selected an app
                // such as Copilot instead of the hidden-icons chevron.
            }
            else if (!expectOverflowOpen && IsOverflowOpen())
            {
                // Closing fallback: send Escape
                keybd_event(0x1B, 0, 0, 0); // VK_ESCAPE down
                keybd_event(0x1B, 0, 2, 0); // VK_ESCAPE up
                WaitForOverflowClose(250);
            }

            var completed = expectOverflowOpen ? IsOverflowOpen() : !IsOverflowOpen();
            Log(completed
                ? expectOverflowOpen
                    ? "Show-hidden-icons opened successfully."
                    : "Show-hidden-icons closed successfully."
                : expectOverflowOpen
                    ? "Show-hidden-icons could not be opened."
                    : "Show-hidden-icons could not be closed.");
            return completed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException
            or UnauthorizedAccessException)
        {
            Log($"Show-hidden-icons guarded invocation failed: {ex.Message}");
            return false;
        }
        finally
        {
            var overflowRemainsOpen = IsOverflowOpen();
            var keepTransparentForOverflow = overflowRemainsOpen && taskbarsMustStayHidden;
            if (keepTransparentForOverflow)
            {
                // TopLevelWindowForOverflowXamlIsland is owned by Shell_TrayWnd.
                // Keep Shell_TrayWnd visible but completely transparent (alpha = 0)
                // while the overflow is open. Save transparency states for cleanup on close.
                _activeTaskbarTransparencyStates = states;
            }
            else
            {
                foreach (var state in states)
                {
                    if (!IsWindow(state.Window)) continue;

                    if (state.IsTaskbarRoot)
                    {
                        // During close, the guarded Explorer taskbar was visible only
                        // as a transparent host for the overflow. Hide it before
                        // restoring its original style so no frame flashes onscreen.
                        var shouldBeVisible = taskbarsMustStayHidden ? false : state.WasVisible;
                        if (IsWindowVisible(state.Window) != shouldBeVisible)
                            _ = ShowWindow(state.Window, shouldBeVisible ? SW_SHOWNOACTIVATE : SW_HIDE);
                    }
                }

                foreach (var state in states)
                {
                    if (!IsWindow(state.Window)) continue;
                    if (state.WasLayered && state.OriginalAlpha > 0)
                    {
                        _ = SetLayeredWindowAttributes(state.Window, state.OriginalColorKey,
                            state.OriginalAlpha, state.OriginalLayerFlags);
                    }
                    else
                    {
                        _ = SetLayeredWindowAttributes(state.Window, 0, 255, LWA_ALPHA);
                        _ = SetWindowLong(state.Window, GWL_EXSTYLE, state.OriginalStyle & ~WS_EX_LAYERED);
                    }

                    _ = SetWindowPos(state.Window, 0, 0, 0, 0, 0,
                        SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
            }

            if (hadOriginalCursor) _ = SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    public const uint WM_MYICON_NOTIFY = 1025; // WM_USER + 1
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;

    /// <summary>
    /// Shows the context menu for a tray icon right at the specified screen position (next to Glassquid's button),
    /// without showing the Windows 11 native overflow flyout.
    /// </summary>
    public static void ShowContextMenuAtPosition(TrayIconItem item, int targetScreenX, int targetScreenY)
    {
        ArgumentNullException.ThrowIfNull(item);
        Log($"ShowContextMenuAtPosition: '{item.ToolTip}' at ({targetScreenX},{targetScreenY})");

        // Set cursor position at the Glassquid icon so any app querying GetCursorPos opens near here
        SetCursorPos(targetScreenX, targetScreenY);

        // 1. UniKey: Direct message path (fastest, cleanest, no overflow needed)
        if (IsUniKeyApp(item))
        {
            var hwnd = FindUniKeyWindow();
            if (hwnd != 0)
            {
                AllowSetForegroundWindow(item.ProcessId);
                SetForegroundWindow(hwnd);
                Thread.Sleep(30);
                PostMessage(hwnd, WM_MYICON_NOTIFY, (nint)(item.Id ?? 0), (nint)WM_RBUTTONUP);
                Log("UniKey context menu triggered via direct message");
                WaitForPopupMenuDismissed(targetScreenX, targetScreenY, 600, 60000);
                // Microsoft KB Q135788: Post WM_NULL to ensure popup menu dismisses properly on click-away
                PostMessage(hwnd, 0 /* WM_NULL */, 0, 0);
                return;
            }
        }

        // 2. All other apps: Relay through the native tray icon, and reposition the resulting
        // context menu next to Glassquid without altering the native overflow window's styling.
        RelayRightClickWithMenuReposition(item, targetScreenX, targetScreenY);
    }

    private static void RelayRightClickWithMenuReposition(TrayIconItem item, int targetScreenX, int targetScreenY)
    {
        var beforeWindows = EnumerateVisibleWindows();
        var openedOverflow = false;
        var hasOriginalCursor = GetCursorPos(out var originalCursor);
        var cursorRestored = false;

        try
        {
            // First check if the icon is directly on taskbar
            var iconRect = FindTaskbarIconRectangle(item);

            // If not on taskbar, open the overflow window
            if (iconRect is null)
            {
                var overflowWindow = FindWindow(OverflowWindowClass, null);
                if (overflowWindow != 0) MakeNativeOverflowNearlyInvisible(overflowWindow);

                if (!IsOverflowOpen())
                {
                    Log("Opening native overflow...");
                    if (ToggleNativeOverflow())
                    {
                        openedOverflow = true;
                        // Wait briefly for overflow window to appear
                        WaitForOverflowWindow(NativeOverflowWaitMilliseconds);
                    }
                }

                overflowWindow = FindWindow(OverflowWindowClass, null);
                if (overflowWindow != 0 && IsOverflowOpen()) MakeNativeOverflowNearlyInvisible(overflowWindow);
                iconRect = FindOverflowIconRectangle(item);
            }

            if (iconRect is null || iconRect.Value.Width < 1 || iconRect.Value.Height < 1)
            {
                Log("Could not find icon bounds for right click relay");
                if (openedOverflow) CloseNativeOverflow();
                return;
            }

            var bounds = iconRect.Value;
            var clickX = bounds.Left + bounds.Width / 2;
            var clickY = bounds.Top + bounds.Height / 2;
            Log($"Found icon at ({clickX}, {clickY})");

            // Move cursor to icon in overflow and right-click
            SetCursorPos(clickX, clickY);

            Debug.WriteLine($"[Relay] rect={iconRect} click=({clickX},{clickY}) IsOverflowOpen={IsOverflowOpen()}");
            try { File.AppendAllText(@"C:\Users\THANHPHAM\Desktop\relay_debug.log", $"[{DateTime.Now:HH:mm:ss.fff}] [Relay] rect={iconRect} click=({clickX},{clickY}) IsOverflowOpen={IsOverflowOpen()}\n"); } catch {}

            NativeInput[] inputs = [MouseInput(MouseRightDown), MouseInput(MouseRightUp)];
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());

            Debug.WriteLine($"[Relay] sent={sent} err={Marshal.GetLastWin32Error()}");
            try { File.AppendAllText(@"C:\Users\THANHPHAM\Desktop\relay_debug.log", $"[{DateTime.Now:HH:mm:ss.fff}] [Relay] sent={sent} err={Marshal.GetLastWin32Error()}\n"); } catch {}

            nint overflowHwnd = FindWindow(OverflowWindowClass, null);

            // Win32 context menus use a separate #32768 window. XAML and WPF
            // menus can instead be tracked by the owning GUI thread's menu mode.
            nint menuHwnd = 0;
            var sw = Stopwatch.StartNew();
            var activeMenu = false;
            while (sw.ElapsedMilliseconds < MenuAppearWaitMilliseconds)
            {
                var visible = EnumerateVisibleWindows();
                var newWnd = visible
                    .Where(w => !beforeWindows.Any(b => b.Handle == w.Handle)
                        && (w.ProcessId == item.ProcessId || IsMenuWindowClass(w.ClassName))
                        && IsNear(w.Bounds, clickX, clickY, 600)
                        && w.Handle != overflowHwnd)
                    .OrderBy(w => IsMenuWindowClass(w.ClassName) ? 0 : 1)
                    .FirstOrDefault();

                if (newWnd is not null)
                {
                    menuHwnd = newWnd.Handle;
                    Log($"Detected newly created menu HWND=0x{menuHwnd.ToInt64():X} class='{newWnd.ClassName}' bounds={newWnd.Bounds}");
                    break;
                }

                var applicationMenuActive = IsApplicationInMenuMode(item.ProcessId, out var menuOwner);
                if (applicationMenuActive && !activeMenu)
                {
                    Log($"Application entered native menu mode; owner HWND=0x{menuOwner.ToInt64():X}.");
                }
                activeMenu |= applicationMenuActive;

                if (IsContextMenuAtPoint(clickX, clickY, out var menuDescription))
                {
                    if (!activeMenu)
                    {
                        Log($"UI Automation detected a context menu at the tray icon: {menuDescription}.");
                    }
                    activeMenu = true;
                }

                // Some apps enter menu mode before creating their separate #32768
                // popup HWND. Keep polling during the appearance window so we can
                // move that popup beside Glassquid instead of leaving it at the
                // Windows notification area.
                Thread.Sleep(20);
            }

            Debug.WriteLine($"[Relay] menuHwnd={menuHwnd:X} elapsed={sw.ElapsedMilliseconds}ms");
            try { File.AppendAllText(@"C:\Users\THANHPHAM\Desktop\relay_debug.log", $"[{DateTime.Now:HH:mm:ss.fff}] [Relay] menuHwnd={menuHwnd:X} elapsed={sw.ElapsedMilliseconds}ms\n"); } catch {}

            if (menuHwnd != 0)
            {
                RepositionMenuNearPoint(menuHwnd, targetScreenX, targetScreenY);
                if (hasOriginalCursor)
                {
                    SetCursorPos(originalCursor.X, originalCursor.Y);
                    cursorRestored = true;
                }

                WaitForWindowToClose(menuHwnd, 60000);
                // Microsoft KB Q135788: Post WM_NULL to force a task switch so menu dismisses cleanly
                PostMessage(menuHwnd, 0 /* WM_NULL */, 0, 0);
            }
            else if (activeMenu)
            {
                if (hasOriginalCursor)
                {
                    SetCursorPos(originalCursor.X, originalCursor.Y);
                    cursorRestored = true;
                }

                WaitForApplicationMenuModeToEnd(item.ProcessId, clickX, clickY, MenuCloseWaitMilliseconds);
            }
            else
            {
                Log("No context menu window appeared after the native tray click.");
            }

            if (hasOriginalCursor && !cursorRestored)
            {
                SetCursorPos(originalCursor.X, originalCursor.Y);
                cursorRestored = true;
            }
        }
        catch (Exception ex)
        {
            Log($"RelayRightClickWithMenuReposition exception: {ex}");
        }
        finally
        {
            if (hasOriginalCursor && !cursorRestored)
            {
                SetCursorPos(originalCursor.X, originalCursor.Y);
            }

            if (openedOverflow || _stealthOverflowHwnd != 0)
            {
                CloseNativeOverflow();
            }
        }
    }

    private static void RepositionMenuNearPoint(nint menuHwnd, int anchorX, int anchorY)
    {
        if (!GetWindowRect(menuHwnd, out var bounds)) return;

        var width = Math.Max(1, bounds.Width);
        var height = Math.Max(1, bounds.Height);
        var screenLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var screenTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var screenWidth = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var screenHeight = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        var x = Math.Clamp(anchorX, screenLeft, Math.Max(screenLeft, screenLeft + screenWidth - width));
        var y = anchorY;
        if (y + height > screenTop + screenHeight) y = anchorY - height;
        y = Math.Clamp(y, screenTop, Math.Max(screenTop, screenTop + screenHeight - height));

        _ = SetWindowPos(menuHwnd, 0, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
    }

    internal static void RestoreNativeOverflowStyle(bool immediately = false)
    {
        CancelNativeOverflowMotion();
        StopNativeOverflowPresentationGuard();
        if (!immediately && DeferNativeOverflowRestoreWhileVisible()) return;
        RestoreNativeOverflowStyleNow();
    }

    private static void RestoreNativeOverflowStyleNow()
    {
        StopNativeTrayKeyboardGuard();
        CloseNativeTrayGlass();
        _overflowRestoreTimer?.Stop();
        _overflowPresentationClosing = false;
        _overflowDockBounds = null;
        RestoreStyledNativeOverflowWindow();
        try
        {
            var hwnd = _stealthOverflowHwnd;
            if (hwnd != 0 && IsWindow(hwnd))
            {
                if (_stealthOverflowWasLayered && _stealthOverflowOriginalAlpha > 1)
                {
                    if (_stealthOverflowHasOriginalLayerAttributes)
                    {
                        _ = SetLayeredWindowAttributes(hwnd, _stealthOverflowOriginalColorKey,
                            _stealthOverflowOriginalAlpha, _stealthOverflowOriginalLayerFlags);
                    }
                }
                else
                {
                    _ = SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                    _ = SetWindowLong(hwnd, GWL_EXSTYLE, _stealthOverflowOriginalStyle & ~WS_EX_LAYERED);
                }

                _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
        }
        catch { }
        finally
        {
            _stealthOverflowHwnd = 0;
            _stealthOverflowOriginalStyle = 0;
            _stealthOverflowWasLayered = false;
            _stealthOverflowHasOriginalLayerAttributes = false;
            _stealthOverflowOriginalColorKey = 0;
            _stealthOverflowOriginalAlpha = 255;
            _stealthOverflowOriginalLayerFlags = 0;
        }
    }

    private static bool MakeNativeOverflowNearlyInvisible(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return false;
        if (_stealthOverflowHwnd == hwnd) return true;

        var originalStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        var wasLayered = (originalStyle & WS_EX_LAYERED) != 0;
        uint colorKey = 0;
        byte alpha = 255;
        uint originalLayerFlags = 0;
        var hasLayerAttributes = false;

        if (wasLayered)
        {
            hasLayerAttributes = GetLayeredWindowAttributes(hwnd, out colorKey, out alpha, out originalLayerFlags);
            if (!hasLayerAttributes)
            {
                Log("Could not inspect existing overflow window transparency; leaving its appearance unchanged.");
                return false;
            }
            if (alpha <= 1)
            {
                wasLayered = false;
                alpha = 255;
                originalStyle &= ~WS_EX_LAYERED;
            }
        }
        else
        {
            _ = SetWindowLong(hwnd, GWL_EXSTYLE, originalStyle | WS_EX_LAYERED);
            _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        // Alpha 1 makes the bridge almost invisible while keeping it hit-testable.
        // Alpha 0 would pass mouse events through the layered window.
        var relayLayerFlags = wasLayered ? originalLayerFlags | (uint)LWA_ALPHA : (uint)LWA_ALPHA;
        if (!SetLayeredWindowAttributes(hwnd, colorKey, 1, relayLayerFlags))
        {
            if (!wasLayered)
            {
                _ = SetWindowLong(hwnd, GWL_EXSTYLE, originalStyle);
                _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }

            Log($"Failed to set the Windows tray relay opacity (error={Marshal.GetLastWin32Error()}).");
            return false;
        }

        _stealthOverflowHwnd = hwnd;
        _stealthOverflowOriginalStyle = originalStyle;
        _stealthOverflowWasLayered = wasLayered;
        _stealthOverflowHasOriginalLayerAttributes = hasLayerAttributes;
        _stealthOverflowOriginalColorKey = colorKey;
        _stealthOverflowOriginalAlpha = alpha;
        _stealthOverflowOriginalLayerFlags = originalLayerFlags;
        Log($"Windows tray relay flyout 0x{hwnd.ToInt64():X} is nearly transparent.");
        return true;
    }

    /// <summary>
    /// Find all windows (visible or hidden) belonging to a given process.
    /// </summary>
    private static List<nint> FindProcessWindows(int processId)
    {
        var windows = new List<nint>();
        try
        {
            using var proc = Process.GetProcessById(processId);
            foreach (ProcessThread thread in proc.Threads)
            {
                EnumThreadWindows((uint)thread.Id, (wnd, _) =>
                {
                    windows.Add(wnd);
                    return true;
                }, 0);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or Win32Exception or NotSupportedException)
        {
            // Process may have exited
        }
        return windows;
    }

    /// <summary>
    /// Looks for a popup menu window (#32768 class) near the given screen coordinates.
    /// </summary>
    private static nint FindPopupMenuNear(int x, int y, int maxDistance)
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var className = new char[128];
            var len = GetClassName(window, className, className.Length);
            if (len <= 0) return true;
            var name = new string(className, 0, len);
            if (name != "#32768" && !name.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal))
                return true;
            if (!GetWindowRect(window, out var bounds)) return true;
            if (IsNear(bounds, x, y, maxDistance))
            {
                found = window;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    /// <summary>
    /// Waits until no popup menu window is visible near the given coordinates.
    /// Used to keep the Glassquid popup open while the app's context menu is showing.
    /// </summary>
    public static void WaitForPopupMenuDismissed(int x, int y, int maxDistance, int timeoutMilliseconds)
    {
        // First wait a moment for the menu to appear without blocking UI
        DelayNonBlocking(400);

        var elapsed = 0;
        while (elapsed < timeoutMilliseconds)
        {
            var menu = FindPopupMenuNear(x, y, maxDistance);
            if (menu == 0)
            {
                Log("Popup menu dismissed");
                return;
            }

            DelayNonBlocking(100);
            elapsed += 100;
        }

        Log($"WaitForPopupMenuDismissed timed out after {timeoutMilliseconds}ms");
    }

    private static bool IsUniKeyApp(TrayIconItem item) =>
        item.ExecutablePath.Contains("unikey", StringComparison.OrdinalIgnoreCase)
        || item.ToolTip.Contains("unikey", StringComparison.OrdinalIgnoreCase);

    public static bool ShowUniKeyContextMenu(nint hwnd)
    {
        if (hwnd != 0)
        {
            return PostMessage(hwnd, WM_MYICON_NOTIFY, 0, (nint)WM_RBUTTONUP);
        }
        return false;
    }

    public static nint FindUniKeyWindow()
    {
        var hwnd = FindWindow("UniKey MainWnd", null);
        if (hwnd != 0) return hwnd;

        foreach (var proc in Process.GetProcessesByName("UniKeyNT"))
        {
            try
            {
                foreach (ProcessThread thread in proc.Threads)
                {
                    nint found = 0;
                    EnumThreadWindows((uint)thread.Id, (wnd, _) =>
                    {
                        var className = new char[128];
                        var len = GetClassName(wnd, className, className.Length);
                        if (len > 0 && new string(className, 0, len).Equals("UniKey MainWnd", StringComparison.OrdinalIgnoreCase))
                        {
                            found = wnd;
                            return false;
                        }
                        return true;
                    }, 0);
                    if (found != 0) return found;
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return 0;
    }

    public static bool ToggleUniKeyDirectly()
    {
        var hwnd = FindUniKeyWindow();
        if (hwnd != 0)
        {
            return PostMessage(hwnd, WM_MYICON_NOTIFY, 0, (nint)WM_LBUTTONDOWN);
        }
        return false;
    }

    public static bool OpenUniKeyPanelDirectly()
    {
        var hwnd = FindUniKeyWindow();
        if (hwnd != 0)
        {
            // Sending WM_LBUTTONDOWN followed by WM_LBUTTONDBLCLK simulates the native double click
            // which restores mode and triggers ID_KEY_PANEL
            _ = PostMessage(hwnd, WM_MYICON_NOTIFY, 0, (nint)WM_LBUTTONDOWN);
            Thread.Sleep(30);
            return PostMessage(hwnd, WM_MYICON_NOTIFY, 0, (nint)WM_LBUTTONDBLCLK);
        }
        return false;
    }

    public static void SendUniKeyToggleShortcut()
    {
        // Simulate Ctrl+Shift
        keybd_event(0x11, 0, 0, 0); // VK_CONTROL down
        keybd_event(0x10, 0, 0, 0); // VK_SHIFT down
        keybd_event(0x10, 0, 2, 0); // VK_SHIFT up
        keybd_event(0x11, 0, 2, 0); // VK_CONTROL up
    }

    internal static bool? QueryUniKeyIsVietnamese()
    {
        try
        {
            foreach (var taskbarWnd in FindTaskbarWindows())
            {
                var taskbar = AutomationElement.FromHandle(taskbarWnd);
                if (taskbar is not null)
                {
                    foreach (AutomationElement element in taskbar.FindAll(TreeScope.Descendants,
                                 new PropertyCondition(AutomationElement.AutomationIdProperty, NotifyItemAutomationId)))
                    {
                        var name = element.Current.Name?.Trim();
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        if (name.Contains("unikey", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Vietnamese", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Tiếng", StringComparison.OrdinalIgnoreCase))
                        {
                            if (name.Contains("turn on Vietnamese", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("English", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("Tiếng Anh", StringComparison.OrdinalIgnoreCase))
                                return false;

                            if (name.Contains("turn off Vietnamese", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("Tiếng Việt", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("Vietnamese", StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                }
            }

            var overflow = FindOverflowWindow();
            if (overflow is not null)
            {
                foreach (AutomationElement element in overflow.FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.AutomationIdProperty, NotifyItemAutomationId)))
                {
                    var name = element.Current.Name?.Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if (name.Contains("unikey", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Vietnamese", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Tiếng", StringComparison.OrdinalIgnoreCase))
                    {
                        if (name.Contains("turn on Vietnamese", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("English", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Tiếng Anh", StringComparison.OrdinalIgnoreCase))
                            return false;

                        if (name.Contains("turn off Vietnamese", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Tiếng Việt", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Vietnamese", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or COMException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return null;
    }

    private static void DelayNonBlocking(int milliseconds)
    {
        Thread.Sleep(milliseconds);
    }

    private static T RunWithTimeout<T>(Func<T> action, int timeoutMilliseconds, T fallback)
    {
        try
        {
            var task = Task.Run(action);
            if (task.Wait(timeoutMilliseconds))
            {
                return task.Result;
            }
            Log($"UI Automation / worker call timed out after {timeoutMilliseconds}ms");
            return fallback;
        }
        catch (Exception ex)
        {
            Log($"RunWithTimeout exception: {ex.Message}");
            return fallback;
        }
    }

    private static void WaitForOverflowWindow(int timeoutMilliseconds)
    {
        var elapsed = 0;
        while (elapsed < timeoutMilliseconds && !IsOverflowOpen())
        {
            DelayNonBlocking(PollMilliseconds);
            elapsed += PollMilliseconds;
        }
    }

    private static void WaitForOverflowClose(int timeoutMilliseconds)
    {
        var elapsed = 0;
        while (elapsed < timeoutMilliseconds && IsOverflowOpen())
        {
            DelayNonBlocking(PollMilliseconds);
            elapsed += PollMilliseconds;
        }
    }

    internal static void CloseNativeOverflow(bool restoreStyle = true, bool taskbarsMustStayHidden = false)
    {
        CancelNativeOverflowMotion();
        StopNativeOverflowPresentationGuard();
        HideStyledNativeOverflowBeforeClose();
        if (IsOverflowOpen())
        {
            _ = ToggleNativeOverflow(
                allowKeyboardFallback: false,
                taskbarsMustStayHidden: taskbarsMustStayHidden,
                expectOverflowOpen: false);

            if (IsOverflowOpen())
            {
                var hwnd = FindWindow(OverflowWindowClass, null);
                if (hwnd != 0)
                {
                    SetForegroundWindow(hwnd);
                    keybd_event(0x1B, 0, 0, 0); // VK_ESCAPE
                    keybd_event(0x1B, 0, 2, 0);
                    WaitForOverflowClose(200);
                }
            }
        }

        if (restoreStyle || _styledOverflowHwnd != 0) RestoreNativeOverflowStyle();

        if (taskbarsMustStayHidden || _activeTaskbarTransparencyStates is not null)
        {
            RestoreGuardedTaskbarsAfterOverflowClose(taskbarsMustStayHidden);
        }
    }

    internal static void RestoreGuardedTaskbarsAfterOverflowClose(bool taskbarsMustStayHidden = true)
    {
        var states = _activeTaskbarTransparencyStates;
        _activeTaskbarTransparencyStates = null;

        var primaryTaskbar = FindWindow("Shell_TrayWnd", null);
        if (states is not null)
        {
            foreach (var state in states)
            {
                if (!IsWindow(state.Window)) continue;
                if (state.IsTaskbarRoot)
                {
                    _ = ShowWindow(state.Window, taskbarsMustStayHidden ? SW_HIDE : SW_SHOWNOACTIVATE);
                }
            }

            foreach (var state in states)
            {
                if (!IsWindow(state.Window)) continue;
                if (state.WasLayered && state.OriginalAlpha > 0)
                {
                    _ = SetLayeredWindowAttributes(state.Window, state.OriginalColorKey,
                        state.OriginalAlpha, state.OriginalLayerFlags);
                }
                else
                {
                    _ = SetLayeredWindowAttributes(state.Window, 0, 255, LWA_ALPHA);
                    _ = SetWindowLong(state.Window, GWL_EXSTYLE, state.OriginalStyle & ~WS_EX_LAYERED);
                }

                _ = SetWindowPos(state.Window, 0, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }
        else if (taskbarsMustStayHidden && primaryTaskbar != 0 && IsWindowVisible(primaryTaskbar))
        {
            _ = ShowWindow(primaryTaskbar, SW_HIDE);
        }
        else if (!taskbarsMustStayHidden)
        {
            ResetAllTaskbarsTransparency();
        }
    }

    internal static void ResetAllTaskbarsTransparency()
    {
        var roots = new List<nint>(FindTaskbarWindows());
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != 0 && !roots.Contains(primary)) roots.Add(primary);

        foreach (var root in roots)
        {
            if (!IsWindow(root)) continue;
            _ = EnumChildWindows(root, (child, _) =>
            {
                ResetSingleWindowTransparency(child);
                return true;
            }, 0);
            ResetSingleWindowTransparency(root);
        }
    }

    private static void ResetSingleWindowTransparency(nint window)
    {
        var ex = GetWindowLong(window, GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) != 0 || GetLayeredWindowAttributes(window, out _, out _, out _))
        {
            _ = SetLayeredWindowAttributes(window, 0, 255, LWA_ALPHA);
            _ = SetWindowLong(window, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
            _ = SetWindowPos(window, 0, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    private static nint WaitForApplicationMenu(
        int processId, int x, int y, HashSet<nint> before, int timeoutMilliseconds)
    {
        var elapsed = 0;
        while (elapsed < timeoutMilliseconds)
        {
            var matching = EnumerateVisibleWindows()
                .Where(window => IsNear(window.Bounds, x, y, 400)
                    && !before.Contains(window.Handle)
                    && (window.ProcessId == processId || IsMenuWindowClass(window.ClassName)))
                .OrderBy(window => window.ProcessId == processId ? 0 : 1)
                .FirstOrDefault();

            if (matching is not null) return matching.Handle;
            Thread.Sleep(PollMilliseconds);
            elapsed += PollMilliseconds;
        }

        return 0;
    }

    private const uint GuiInMenuMode = 0x00000004;
    private const uint GuiSystemMenuMode = 0x00000008;
    private const uint GuiPopupMenuMode = 0x00000010;

    private static bool IsApplicationInMenuMode(int processId, out nint menuOwner)
    {
        menuOwner = 0;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            foreach (ProcessThread thread in process.Threads)
            {
                var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
                if (!GetGUIThreadInfo((uint)thread.Id, ref info)) continue;

                if ((info.Flags & (GuiInMenuMode | GuiSystemMenuMode | GuiPopupMenuMode)) == 0) continue;
                menuOwner = info.MenuOwner != 0 ? info.MenuOwner : info.ActiveWindow;
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
            or NotSupportedException or UnauthorizedAccessException)
        {
            // The tray owner can exit while a menu is being opened.
        }
        finally
        {
            process?.Dispose();
        }

        return false;
    }

    private static void WaitForApplicationMenuModeToEnd(int processId, int x, int y, int timeoutMilliseconds)
    {
        var elapsed = 0;
        while (elapsed < timeoutMilliseconds)
        {
            if (!IsApplicationInMenuMode(processId, out _) && !IsContextMenuAtPoint(x, y, out _))
            {
                Log($"Application menu mode ended for PID {processId}.");
                return;
            }

            Thread.Sleep(PollMilliseconds);
            elapsed += PollMilliseconds;
        }

        Log($"Timed out waiting for application menu mode to end for PID {processId}.");
    }

    private static bool IsContextMenuAtPoint(int x, int y, out string description)
    {
        description = string.Empty;
        var offsets = new (int X, int Y)[] { (0, 0), (12, 12), (24, 20), (8, 34) };
        foreach (var offset in offsets)
        {
            try
            {
                var element = AutomationElement.FromPoint(new System.Windows.Point(x + offset.X, y + offset.Y));
                if (element is null) continue;

                var current = element.Current;
                var type = current.ControlType;
                if (type != ControlType.Menu && type != ControlType.MenuItem)
                {
                    var parent = TreeWalker.RawViewWalker.GetParent(element);
                    for (var depth = 0; depth < 4 && parent is not null; depth++)
                    {
                        if (parent.Current.ControlType == ControlType.Menu)
                        {
                            type = ControlType.MenuItem;
                            break;
                        }
                        parent = TreeWalker.RawViewWalker.GetParent(parent);
                    }
                }

                if (type == ControlType.Menu || type == ControlType.MenuItem)
                {
                    description = $"type={type.ProgrammaticName}, name='{current.Name}', class='{current.ClassName}'";
                    return true;
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                or COMException or UnauthorizedAccessException or ArgumentException)
            {
                // Explorer can replace the accessible menu tree while it opens.
            }
        }

        return false;
    }

    private static void WaitForWindowToClose(nint window, int timeoutMilliseconds)
    {
        var elapsed = 0;
        while (elapsed < timeoutMilliseconds && IsWindow(window) && IsWindowVisible(window))
        {
            Thread.Sleep(PollMilliseconds);
            elapsed += PollMilliseconds;
        }
    }

    private static HashSet<nint> GetWindowsNearPoint(int x, int y)
    {
        return EnumerateVisibleWindows()
            .Where(window => IsNear(window.Bounds, x, y, 400))
            .Select(window => window.Handle)
            .ToHashSet();
    }

    private static List<VisibleWindow> EnumerateVisibleWindows()
    {
        var windows = new List<VisibleWindow>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !GetWindowRect(window, out var bounds)) return true;
            if (GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0) return true;
            var className = new char[128];
            var length = GetClassName(window, className, className.Length);
            windows.Add(new VisibleWindow(window, (int)processId, bounds,
                length > 0 ? new string(className, 0, length) : string.Empty));
            return true;
        }, 0);
        return windows;
    }

    private static bool IsNear(NativeRect rect, int x, int y, int maximumDistance)
    {
        var dx = x < rect.Left ? rect.Left - x : x > rect.Right ? x - rect.Right : 0;
        var dy = y < rect.Top ? rect.Top - y : y > rect.Bottom ? y - rect.Bottom : 0;
        return dx <= maximumDistance && dy <= maximumDistance;
    }

    private static bool IsMenuWindowClass(string className) =>
        className.Equals("#32768", StringComparison.Ordinal)
        || className.Equals("Chrome_WidgetWin_1", StringComparison.Ordinal);

    private static NativeRect ToNativeRect(System.Windows.Rect rect) => new()
    {
        Left = (int)Math.Round(rect.Left),
        Top = (int)Math.Round(rect.Top),
        Right = (int)Math.Round(rect.Right),
        Bottom = (int)Math.Round(rect.Bottom),
    };

    private static nint[] FindTaskbarWindows()
    {
        var windows = new List<nint>();
        EnumWindows((window, _) =>
        {
            var className = new char[128];
            var length = GetClassName(window, className, className.Length);
            if (length > 0)
            {
                var name = new string(className, 0, length);
                if (name.Equals("Shell_TrayWnd", StringComparison.Ordinal)
                    || name.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal))
                    windows.Add(window);
            }

            return true;
        }, 0);
        return windows.ToArray();
    }

    private static NativeInput MouseInput(uint flags) => new()
    {
        Type = InputMouse,
        Data = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = flags } },
    };

    internal static List<VisibleWindow> GetVisibleWindowsSnapshot() => EnumerateVisibleWindows();

    internal static void WaitForMenuDismissal(int processId, List<VisibleWindow> beforeWindows, nint targetHwnd)
    {
        nint menuHwnd = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500)
        {
            var visible = EnumerateVisibleWindows();
            var newWnd = visible.FirstOrDefault(w =>
                !beforeWindows.Any(b => b.Handle == w.Handle)
                && (w.ProcessId == processId || IsMenuWindowClass(w.ClassName)));

            if (newWnd is not null)
            {
                menuHwnd = newWnd.Handle;
                break;
            }
            Thread.Sleep(20);
        }

        if (menuHwnd != 0)
        {
            WaitForWindowToClose(menuHwnd, 60000);
            if (targetHwnd != 0)
            {
                PostMessage(targetHwnd, 0 /* WM_NULL */, 0, 0);
            }
        }
        else
        {
            var sw2 = Stopwatch.StartNew();
            while (sw2.ElapsedMilliseconds < 30000)
            {
                var stillOpen = EnumerateVisibleWindows().Any(w =>
                    w.ProcessId == processId && IsMenuWindowClass(w.ClassName));
                if (!stillOpen) break;
                Thread.Sleep(50);
            }
            if (targetHwnd != 0)
            {
                PostMessage(targetHwnd, 0 /* WM_NULL */, 0, 0);
            }
        }
    }

    internal sealed record VisibleWindow(nint Handle, int ProcessId, NativeRect Bounds, string ClassName);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint ActiveWindow;
        public nint FocusWindow;
        public nint CaptureWindow;
        public nint MenuOwner;
        public nint MoveSizeWindow;
        public nint CaretWindow;
        public NativeRect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int Size;
        public nint Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
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

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect iconLocation);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsCallback callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsCallback callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint window, [Out] char[] className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    private delegate bool EnumWindowsCallback(nint window, nint lParam);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int LWA_ALPHA = 0x00000002;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(nint hwnd, out uint crKey, out byte bAlpha, out uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int valueSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);
}
