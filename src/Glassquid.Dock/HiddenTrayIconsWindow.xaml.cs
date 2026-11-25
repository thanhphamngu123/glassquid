using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glassquid.Core.Models.Settings;
using Glassquid.UI.Controls;
using Glassquid.UI.Settings;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>A Glassquid-owned flyout for active notification-area app icons.</summary>
public partial class HiddenTrayIconsWindow : GlassWindow
{
    private readonly ObservableCollection<TrayIconItem> _icons = [];
    private readonly SystemTrayBarWindow _dock;
    private readonly DispatcherTimer _liveFrameTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(16),
    };
    private readonly DispatcherTimer _clickTimer = new();
    private TrayIconItem? _pendingClickItem;
    private bool _isClosed;
    private bool _isClosing;
    private bool _closeQueued;
    private bool _glassRevealPending;
    private long _glassRevealStarted;
    private bool _contextMenuActive;

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    internal HiddenTrayIconsWindow(SystemTrayBarWindow dock, IReadOnlyList<TrayIconItem> icons, bool isLoading = false)
    {
        ArgumentNullException.ThrowIfNull(dock);
        ArgumentNullException.ThrowIfNull(icons);
        _dock = dock;
        Owner = dock;

        InitializeComponent();
        foreach (var icon in icons)
        {
            if (IsUniKey(icon) && RegistryTrayReader.LastKnownUniKeyIsVietnamese.HasValue)
            {
                ApplyUniKeyMode(icon, RegistryTrayReader.LastKnownUniKeyIsVietnamese.Value);
            }
            _icons.Add(icon);
        }

        TrayIconsList.ItemsSource = _icons;
        var hasIcons = _icons.Count > 0;
        TrayIconsList.Visibility = hasIcons ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Visibility = hasIcons ? Visibility.Collapsed : Visibility.Visible;
        EmptyStateText.Text = isLoading ? "Đang tải biểu tượng khay..." : "Không có biểu tượng khay ẩn đang chạy.";
        var app = ThemeService.Current?.Appearance;
        var initialRadius = app is not null
            ? Math.Max(40, app.DockCornerRadius) * (DockSettingsHost.Current?.Settings.SizeScale ?? 1)
            : 40;
        PanelCornerRadius = new CornerRadius(initialRadius);
        GlassSurface.IsBackdropActive = () => IsVisible && !_glassRevealPending;
        GlassSurface.SuppressAmbientFallback = true;
        _liveFrameTimer.Tick += OnLiveFrameTimerTick;
        _clickTimer.Interval = TimeSpan.FromMilliseconds(220);
        _clickTimer.Tick += OnClickTimerTick;
    }

    /// <summary>Set final dimensions and screen position before the HWND is first shown.</summary>
    public void PrepareForShow()
    {
        _glassRevealPending = true;
        Opacity = 0;
        MeasureContentAndPosition();
        GlassSurface.PrepareRevealForWindow(this);
    }

    internal void UpdateIcons(IReadOnlyList<TrayIconItem> icons)
    {
        ArgumentNullException.ThrowIfNull(icons);
        if (_isClosed) return;

        _icons.Clear();
        foreach (var icon in icons)
        {
            if (IsUniKey(icon) && RegistryTrayReader.LastKnownUniKeyIsVietnamese.HasValue)
            {
                ApplyUniKeyMode(icon, RegistryTrayReader.LastKnownUniKeyIsVietnamese.Value);
            }
            _icons.Add(icon);
        }
        var hasIcons = _icons.Count > 0;
        TrayIconsList.Visibility = hasIcons ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Visibility = hasIcons ? Visibility.Collapsed : Visibility.Visible;
        EmptyStateText.Text = "Không có biểu tượng khay ẩn đang chạy.";
        MeasureContentAndPosition();
    }

    private void MeasureContentAndPosition()
    {
        if (Content is UIElement content)
        {
            var workArea = WorkAreaDip;
            content.Measure(new Size(workArea.Width, workArea.Height));
            var desired = content.DesiredSize;
            Width = Math.Ceiling(desired.Width);
            Height = Math.Ceiling(desired.Height);
            SizeToContent = SizeToContent.Manual;
            PositionAboveDock(Width, Height);
        }
    }

    protected override bool UsesSquirclePanelGeometryCore => true;

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        var scale = DockSettingsHost.Current?.Settings.SizeScale ?? 1;
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        return Math.Max(40, appearance.DockCornerRadius) * scale;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PrimaryDisplaySession is { IsAvailable: false }) Hide();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_glassRevealPending) return;

        // Do not display the temporary desktop snapshot here. It is deliberately
        // not the dock's live optical material and leaves desktop labels visible.
        _glassRevealPending = false;
        _glassRevealStarted = Stopwatch.GetTimestamp();
        GlassSurface.RequestBackdropRefresh(revealing: true);
        Opacity = 1;
        AnimatePopupOpen();
        _liveFrameTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _isClosing = true;
        _liveFrameTimer.Stop();
        _liveFrameTimer.Tick -= OnLiveFrameTimerTick;
        _clickTimer.Stop();
        _clickTimer.Tick -= OnClickTimerTick;
        _pendingClickItem = null;
        base.OnClosed(e);
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        if (_isClosed || _isClosing) return;
        // Don't auto-close while a tray icon context menu is being shown
        if (_contextMenuActive) return;
        QueueCloseIfInactive();
    }

    private void OnLiveFrameTimerTick(object? sender, EventArgs e)
    {
        if (_isClosed || !IsVisible)
        {
            _liveFrameTimer.Stop();
            return;
        }

        var ready = GlassSurface.IsCurrentLiveMaterialReady;
        var rendered = GlassSurface.HasRenderedCurrentMaterial;
        var timedOut = Stopwatch.GetElapsedTime(_glassRevealStarted) >= TimeSpan.FromMilliseconds(800);
        if (!ready && !timedOut)
        {
            return;
        }

        System.Diagnostics.Debug.WriteLine(
            $"[Glassquid HiddenTrayIcons] First live optical frame ready: {ready}; rendered: {rendered}; timeout: {timedOut}");
        _liveFrameTimer.Stop();
    }

    private void AnimatePopupOpen()
    {
        var duration = TimeSpan.FromMilliseconds(350);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        PopupSlideTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(26, 0, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        PopupVisualRoot.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);

        PopupMotionTint.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(.38, 0, duration),
            HandoffBehavior.SnapshotAndReplace);
    }

    private void QueueCloseIfInactive()
    {
        if (_closeQueued || _isClosed || _isClosing || _contextMenuActive
            || Dispatcher.HasShutdownStarted) return;
        _closeQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _closeQueued = false;
            if (_isClosed || _isClosing || !IsVisible || IsActive || _contextMenuActive) return;
            ClosePopup();
        }));
    }

    private void ClosePopup()
    {
        Debug.WriteLine($"[Close] _contextMenuActive={_contextMenuActive} IsActive={IsActive} stack={Environment.StackTrace}");
        try { File.AppendAllText(@"C:\Users\THANHPHAM\Desktop\relay_debug.log", $"[{DateTime.Now:HH:mm:ss.fff}] [Close] _contextMenuActive={_contextMenuActive} IsActive={IsActive} stack={Environment.StackTrace}\n"); } catch {}
        if (_contextMenuActive) return;
        _clickTimer.Stop();
        _pendingClickItem = null;
        if (_isClosed || _isClosing) return;
        _isClosing = true;
        Close();
    }

    private void PositionAboveDock(double? widthOverride = null, double? heightOverride = null)
    {
        var width = widthOverride ?? (ActualWidth > 0 ? ActualWidth : Width);
        var height = heightOverride ?? (ActualHeight > 0 ? ActualHeight : Height);
        if (!_dock.IsVisible || width <= 0 || height <= 0) return;

        var anchor = _dock.GetHiddenIconsAnchorPoint();
        var workArea = WorkAreaDip;
        Left = Math.Clamp(_dock.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        Top = _dock.IsTopPlacement
            ? Math.Clamp(anchor.Y + 14, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height))
            : Math.Clamp(anchor.Y - height - 14, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
    }

    private void OnTrayIconPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: TrayIconItem item }) return;

        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            _pendingClickItem = null;
            e.Handled = true;
            HandleTrayIconDoubleClick(item);
            return;
        }

        if (e.ClickCount == 1)
        {
            if (IsUniKey(item))
            {
                _pendingClickItem = item;
                _clickTimer.Stop();
                _clickTimer.Start();
                return;
            }

            HandleTrayIconSingleClick(item);
        }
    }

    private void OnClickTimerTick(object? sender, EventArgs e)
    {
        _clickTimer.Stop();
        var item = _pendingClickItem;
        _pendingClickItem = null;
        if (item is null || _isClosed || _isClosing) return;

        HandleTrayIconSingleClick(item);
    }

    private static void ApplyUniKeyMode(TrayIconItem item, bool isVietnamese)
    {
        item.IsUniKeyModeVietnamese = isVietnamese;
        var newIcon = RegistryTrayReader.TryExtractExecutableIcon(item.ExecutablePath, isVietnamese ? 1 : 2);
        if (newIcon != null)
        {
            item.Icon = newIcon;
        }
        item.ToolTip = isVietnamese
            ? "UniKey (Tiếng Việt - Bấm để chuyển sang E)"
            : "UniKey (English - Bấm để chuyển sang V)";
    }

    private void HandleTrayIconSingleClick(TrayIconItem item)
    {
        if (IsUniKey(item))
        {
            var nextMode = !item.IsUniKeyModeVietnamese;
            ApplyUniKeyMode(item, nextMode);
            RegistryTrayReader.LastKnownUniKeyIsVietnamese = nextMode;

            var success = TrayIconContextMenuRelay.ToggleUniKeyDirectly();
            if (!success)
            {
                TrayIconContextMenuRelay.SendUniKeyToggleShortcut();
            }
            return;
        }

        var managedIcon = FindManagedIcon(item);
        if (managedIcon != null && managedIcon.HWnd != IntPtr.Zero && managedIcon.CallbackMessage != 0)
        {
            uint mouseParam = GetCursorPositionParam();
            managedIcon.IconMouseDown(MouseButton.Left, mouseParam, 500);
            managedIcon.IconMouseUp(MouseButton.Left, mouseParam, 500);
            ClosePopup();
        }
        else
        {
            ClosePopup();
            Task.Run(() =>
            {
                if (!TrayIconContextMenuRelay.RelayLeftClick(item))
                {
                    Dispatcher.BeginInvoke(() => ActivateApplication(item));
                }
            });
        }
    }

    private void HandleTrayIconDoubleClick(TrayIconItem item)
    {
        ClosePopup();

        if (IsUniKey(item))
        {
            var success = TrayIconContextMenuRelay.OpenUniKeyPanelDirectly();
            if (!success)
            {
                ActivateApplication(item);
            }
            return;
        }

        var managedIcon = FindManagedIcon(item);
        if (managedIcon != null && managedIcon.HWnd != IntPtr.Zero && managedIcon.CallbackMessage != 0)
        {
            uint mouseParam = GetCursorPositionParam();
            managedIcon.IconMouseDown(MouseButton.Left, mouseParam, 0);
            managedIcon.IconMouseUp(MouseButton.Left, mouseParam, 0);
        }
        else
        {
            Task.Run(() =>
            {
                if (!TrayIconContextMenuRelay.RelayDoubleClick(item))
                {
                    Dispatcher.BeginInvoke(() => ActivateApplication(item));
                }
            });
        }
    }

    private static ManagedShell.WindowsTray.NotifyIcon? FindManagedIcon(TrayIconItem item)
    {
        var all = Tray.ManagedShellNotificationArea.Instance.AllIcons;
        if (item.NotifyIcon is { } cached && all.Contains(cached)
            && cached.HWnd != IntPtr.Zero && cached.CallbackMessage != 0
            && (IsSameProcess(cached.HWnd, item.ProcessId)
                || IsSameExecutablePath(cached.Path, item.ExecutablePath))) return cached;
        item.NotifyIcon = null;

        var usableIcons = all
            .Where(i => i.HWnd != IntPtr.Zero && i.CallbackMessage != 0)
            .ToArray();
        var processIcons = usableIcons.Where(i => IsSameProcess(i.HWnd, item.ProcessId)).ToArray();
        var pathIcons = usableIcons.Where(i => IsSameExecutablePath(i.Path, item.ExecutablePath)).ToArray();

        ManagedShell.WindowsTray.NotifyIcon? icon = null;
        if (item.IconGuid is { } guid && guid != Guid.Empty)
        {
            var guidMatches = usableIcons.Where(i => i.GUID == guid).ToArray();
            if (guidMatches.Length == 1) icon = guidMatches[0];
        }

        if (icon is null && item.Id is { } id)
        {
            var idMatches = usableIcons
                .Where(i => i.UID == id && (processIcons.Contains(i) || pathIcons.Contains(i)))
                .ToArray();
            if (idMatches.Length == 1) icon = idMatches[0];
        }

        if (icon is null)
        {
            var titleMatches = usableIcons
                .Where(i => (processIcons.Contains(i) || pathIcons.Contains(i))
                && string.Equals(i.Title, item.ToolTip, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (titleMatches.Length == 1) icon = titleMatches[0];
        }

        if (icon is null && processIcons.Length == 1) icon = processIcons[0];
        if (icon is null && pathIcons.Length == 1) icon = pathIcons[0];

        if (icon != null)
        {
            item.NotifyIcon = icon;
            var message = $"Matched '{item.ToolTip}' (exe '{item.ExecutablePath}', PID {item.ProcessId}, UID {item.Id}, GUID {item.IconGuid}) to ManagedShell icon '{icon.Title}' (path '{icon.Path}', UID {icon.UID}, GUID {icon.GUID}, HWnd=0x{icon.HWnd.ToInt64():X}, callback=0x{icon.CallbackMessage:X}, version={icon.Version}).";
            Debug.WriteLine($"[HiddenTrayIconsWindow] {message}");
            TrayIconContextMenuRelay.Log(message);
        }
        else
        {
            Debug.WriteLine($"[HiddenTrayIconsWindow] No unique ManagedShell callback found for '{item.ToolTip}' (PID {item.ProcessId}, UID {item.Id}, GUID {item.IconGuid}, process icons {processIcons.Length}).");
            TrayIconContextMenuRelay.Log($"No ManagedShell callback match: title='{item.ToolTip}', exe='{item.ExecutablePath}', PID={item.ProcessId}, UID={item.Id}, GUID={item.IconGuid}, processIcons={processIcons.Length}, pathIcons={pathIcons.Length}, total={usableIcons.Length}.");
        }

        return icon;
    }

    private static bool IsSameProcess(nint window, int processId)
    {
        if (window == 0 || processId <= 0) return false;
        _ = GetWindowThreadProcessId(window, out var actualProcessId);
        return actualProcessId == (uint)processId;
    }

    private static bool IsSameExecutablePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return left.Equals(right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static uint GetCursorPositionParam()
    {
        if (!GetCursorPos(out var point)) return 0;
        return (uint)((point.X & 0xFFFF) | ((point.Y & 0xFFFF) << 16));
    }

    private static bool IsUniKey(TrayIconItem item)
    {
        return item.ExecutablePath.Contains("unikey", StringComparison.OrdinalIgnoreCase)
            || item.ToolTip.Contains("unikey", StringComparison.OrdinalIgnoreCase);
    }

    private void OnTrayIconRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: TrayIconItem item } button) return;
        e.Handled = true;

        var buttonPos = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        var screenX = (int)buttonPos.X;
        var screenY = (int)buttonPos.Y;

        TrayIconContextMenuRelay.AllowSetForegroundWindow(item.ProcessId);
        var managedIcon = FindManagedIcon(item);
        _contextMenuActive = true;
        var wasTopmost = Topmost;
        Topmost = false;

        var relay = new Thread(() =>
        {
            try
            {
                if (managedIcon != null && managedIcon.HWnd != IntPtr.Zero && managedIcon.CallbackMessage != 0)
                {
                    var opened = TrayIconContextMenuRelay.RelayManagedContextMenu(
                        managedIcon, item.ProcessId, screenX, screenY);
                    if (!opened)
                    {
                        Debug.WriteLine($"[HiddenTrayIconsWindow] Callback did not open a menu for '{item.ToolTip}'; trying Explorer relay.");
                        TrayIconContextMenuRelay.ShowContextMenuAtPosition(item, screenX, screenY);
                    }
                }
                else
                {
                    Debug.WriteLine($"[HiddenTrayIconsWindow] No ManagedShell callback for '{item.ToolTip}'; trying Explorer relay.");
                    TrayIconContextMenuRelay.ShowContextMenuAtPosition(item, screenX, screenY);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HiddenTrayIconsWindow] Context menu relay failed: {ex}");
            }
            finally
            {
                Dispatcher.BeginInvoke(() =>
                {
                    Topmost = wasTopmost;
                    _contextMenuActive = false;
                    if (!IsActive && !_isClosed && !_isClosing)
                    {
                        QueueCloseIfInactive();
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "Glassquid tray menu direct",
        };
        relay.SetApartmentState(ApartmentState.STA);
        relay.Start();
    }

    private static void ActivateApplication(TrayIconItem item)
    {
        try
        {
            using var process = Process.GetProcessById(item.ProcessId);
            if (process.HasExited) return;

            var window = process.MainWindowHandle;
            if (window != 0)
            {
                _ = ShowWindowAsync(window, 9); // SW_RESTORE
                _ = SetForegroundWindow(window);
                return;
            }

            if (File.Exists(item.ExecutablePath))
                _ = Process.Start(new ProcessStartInfo(item.ExecutablePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or Win32Exception or IOException or SecurityException)
        {
            // A tray application can exit between refreshing the list and a click.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

internal sealed class TrayIconItem : INotifyPropertyChanged
{
    private string _toolTip;
    private ImageSource? _icon;

    public string ToolTip
    {
        get => _toolTip;
        set
        {
            if (_toolTip != value)
            {
                _toolTip = value;
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (!Equals(_icon, value))
            {
                _icon = value;
                OnPropertyChanged(nameof(Icon));
            }
        }
    }

    public string ExecutablePath { get; }
    public int ProcessId { get; }
    public uint? Id { get; }
    public Guid? IconGuid { get; }
    public bool IsUniKeyModeVietnamese { get; set; }
    public ManagedShell.WindowsTray.NotifyIcon? NotifyIcon { get; set; }

    public TrayIconItem(
        string toolTip,
        ImageSource? icon,
        string executablePath,
        int processId,
        uint? id,
        Guid? iconGuid)
    {
        _toolTip = toolTip;
        _icon = icon;
        ExecutablePath = executablePath;
        ProcessId = processId;
        Id = id;
        IconGuid = iconGuid;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
