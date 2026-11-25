using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glassquid.Core.Models.Settings;
using Glassquid.Platform;
using Glassquid.Platform.Displays;
using Glassquid.Platform.Shell;
using Glassquid.UI.Controls;
using Glassquid.UI.Settings;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>A compact bottom-right companion bar for Windows status and shell panels.</summary>
public partial class SystemTrayBarWindow : GlassWindow
{
    private static readonly TimeSpan AutoHideFrameInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan AutoHideIdleInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ClockRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly Lazy<Dictionary<string, Geometry>> BatteryGeometries = new(LoadBatteryGeometries);
    private static readonly Lazy<Dictionary<string, Geometry>> NetworkGeometries = new(LoadNetworkGeometries);
    private static readonly Lazy<Geometry> NotificationAlertGeometry = new(() => LoadSvgGeometry(
        "Glassquid.Dock.Assets.Alerts.ic_fluent_alert_24_regular.svg"));
    private static readonly Lazy<Geometry> NotificationAlertOffGeometry = new(() => LoadSvgGeometry(
        "Glassquid.Dock.Assets.Alerts.ic_fluent_alert_off_24_regular.svg"));
    private bool? _currentDndActive;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _nativeOverflowWatcherTimer;
    private DispatcherTimer? _autoHideFrameTimer;
    private DockWindow? _mainDock;

    public void AttachMainDock(DockWindow mainDock)
    {
        _mainDock = mainDock;
        _autoHideFrameTimer?.Stop();
    }

    public void DetachMainDock()
    {
        _mainDock = null;
        if (IsVisible && !_performanceSleeping)
        {
            _autoHideFrameTimer?.Start();
        }
    }

    public bool IsPointerOverTray((int X, int Y) cursor)
        => Inflate(GetVisibleTrayRect(), 2).Contains(cursor.X, cursor.Y);

    public bool IsPointerOverTrayPosition((int X, int Y) cursor)
    {
        var displayBounds = PrimaryDisplaySession?.Display.Bounds;
        var isLeft = IsLeftPlacement;
        var limit = isLeft ? (displayBounds?.X ?? _restingRect.X) : (displayBounds?.Right ?? _restingRect.Right);
        var trayHotzone = IsTopPlacement
            ? (isLeft
                ? new PixelRect(
                    limit - 10,
                    displayBounds?.Y ?? (_restingRect.Y - 10),
                    Math.Max(_restingRect.Width + 10, _restingRect.Right - limit + 10),
                    Math.Max(_restingRect.Height + 10, _restingRect.Bottom - (displayBounds?.Y ?? _restingRect.Y) + 10))
                : new PixelRect(
                    _restingRect.X - 10,
                    displayBounds?.Y ?? (_restingRect.Y - 10),
                    Math.Max(_restingRect.Width + 10, limit - _restingRect.X + 10),
                    Math.Max(_restingRect.Height + 10, _restingRect.Bottom - (displayBounds?.Y ?? _restingRect.Y) + 10)))
            : (isLeft
                ? new PixelRect(
                    limit - 10,
                    _restingRect.Y - 10,
                    Math.Max(_restingRect.Width + 10, _restingRect.Right - limit + 10),
                    Math.Max(_restingRect.Height + 10, (displayBounds?.Bottom ?? _restingRect.Bottom) - _restingRect.Y + 10))
                : new PixelRect(
                    _restingRect.X - 10,
                    _restingRect.Y - 10,
                    Math.Max(_restingRect.Width + 10, limit - _restingRect.X + 10),
                    Math.Max(_restingRect.Height + 10, (displayBounds?.Bottom ?? _restingRect.Bottom) - _restingRect.Y + 10)));
        return trayHotzone.Contains(cursor.X, cursor.Y) || Inflate(_restingRect, 2).Contains(cursor.X, cursor.Y);
    }

    public bool HasActivePopup() =>
        _hiddenIconsPopupOpening
        || HiddenIconsButton.Tag is true
        || (_quickSettingsFlyoutWindow is { IsVisible: true })
        || (_wifiFlyoutWindow is { IsVisible: true })
        || (_keyboardLayoutFlyoutWindow is { IsVisible: true })
        || (_notificationsFlyoutWindow is { IsVisible: true })
        || IsNativeOverflowSessionActive;

    public void SyncSlideProgress(double progress, AutoHideState state)
    {
        if (_performanceSleeping || _restingRect.Width <= 0) return;

        if (_autoHide is not null)
        {
            if (state == AutoHideState.Hidden)
            {
                _autoHide.ForceHide(DateTime.UtcNow);
            }
            else
            {
                _autoHide.ForceShown(DateTime.UtcNow);
            }
        }

        var target = progress > 0 ? (double)_fullyHiddenOffsetPixels : 0.0;
        if (target == 0) PrepareConcealedReveal();

        _slideOffsetPixels = Math.Clamp(progress * _fullyHiddenOffsetPixels, 0, _fullyHiddenOffsetPixels);
        ApplySlide(_restingRect, _slideOffsetPixels);
    }
    private AutoHideController? _autoHide;
    private readonly Func<IDisposable>? _suspendWindowsTaskbarRefresh;
    private DockSettingsHost? _dockSettings;
    public bool IsTopPlacement => _dockSettings?.Settings.Placement == DockPlacement.TopCenter;
    public void PrepareRevealForStartup() => GlassSurface.PrepareRevealForWindow(this);
    public bool IsLeftPlacement => _dockSettings?.Settings.SystemTrayOnLeft == true;
    private int SlideOutDirection() => IsTopPlacement ? -1 : 1;
    private KeyboardLayoutFlyoutWindow? _keyboardLayoutFlyoutWindow;
    private QuickSettingsFlyoutWindow? _quickSettingsFlyoutWindow;
    private WifiFlyoutWindow? _wifiFlyoutWindow;
    private NotificationsFlyoutWindow? _notificationsFlyoutWindow;
    private nint _lastInputLanguageTargetWindow;
    private nint _lastInputLanguageFocusWindow;
    private DateTime _inputLanguageFeedbackExpiresAt;
    private bool _hiddenIconsPopupShouldClose;
    private bool _hiddenIconsPopupOpening;
    private int _nativeOverflowGeneration;
    private nint _nativeOverflowReturnWindow;
    private uint _nativeOverflowReturnProcess;
    private bool _performanceSleeping;
    private bool _positionUpdateQueued;
    private DateTime _lastSystemStatusRefresh;
    private bool _windowsTaskbarHidden;
    private PixelRect _restingRect;
    private int _fullyHiddenOffsetPixels;
    private double _slideOffsetPixels;
    private bool _edgeRevealedDuringSuppression;
    private bool _panelWasConcealed;
    private bool _backdropWasActive;
    private long _revealPreparationStarted;

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    protected override Geometry CreatePanelGeometryCore(Rect bounds, double radius) =>
        SquircleGlassGeometry.Create(bounds, radius);

    protected override bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) =>
        SquircleGlassGeometry.Contains(bounds, radius, point);

    protected override bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        SquircleGlassGeometry.TryGetEdge(bounds, radius, point, out depth, out inwardNormal);

    protected override bool UsesSquirclePanelGeometryCore => true;

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(40, appearance.DockCornerRadius) * DockScale;
    }

    /// <summary>Whether Windows 11's native tray overflow flyout is currently open.</summary>
    public static bool IsNativeOverflowActive() => TrayIconContextMenuRelay.IsNativeOverflowVisible();

    private readonly bool _isSecondary;

    public SystemTrayBarWindow(Func<IDisposable>? suspendWindowsTaskbarRefresh = null, bool isSecondary = false)
    {
        _isSecondary = isSecondary;
        _suspendWindowsTaskbarRefresh = suspendWindowsTaskbarRefresh;
        InitializeComponent();
        RefreshNotificationsStatus();
        WindowsDoNotDisturbController.StateChanged += OnDoNotDisturbStateChanged;
        if (!_isSecondary)
        {
            WindowsNotificationManager.Instance.Initialize();
            Tray.ManagedShellNotificationArea.Instance.Initialize();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(TrayIconContextMenuRelay.WarmNativeTrayArtwork));
        }
        PanelCornerRadius = new CornerRadius(40);
        HiddenIconsChevronRotation = new RotateTransform(0);
        HiddenIconsGlyph.RenderTransform = HiddenIconsChevronRotation;
        GlassSurface.IsBackdropActive = () => IsVisible && IsOpticalSurfaceVisible;
        GlassSurface.SuppressAmbientFallback = true;
        RefreshNetworkStatus();

        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.InvokeAsync(RefreshNetworkStatus);
            NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.InvokeAsync(RefreshNetworkStatus);
        }
        catch
        {
            // Ignore network event registration errors
        }

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ClockRefreshInterval,
        };
        _refreshTimer.Tick += OnRefreshTimerTick;

        _nativeOverflowWatcherTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _nativeOverflowWatcherTimer.Tick += (_, _) =>
        {
            if (!_hiddenIconsPopupOpening && !TrayIconContextMenuRelay.IsNativeOverflowVisible())
            {
                _nativeOverflowWatcherTimer.Stop();
                if (HiddenIconsButton.Tag is true)
                {
                    SetHiddenIconsPopupOpen(false);
                    CloseNativeOverflowForDock(restoreStyle: true, taskbarsMustStayHidden: true);
                }
            }
        };
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                RefreshStatus();
                _refreshTimer.Start();
                if (!_performanceSleeping) _autoHideFrameTimer?.Start();
            }
            else
            {
                _refreshTimer.Stop();
                _autoHideFrameTimer?.Stop();
            }
        };
    }

    public void AttachDockSettings(DockSettingsHost settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_dockSettings is not null) _dockSettings.Changed -= OnDockSettingsChanged;
        _dockSettings = settings;
        _dockSettings.Changed += OnDockSettingsChanged;
        ApplyDockMetrics();
        ApplyAppearance();
        RebuildAutoHide();
        PositionOnDisplay();
    }

    public void SetPerformanceSleeping(bool sleeping)
    {
        if (_performanceSleeping == sleeping) return;
        _performanceSleeping = sleeping;
        if (sleeping)
        {
            Hide();
            GlassSurface.SetPerformanceSleeping(true);
        }
        else
        {
            GlassSurface.SetPerformanceSleeping(false);
            Show();
            PositionOnDisplay();
            if (IsVisible) _autoHideFrameTimer?.Start();
        }
    }

    public void RefreshDisplayPlacement()
    {
        if (PrimaryDisplaySession is { IsAvailable: true }) PositionOnDisplay();
    }

    public void SetWindowsTaskbarHidden(bool hidden)
    {
        if (hidden)
        {
            if (!_windowsTaskbarHidden)
                CloseNativeOverflowForDock(taskbarsMustStayHidden: true);
            TrayIconContextMenuRelay.ResetNativeOverflowAppearance();
        }
        else
        {
            if (_windowsTaskbarHidden)
                CloseNativeOverflowForDock(taskbarsMustStayHidden: false);
            else TrayIconContextMenuRelay.ResetNativeOverflowAppearance();
        }
        if (_windowsTaskbarHidden == hidden) return;
        _windowsTaskbarHidden = hidden;
        RebuildAutoHide();
        HiddenIconsButton.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        ApplyDockMetrics();
        _hiddenIconsPopupShouldClose = false;
        _hiddenIconsPopupOpening = false;
        SetHiddenIconsPopupOpen(false);
        PositionOnDisplay();
        QueuePositionOnDisplay();
    }

    public void PrepareForWindowsTaskbarRestore()
    {
        // Release Explorer's temporary transparent tray host while preparing to restore the taskbars.
        CloseNativeOverflowForDock(restoreStyle: true, taskbarsMustStayHidden: false);
    }

    public void ResumeAfterPrimaryDisplayChange(bool show)
    {
        PositionOnDisplay();
        if (show && !_performanceSleeping) Show();
    }

    internal Point GetHiddenIconsAnchorPoint()
    {
        var buttonTopCenter = HiddenIconsButton.PointToScreen(
            new Point(HiddenIconsButton.ActualWidth / 2, IsTopPlacement ? HiddenIconsButton.ActualHeight : 0));
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(buttonTopCenter)
            : buttonTopCenter;
    }

    internal Point GetHiddenIconsAnchorScreenPoint() => HiddenIconsButton.PointToScreen(
        new Point(HiddenIconsButton.ActualWidth / 2, IsTopPlacement ? HiddenIconsButton.ActualHeight : 0));

    internal Point GetInputLanguageAnchorPoint()
    {
        var buttonTopCenter = InputLanguageButton.PointToScreen(
            new Point(InputLanguageButton.ActualWidth / 2, IsTopPlacement ? InputLanguageButton.ActualHeight : 0));
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(buttonTopCenter)
            : buttonTopCenter;
    }

    internal Point GetQuickSettingsAnchorPoint()
    {
        var rightButton = BatteryButton.Visibility == Visibility.Visible ? BatteryButton : VolumeButton;
        var y = IsTopPlacement ? rightButton.ActualHeight : 0;
        var left = NetworkButton.PointToScreen(new Point(0, y));
        var right = rightButton.PointToScreen(new Point(rightButton.ActualWidth, y));
        var centerTop = new Point((left.X + right.X) / 2, IsTopPlacement ? Math.Max(left.Y, right.Y) : Math.Min(left.Y, right.Y));
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(centerTop)
            : centerTop;
    }

    internal Point GetNotificationsAnchorPoint()
    {
        var button = NotificationsButton.Visibility == Visibility.Visible
            ? (FrameworkElement)NotificationsButton
            : ClockButton;
        var buttonTopCenter = button.PointToScreen(new Point(button.ActualWidth / 2, IsTopPlacement ? button.ActualHeight : 0));
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(buttonTopCenter)
            : buttonTopCenter;
    }

    public void PositionOnDisplay()
    {
        var handle = Handle;
        if (handle == 0 || PrimaryDisplaySession is not { IsAvailable: true } session) return;

        var display = session.Display;
        var width = Math.Max(1, display.ToPixels(ActualWidth > 0 ? ActualWidth : DesiredSize.Width));
        var height = Math.Max(1, display.ToPixels(ActualHeight > 0 ? ActualHeight : DesiredSize.Height));
        var inset = display.ToPixels(8);
        var bottomInset = display.ToPixels(10);
        // Keep the horizontal anchor in the reserved work area so it doesn't
        // shift past the right edge. When hidden, use the full screen vertically
        // so the tray bar drops into the space exposed by the taskbar.
        var work = display.WorkArea;
        var verticalArea = _windowsTaskbarHidden ? display.Bounds : display.WorkArea;
        width = Math.Min(width, work.Width);
        height = Math.Min(height, verticalArea.Height);
        inset = Math.Min(inset, Math.Max(0, Math.Min(work.Width - width, verticalArea.Height - height)));

        var offsetY = _dockSettings is not null
            ? display.ToPixels(_dockSettings.Settings.OffsetY)
            : 0;

        var isTop = IsTopPlacement;
        var requestedY = isTop ? verticalArea.Y + offsetY : verticalArea.Bottom - height - bottomInset + offsetY;
        var clampedY = isTop
            ? Math.Clamp(requestedY, verticalArea.Y, Math.Max(verticalArea.Y, verticalArea.Bottom - height))
            : Math.Clamp(requestedY, verticalArea.Y, Math.Max(verticalArea.Y, verticalArea.Bottom - height - bottomInset));

        var isLeft = IsLeftPlacement;
        var requestedX = isLeft ? work.X + inset : work.Right - width - inset;
        var clampedX = Math.Clamp(requestedX, work.X, Math.Max(work.X, work.Right - width));
        var clamped = new PixelRect(
            clampedX,
            clampedY,
            width,
            height);
        _restingRect = clamped;
        if (DisplayService.GetWindowPixelBounds(handle) != clamped)
        {
            _ = DisplayService.MoveWindowToPixelRect(handle, clamped);
        }
        _fullyHiddenOffsetPixels = isTop
            ? Math.Max(0, clamped.Bottom - display.Bounds.Y) + 2
            : Math.Max(0, display.Bounds.Bottom - clamped.Y) + 2;
        _slideOffsetPixels = Math.Clamp(_slideOffsetPixels, 0, _fullyHiddenOffsetPixels);
        ApplySlide(_restingRect, _slideOffsetPixels, display);
    }

    private void OnDoNotDisturbStateChanged(object? sender, bool active)
    {
        Dispatcher.InvokeAsync(RefreshNotificationsStatus);
    }

    internal void RefreshNotificationsStatus()
    {
        bool isDnd = WindowsDoNotDisturbController.IsActive();
        if (_currentDndActive == isDnd) return;
        _currentDndActive = isDnd;

        NotificationsGlyph.Data = isDnd
            ? NotificationAlertOffGeometry.Value
            : NotificationAlertGeometry.Value;

        NotificationsButton.ToolTip = isDnd
            ? "Không làm phiền đang bật (Mở trung tâm thông báo)"
            : "Mở trung tâm thông báo";
    }

    private void RefreshStatus()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("t", CultureInfo.CurrentCulture);
        DateText.Text = now.ToString("d", CultureInfo.CurrentCulture);
        RefreshInputLanguageStatus();
        RefreshNotificationsStatus();
        if (_lastSystemStatusRefresh == default || now - _lastSystemStatusRefresh >= RefreshInterval)
        {
            RefreshNetworkStatus();
            RefreshVolumeStatus();
            RefreshBatteryStatus();
            _lastSystemStatusRefresh = now;
        }
    }

    internal void RefreshInputLanguageStatus()
    {
        var (foregroundWindow, _) = GetInputLanguageTarget();
        var foregroundThread = foregroundWindow == 0 ? 0 : GetWindowThreadProcessId(foregroundWindow, out _);
        var keyboardLayout = GetKeyboardLayout(foregroundThread);
        var languageId = unchecked((int)(keyboardLayout.ToInt64() & 0xFFFF));

        try
        {
            var inputCulture = languageId == 0
                ? CultureInfo.CurrentCulture
                : CultureInfo.GetCultureInfo(languageId);
            var language = inputCulture.ThreeLetterISOLanguageName.ToUpperInvariant();
            var cultureParts = inputCulture.Name.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var region = cultureParts.Length > 1 ? cultureParts[^1].ToUpperInvariant() : string.Empty;
            InputLanguageText.Text = language;
            InputLanguageRegionText.Text = region;
            if (DateTime.UtcNow >= _inputLanguageFeedbackExpiresAt)
                InputLanguageButton.ToolTip = $"Ngôn ngữ nhập: {inputCulture.EnglishName} · bấm để đổi";
        }
        catch (CultureNotFoundException)
        {
            InputLanguageText.Text = "ENG";
            InputLanguageRegionText.Text = string.Empty;
            if (DateTime.UtcNow >= _inputLanguageFeedbackExpiresAt)
                InputLanguageButton.ToolTip = "Ngôn ngữ nhập · bấm để đổi";
        }
    }

    internal void ShowInputLanguageFeedback(string message)
    {
        InputLanguageButton.ToolTip = message;
        _inputLanguageFeedbackExpiresAt = DateTime.UtcNow.AddSeconds(5);
    }

    private sealed record NetworkStateSnapshot(
        bool IsEthernet,
        bool IsWifi,
        bool HasInternet,
        string Ssid,
        int WifiLevel,
        uint? SignalQuality,
        bool Failed);

    private bool _networkScanInProgress;

    private void RefreshNetworkStatus()
    {
        if (_networkScanInProgress) return;
        _networkScanInProgress = true;

        Task.Run(() =>
        {
            try
            {
                Windows.Networking.Connectivity.ConnectionProfile? profile = null;
                try
                {
                    profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                }
                catch
                {
                    // Fallback gracefully if WinRT API is unavailable
                }

                var hasInternet = profile is not null &&
                                  profile.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;

                var activeAdapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                    .ToList();

                var activeWifi = activeAdapters.FirstOrDefault(a => a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                var activeEthernet = activeAdapters.FirstOrDefault(a => a.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet);

                // Determine if connected via Ethernet
                var isEthernet = (profile is { IsWlanConnectionProfile: false } && (profile.NetworkAdapter?.IanaInterfaceType == 6 || activeEthernet is not null))
                                 || (profile is null && activeEthernet is not null && NetworkInterface.GetIsNetworkAvailable());

                if (isEthernet)
                {
                    return new NetworkStateSnapshot(true, false, hasInternet, string.Empty, 1, null, false);
                }

                // Determine if connected via Wi-Fi
                var isWifi = (profile is not null && profile.IsWlanConnectionProfile)
                             || (activeWifi is not null && NetworkInterface.GetIsNetworkAvailable());

                if (isWifi)
                {
                    string ssid = profile?.ProfileName ?? string.Empty;
                    uint? signalQuality = null;

                    try
                    {
                        var connectedInfo = NativeWifi.ReadNetworks().FirstOrDefault(n => n.IsConnected);
                        if (connectedInfo is not null)
                        {
                            if (string.IsNullOrWhiteSpace(ssid)) ssid = connectedInfo.Name;
                            signalQuality = connectedInfo.SignalQuality;
                        }
                    }
                    catch
                    {
                        // NativeWifi fallback
                    }

                    byte? signalBars = profile?.GetSignalBars();

                    // Determine signal level (1 = full/4 bars, 2 = 3 bars, 3 = 2 bars, 4 = 1 bar)
                    int level = 1;
                    if (signalQuality.HasValue)
                    {
                        if (signalQuality.Value >= 70) level = 1;
                        else if (signalQuality.Value >= 45) level = 2;
                        else if (signalQuality.Value >= 20) level = 3;
                        else level = 4;
                    }
                    else if (signalBars.HasValue)
                    {
                        if (signalBars.Value >= 4) level = 1;
                        else if (signalBars.Value == 3) level = 2;
                        else if (signalBars.Value == 2) level = 3;
                        else level = 4;
                    }

                    return new NetworkStateSnapshot(false, true, hasInternet, ssid, level, signalQuality, false);
                }

                return new NetworkStateSnapshot(false, false, false, string.Empty, 1, null, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid Network] Background read failed: {ex.Message}");
                return new NetworkStateSnapshot(false, false, false, string.Empty, 1, null, true);
            }
        }).ContinueWith(t =>
        {
            _networkScanInProgress = false;
            if (t.IsCompletedSuccessfully && t.Result is { } snapshot)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => ApplyNetworkSnapshot(snapshot));
            }
        });
    }

    private void ApplyNetworkSnapshot(NetworkStateSnapshot snapshot)
    {
        if (snapshot.Failed)
        {
            NetworkGlyph.Opacity = 0.45;
            NetworkGlyph.Data = GetNetworkGeometry("ic_fluent_wifi_off_24_regular");
            NetworkButton.ToolTip = "Không đọc được trạng thái mạng · bấm để mở cài đặt nhanh";
            return;
        }

        if (snapshot.IsEthernet)
        {
            NetworkGlyph.Opacity = 1.0;
            NetworkGlyph.Data = GetNetworkGeometry("ic_fluent_ethernet_24_regular");
            NetworkButton.ToolTip = snapshot.HasInternet
                ? "Mạng dây đang kết nối (Internet) · bấm để mở cài đặt nhanh"
                : "Mạng dây đang kết nối · bấm để mở cài đặt nhanh";
            return;
        }

        if (snapshot.IsWifi)
        {
            if (!snapshot.HasInternet)
            {
                NetworkGlyph.Opacity = 1.0;
                NetworkGlyph.Data = GetNetworkGeometry("ic_fluent_wifi_warning_24_regular");
                NetworkButton.ToolTip = string.IsNullOrWhiteSpace(snapshot.Ssid)
                    ? "Wi-Fi đang kết nối (Không có Internet) · bấm để mở cài đặt nhanh"
                    : $"Wi-Fi: {snapshot.Ssid} (Không có Internet) · bấm để mở cài đặt nhanh";
                return;
            }

            NetworkGlyph.Opacity = 1.0;
            NetworkGlyph.Data = GetNetworkGeometry($"ic_fluent_wifi_{snapshot.WifiLevel}_24_regular");

            var signalDetail = snapshot.SignalQuality.HasValue ? $" · tín hiệu {snapshot.SignalQuality.Value}%" : string.Empty;
            var weakNote = snapshot.WifiLevel >= 3 ? " · Tín hiệu yếu" : string.Empty;
            var displayName = string.IsNullOrWhiteSpace(snapshot.Ssid) ? "Wi-Fi" : $"Wi-Fi: {snapshot.Ssid}";

            NetworkButton.ToolTip = $"{displayName}{weakNote}{signalDetail} · bấm để mở cài đặt nhanh";
            return;
        }

        // Disconnected / Lost Wi-Fi
        NetworkGlyph.Opacity = 0.55;
        NetworkGlyph.Data = GetNetworkGeometry("ic_fluent_wifi_off_24_regular");
        NetworkButton.ToolTip = "Chưa kết nối mạng · bấm để mở cài đặt nhanh";
    }

    private void RefreshBatteryStatus()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryLifePercent > 100)
        {
            BatteryButton.Visibility = Visibility.Collapsed;
            return;
        }

        var percent = status.BatteryLifePercent;
        BatteryButton.Visibility = Visibility.Visible;
        var charging = status.ACLineStatus == 1 || (status.BatteryFlag & 8) != 0;
        BatteryGlyph.Data = GetBatteryGeometry(percent, charging);
        BatteryGlyph.Tag = charging;
        BatteryButton.ToolTip = $"Pin {percent}%{(charging ? " · đang sạc" : string.Empty)} · bấm để mở cài đặt nhanh";
    }

    private void RefreshVolumeStatus()
    {
        Task.Run<int?>(() =>
        {
            if (WaveOutGetVolume(0, out var channels) != 0) return null;
            var left = (int)(channels & 0xFFFF);
            var right = (int)((channels >> 16) & 0xFFFF);
            return (left + right) / 2;
        }).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } volume)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => ApplyVolumeSnapshot(volume));
            }
        });
    }

    private void ApplyVolumeSnapshot(int volume)
    {
        VolumeGlyph.Text = volume == 0 ? "\uE992"
            : volume < 0x5555 ? "\uE993"
            : volume < 0xAAAA ? "\uE994"
            : "\uE995";
        var percent = (int)Math.Round(volume * 100.0 / ushort.MaxValue);
        VolumeButton.ToolTip = $"Âm lượng {percent}% · bấm để mở cài đặt nhanh";
    }

    private static Geometry GetBatteryGeometry(byte percent, bool charging)
    {
        var level = Math.Clamp(percent / 10, 0, 10);
        var name = charging
            ? $"ic_fluent_battery_charge_{level}_20_regular"
            : $"ic_fluent_battery_{level}_24_regular";
        return BatteryGeometries.Value.TryGetValue(name, out var geometry) ? geometry : Geometry.Empty;
    }

    private static Dictionary<string, Geometry> LoadBatteryGeometries()
    {
        const string resourcePrefix = "Glassquid.Dock.Assets.Battery.";
        var geometries = new Dictionary<string, Geometry>(StringComparer.Ordinal);
        var assembly = typeof(SystemTrayBarWindow).Assembly;

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal)
                         && name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            var document = XDocument.Load(stream);
            var pathData = document.Root?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "path")?
                .Attribute("d")?.Value;
            if (string.IsNullOrWhiteSpace(pathData)) continue;

            var geometry = Geometry.Parse(pathData);
            if (geometry.CanFreeze) geometry.Freeze();
            var key = resourceName[resourcePrefix.Length..^4];
            geometries[key] = geometry;
        }

        return geometries;
    }

    private static Geometry GetNetworkGeometry(string name)
    {
        return NetworkGeometries.Value.TryGetValue(name, out var geometry) ? geometry : Geometry.Empty;
    }

    private static Dictionary<string, Geometry> LoadNetworkGeometries()
    {
        const string resourcePrefix = "Glassquid.Dock.Assets.Network.";
        var geometries = new Dictionary<string, Geometry>(StringComparer.Ordinal);
        var assembly = typeof(SystemTrayBarWindow).Assembly;

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal)
                         && name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            var document = XDocument.Load(stream);
            var pathData = document.Root?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "path")?
                .Attribute("d")?.Value;
            if (string.IsNullOrWhiteSpace(pathData)) continue;

            var geometry = Geometry.Parse(pathData);
            if (geometry.CanFreeze) geometry.Freeze();
            var key = resourceName[resourcePrefix.Length..^4];
            geometries[key] = geometry;
        }

        return geometries;
    }

    private static Geometry LoadSvgGeometry(string resourceName)
    {
        using var stream = typeof(SystemTrayBarWindow).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The icon resource '{resourceName}' was not embedded.");
        var document = XDocument.Load(stream);
        var pathData = document.Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "path")?
            .Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(pathData))
            throw new InvalidDataException($"The icon resource '{resourceName}' does not contain an SVG path.");

        var geometry = Geometry.Parse(pathData);
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }

    private void OnWifiFlyoutClick(object sender, RoutedEventArgs e) => OpenWifiFlyout();

    internal void OpenWifiFlyout()
    {
        if (_wifiFlyoutWindow is { IsVisible: true } openFlyout)
        {
            openFlyout.Activate();
            return;
        }

        var flyout = new WifiFlyoutWindow(this);
        _wifiFlyoutWindow = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_wifiFlyoutWindow, flyout))
                _wifiFlyoutWindow = null;
        };

        try
        {
            flyout.PrepareForShow();
            flyout.Show();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Wi-Fi] Open failed: {ex}");
            _wifiFlyoutWindow = null;
        }
    }

    private void OnQuickSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_quickSettingsFlyoutWindow is { IsVisible: true } openFlyout)
        {
            openFlyout.Close();
            return;
        }

        OpenQuickSettingsFlyout();
    }

    internal void OpenQuickSettingsFlyout()
    {
        if (_quickSettingsFlyoutWindow is { IsVisible: true } existingFlyout)
        {
            existingFlyout.Activate();
            return;
        }

        _notificationsFlyoutWindow?.Close();

        var flyout = new QuickSettingsFlyoutWindow(this,
            NetworkButton.ToolTip?.ToString() ?? "Wi-Fi",
            VolumeButton.ToolTip?.ToString() ?? "Âm lượng",
            BatteryButton.Visibility == Visibility.Visible
                ? BatteryButton.ToolTip?.ToString() ?? "Pin"
                : "Thiết bị không có pin");
        _quickSettingsFlyoutWindow = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_quickSettingsFlyoutWindow, flyout))
                _quickSettingsFlyoutWindow = null;
        };

        try
        {
            flyout.PrepareForShow();
            flyout.Show();
            flyout.Activate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid QuickSettings] Open failed: {ex}");
            _quickSettingsFlyoutWindow = null;
        }
    }

    private void OnInputLanguageClick(object sender, RoutedEventArgs e)
    {
        if (_keyboardLayoutFlyoutWindow is { IsVisible: true } openFlyout)
        {
            openFlyout.Close();
            return;
        }

        var (targetWindow, focusWindow) = GetInputLanguageTarget();
        var layouts = KeyboardLayoutCatalog.Read(targetWindow);
        var flyout = new KeyboardLayoutFlyoutWindow(this, targetWindow, focusWindow, layouts);
        _keyboardLayoutFlyoutWindow = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_keyboardLayoutFlyoutWindow, flyout))
                _keyboardLayoutFlyoutWindow = null;
        };

        try
        {
            flyout.PrepareForShow();
            flyout.Show();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid KeyboardLayoutFlyout] Open failed: {ex}");
            _keyboardLayoutFlyoutWindow = null;
            InputLanguageButton.ToolTip = "Không mở được bảng chọn bố cục bàn phím của Glassquid.";
        }
    }

    private void OnInputLanguagePreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var foregroundWindow = GetForegroundWindow();
        var dockWindow = new WindowInteropHelper(this).Handle;
        if (foregroundWindow == 0 || foregroundWindow == dockWindow) return;

        _lastInputLanguageTargetWindow = foregroundWindow;
        _lastInputLanguageFocusWindow = KeyboardLayoutCatalog.GetFocusedWindow(foregroundWindow);
    }

    private (nint TargetWindow, nint FocusWindow) GetInputLanguageTarget()
    {
        var foregroundWindow = GetForegroundWindow();
        var dockWindow = new WindowInteropHelper(this).Handle;
        var flyoutWindow = _keyboardLayoutFlyoutWindow is null
            ? 0
            : new WindowInteropHelper(_keyboardLayoutFlyoutWindow).Handle;

        if (foregroundWindow != 0 && foregroundWindow != dockWindow && foregroundWindow != flyoutWindow)
        {
            _lastInputLanguageTargetWindow = foregroundWindow;
            _lastInputLanguageFocusWindow = KeyboardLayoutCatalog.GetFocusedWindow(foregroundWindow);
        }

        var targetWindow = _lastInputLanguageTargetWindow != 0
            ? _lastInputLanguageTargetWindow
            : foregroundWindow;
        var focusWindow = targetWindow == _lastInputLanguageTargetWindow && _lastInputLanguageFocusWindow != 0
            ? _lastInputLanguageFocusWindow
            : KeyboardLayoutCatalog.GetFocusedWindow(targetWindow);
        return (targetWindow, focusWindow);
    }

    private void OnNotificationsClick(object sender, RoutedEventArgs e)
    {
        if (_notificationsFlyoutWindow is { IsVisible: true } openFlyout)
        {
            openFlyout.Close();
            return;
        }

        OpenNotificationsFlyout();
    }

    internal void OpenNotificationsFlyout()
    {
        if (_notificationsFlyoutWindow is { IsVisible: true } existingFlyout)
        {
            existingFlyout.Activate();
            return;
        }

        // Close other tray flyouts to avoid overlap
        _quickSettingsFlyoutWindow?.Close();
        _wifiFlyoutWindow?.Close();
        _keyboardLayoutFlyoutWindow?.Close();
        CloseNativeOverflowForDock(
            restoreStyle: !_windowsTaskbarHidden,
            taskbarsMustStayHidden: _windowsTaskbarHidden);

        var flyout = new NotificationsFlyoutWindow(this);
        _notificationsFlyoutWindow = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_notificationsFlyoutWindow, flyout))
                _notificationsFlyoutWindow = null;
        };

        try
        {
            flyout.PrepareForShow();
            flyout.Show();
            flyout.Activate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Notifications] Open failed: {ex}");
            _notificationsFlyoutWindow = null;
        }
    }

    private void OnHiddenIconsPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Right)
        {
            e.Handled = true;
            if (TrayIconContextMenuRelay.IsNativeOverflowVisible() || HiddenIconsButton.Tag is true)
            {
                CloseNativeOverflowForDock(restoreStyle: true, taskbarsMustStayHidden: true);
                SetHiddenIconsPopupOpen(false);
            }
            else RestoreNativeOverflowKeyboardFocus();
            return;
        }
        _hiddenIconsPopupShouldClose = TrayIconContextMenuRelay.IsNativeOverflowVisible() || _hiddenIconsPopupOpening;
    }

    internal static bool IsNativeOverflowSessionActive =>
        _isAnyHiddenIconsPopupOpening
        || TrayIconContextMenuRelay.IsNativeOverflowVisible()
        || TrayIconContextMenuRelay.IsNativeOverflowClosing
        || TrayIconContextMenuRelay.IsOverflowOpen();
    private static volatile bool _isAnyHiddenIconsPopupOpening;

    private async void OnHiddenIconsClick(object sender, RoutedEventArgs e)
    {
        if (!_windowsTaskbarHidden) return;

        if (_hiddenIconsPopupOpening || TrayIconContextMenuRelay.IsNativeOverflowClosing) return;

        _isAnyHiddenIconsPopupOpening = true;
        var generation = ++_nativeOverflowGeneration;
        if (_hiddenIconsPopupShouldClose || TrayIconContextMenuRelay.IsNativeOverflowVisible())
        {
            _hiddenIconsPopupShouldClose = false;
            _hiddenIconsPopupOpening = true;
            _nativeOverflowWatcherTimer.Stop();
            try
            {
                await TrayIconContextMenuRelay.AnimateNativeOverflowCloseAsync();
                if (generation != _nativeOverflowGeneration) return;
                CloseNativeOverflowForDock(restoreStyle: true, taskbarsMustStayHidden: true);
                SetHiddenIconsPopupOpen(false);
            }
            finally
            {
                if (generation == _nativeOverflowGeneration)
                {
                    _hiddenIconsPopupOpening = false;
                    _isAnyHiddenIconsPopupOpening = false;
                }
            }
            return;
        }

        _hiddenIconsPopupOpening = true;
        RememberNativeOverflowKeyboardFocus();
        SetHiddenIconsPopupOpen(true, animateChevron: false);
        try
        {
            _quickSettingsFlyoutWindow?.Close();
            _wifiFlyoutWindow?.Close();
            _keyboardLayoutFlyoutWindow?.Close();
            _notificationsFlyoutWindow?.Close();

            TrayIconContextMenuRelay.PrepareNativeOverflowForDock();
            var suspend = _suspendWindowsTaskbarRefresh?.Invoke();
            bool opened;
            try
            {
                opened = await Task.Run(() =>
                {
                    return TrayIconContextMenuRelay.ToggleNativeOverflow(
                        allowKeyboardFallback: false,
                        taskbarsMustStayHidden: true);
                });
            }
            finally
            {
                suspend?.Dispose();
            }

            if (!opened)
            {
                CloseNativeOverflowForDock(restoreStyle: true, taskbarsMustStayHidden: true);
                SetHiddenIconsPopupOpen(false);
                return;
            }

            var anchor = GetHiddenIconsAnchorScreenPoint();
            SetHiddenIconsPopupOpen(true);
            var presented = await TrayIconContextMenuRelay.AnimateNativeOverflowOpenAsync(
                this, (int)anchor.X, (int)anchor.Y);
            if (generation != _nativeOverflowGeneration) return;
            if (presented)
            {
                _nativeOverflowWatcherTimer.Start();
            }
            else CloseNativeOverflowForDock(restoreStyle: true, taskbarsMustStayHidden: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid native tray overflow] Open failed: {ex}");
            if (generation == _nativeOverflowGeneration)
                CloseNativeOverflowForDock(restoreStyle: true, taskbarsMustStayHidden: _windowsTaskbarHidden);
        }
        finally
        {
            if (generation == _nativeOverflowGeneration)
            {
                _hiddenIconsPopupOpening = false;
                if (!TrayIconContextMenuRelay.IsNativeOverflowVisible())
                {
                    SetHiddenIconsPopupOpen(false);
                    _isAnyHiddenIconsPopupOpening = false;
                }
            }
        }
    }

    private void CloseNativeOverflowForDock(bool restoreStyle = true, bool taskbarsMustStayHidden = true)
    {
        _nativeOverflowGeneration++;
        _hiddenIconsPopupOpening = false;
        _isAnyHiddenIconsPopupOpening = false;
        _nativeOverflowWatcherTimer.Stop();
        var suspend = _suspendWindowsTaskbarRefresh?.Invoke();
        _ = Task.Run(() =>
        {
            try
            {
                using (suspend)
                {
                    TrayIconContextMenuRelay.CloseNativeOverflow(
                        restoreStyle: restoreStyle,
                        taskbarsMustStayHidden: taskbarsMustStayHidden);
                }
            }
            catch { }
        });
        SetHiddenIconsPopupOpen(false);
        RestoreNativeOverflowKeyboardFocus();
    }

    private void RememberNativeOverflowKeyboardFocus()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || foreground == Handle || TrayIconContextMenuRelay.IsNativeTrayFocusWindow(foreground)) return;
        _nativeOverflowReturnWindow = foreground;
        _ = GetWindowThreadProcessId(foreground, out _nativeOverflowReturnProcess);
    }

    private void RestoreNativeOverflowKeyboardFocus()
    {
        if (_nativeOverflowReturnWindow == 0) return;
        var foreground = GetForegroundWindow();
        if (foreground != 0 && !TrayIconContextMenuRelay.IsNativeTrayFocusWindow(foreground)) return;
        _ = GetWindowThreadProcessId(_nativeOverflowReturnWindow, out var processId);
        if (processId == 0 || processId != _nativeOverflowReturnProcess) return;
        _ = WindowActivation.TryBringToForeground(_nativeOverflowReturnWindow);
    }

    private void SetHiddenIconsPopupOpen(bool isOpen, bool animateChevron = true)
    {
        HiddenIconsButton.Tag = isOpen;
        if (!animateChevron) return;

        var animation = new DoubleAnimation
        {
            To = isOpen ? 180 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        HiddenIconsChevronRotation.BeginAnimation(
            RotateTransform.AngleProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    internal void AnimateHiddenIconsChevronOpen()
    {
        var animation = new DoubleAnimation
        {
            To = 180,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        HiddenIconsChevronRotation.BeginAnimation(
            RotateTransform.AngleProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnDockSettingsChanged(object? sender, EventArgs e)
    {
        RebuildAutoHide();
        ApplyDockMetrics();
        ApplyAppearance();
        _slideOffsetPixels = 0;
        QueuePositionOnDisplay();
    }

    private bool IsOpticalSurfaceVisible => _slideOffsetPixels < Math.Max(1, _restingRect.Height - 4);

    private void RebuildAutoHide()
    {
        var settings = _dockSettings?.Settings;
        if (settings is null) return;

        _autoHide = new AutoHideController(settings.AutoHideLeaveDelayMs);
        _autoHide.ForceShown(DateTime.UtcNow);
        _slideOffsetPixels = 0;
        _edgeRevealedDuringSuppression = false;
        _revealPreparationStarted = 0;

        if (_autoHideFrameTimer is null)
        {
            _autoHideFrameTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = AutoHideFrameInterval,
            };
            _autoHideFrameTimer.Tick += (_, _) => OnAutoHideFrame();
        }

        if (!_isForegroundHookSubscribed)
        {
            _isForegroundHookSubscribed = true;
            ForegroundEventWatcher.Subscribe(OnSystemForegroundChanged);
        }

        _autoHideFrameTimer.Interval = AutoHideFrameInterval;
        _autoHideFrameTimer.IsEnabled = false;
    }

    private void OnAutoHideFrame()
    {
        if (_mainDock is not null)
        {
            _autoHideFrameTimer?.Stop();
            return;
        }

        if (_performanceSleeping || _autoHide is null || _restingRect.Width <= 0) return;

        if (IsNativeOverflowSessionActive)
        {
            _desktopFocusStartTime = DateTimeOffset.MinValue;
            _autoHideFrameTimer?.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        var cursor = DisplayService.GetCursorPixelPoint();
        var fullscreen = FullscreenDetector.IsFullscreenActive();
        var covered = ForegroundCoverage.IsCovered(_restingRect);
        var pointerOverVisibleTray = cursor is { } currentPoint && IsPointerOverTray(currentPoint);
        var pointerOverTrayPosition = cursor is { } position && IsPointerOverTrayPosition(position);
        var trayPopupOpen = _hiddenIconsPopupOpening
            || HiddenIconsButton.Tag is true
            || _quickSettingsFlyoutWindow?.IsVisible == true
            || _wifiFlyoutWindow?.IsVisible == true
            || _keyboardLayoutFlyoutWindow?.IsVisible == true
            || _notificationsFlyoutWindow?.IsVisible == true;

        if (trayPopupOpen)
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }
        else if (fullscreen)
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceHide(now);
        }
        else if (covered)
        {
            var revealLocked = _mainDock?.IsRevealLocked ?? true;
            if (revealLocked)
            {
                _edgeRevealedDuringSuppression = false;
                _autoHide.ForceHide(now);
            }
            else if (_autoHide.State == AutoHideState.Hidden)
            {
                if (PassiveRevealPolicy.CanRevealDuringSuppression(pointerOverTrayPosition, revealLocked)
                    && _autoHide.TryReveal(isCovered: false, now))
                    _edgeRevealedDuringSuppression = true;
            }
            else if (_edgeRevealedDuringSuppression)
            {
                if (pointerOverTrayPosition || pointerOverVisibleTray) _autoHide.PointerEnter(now);
                else _autoHide.PointerLeave(now);
                _autoHide.Poll(now);
            }
            else
            {
                _autoHide.ForceHide(now);
            }
        }
        else if (_dockSettings?.Settings.AutoHide == true)
        {
            _edgeRevealedDuringSuppression = false;
            if (pointerOverTrayPosition || pointerOverVisibleTray)
                _autoHide.PointerEnter(now);
            else
            {
                _autoHide.PointerLeave(now);
                _autoHide.Poll(now);
            }
        }
        else
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }

        if (_autoHide.State == AutoHideState.Hidden) _edgeRevealedDuringSuppression = false;

        var target = _autoHide.State == AutoHideState.Hidden ? (double)_fullyHiddenOffsetPixels : 0.0;
        if (target == 0) PrepareConcealedReveal();
        if (target == 0 && !CanStartPreparedReveal(System.Diagnostics.Stopwatch.GetTimestamp()))
        {
            _autoHideFrameTimer!.Interval = AutoHideFrameInterval;
            return;
        }
        if (target != 0)
        {
            _revealPreparationStarted = 0;
            _revealMaterialPrepared = false;
        }

        if (Math.Abs(_slideOffsetPixels - target) > 0.5)
        {
            _slideOffsetPixels += (target - _slideOffsetPixels) * 0.22;
            if (Math.Abs(_slideOffsetPixels - target) <= 0.5) _slideOffsetPixels = target;
            ApplySlide(_restingRect, _slideOffsetPixels);
        }
        else if (_slideOffsetPixels != target)
        {
            _slideOffsetPixels = target;
            ApplySlide(_restingRect, _slideOffsetPixels);
        }

        if (Math.Abs(_slideOffsetPixels - target) <= 0.5)
        {
            _autoHideFrameTimer?.Stop();
        }
    }

    private void PrepareConcealedReveal()
    {
        if (_revealMaterialPrepared) return;
        if (_restingRect.Width > 0 && _panelWasConcealed
            && PrimaryDisplaySession?.Display is { } display)
        {
            _revealMaterialPrepared = true;
            GlassSurface.PrepareRevealMaterial(_restingRect, _restingRect, display.Dpi);
        }
    }

    private bool CanStartPreparedReveal(long now)
    {
        if (_restingRect.Height <= 0 || !_panelWasConcealed || GlassSurface.IsCurrentRevealMaterialReady)
        {
            _revealPreparationStarted = 0;
            return true;
        }

        if (_revealPreparationStarted == 0) _revealPreparationStarted = now;
        return now - _revealPreparationStarted >= System.Diagnostics.Stopwatch.Frequency / 20;
    }

    private void ApplySlide(PixelRect restingRect, double slideOffsetPixels, DisplayInfo? display = null)
    {
        var slidePixels = (int)Math.Round(slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var concealed = restingRect.Height > 0 && Math.Abs(slidePixels) >= restingRect.Height;
        if (concealed && !_panelWasConcealed)
        {
            GlassSurface.RetireConcealedMaterial();
            _revealMaterialPrepared = false;
        }
        _panelWasConcealed = concealed;

        var backdropActive = IsOpticalSurfaceVisible;
        if (backdropActive && !_backdropWasActive) GlassSurface.RequestBackdropRefresh(revealing: true);
        _backdropWasActive = backdropActive;

        display ??= PrimaryDisplaySession?.Display;
        var handle = Handle;
        if (handle != 0 && PrimaryDisplaySession is { IsAvailable: true })
        {
            if (DisplayService.GetWindowPixelBounds(handle) != restingRect)
            {
                _ = DisplayService.MoveWindowToPixelRect(handle, restingRect);
            }
        }

        var scale = Math.Max(.01, display?.ScaleFactor ?? 1);
        var visualOffsetDip = slidePixels / scale;
        SystemTraySlideTransform.Y = visualOffsetDip;

        if (ActualWidth > 0 && ActualHeight > 0)
        {
            var panelBounds = new Rect(0, visualOffsetDip, ActualWidth, ActualHeight);
            GlassSurface.SetPanelShape(panelBounds, PanelCornerRadius.TopLeft);
            var hiddenOffsetDip = _fullyHiddenOffsetPixels * SlideOutDirection() / scale;
            GlassSurface.SetContainerTransitionBounds(new Rect(0, Math.Min(0, hiddenOffsetDip), ActualWidth,
                ActualHeight + Math.Abs(hiddenOffsetDip)));
            SetSharedPanelClip(panelBounds, PanelCornerRadius.TopLeft);
            var shadowStrength = Math.Clamp(1 - Math.Abs(visualOffsetDip) / Math.Max(1, ActualHeight), 0, 1);
            SetTransitionShadowShape(Math.Abs(visualOffsetDip) < .01 ? null : panelBounds,
                PanelCornerRadius.TopLeft, shadowStrength);
        }
    }

    private void StartSlide()
    {
        if (_mainDock is not null)
        {
            _mainDock.WakeSlide();
            return;
        }
        if (_performanceSleeping || _autoHide is null || _restingRect.Width <= 0) return;
        if (IsNativeOverflowSessionActive) return;
        if (_autoHideFrameTimer is not null)
        {
            _autoHideFrameTimer.Interval = AutoHideFrameInterval;
            _autoHideFrameTimer.Start();
        }
    }


    private PixelRect GetVisibleTrayRect()
    {
        var slidePixels = (int)Math.Round(_slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var translated = _restingRect with { Y = _restingRect.Y + slidePixels };
        var left = Math.Max(_restingRect.X, translated.X);
        var top = Math.Max(_restingRect.Y, translated.Y);
        var right = Math.Min(_restingRect.Right, translated.Right);
        var bottom = Math.Min(_restingRect.Bottom, translated.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(_restingRect.X, _restingRect.Y, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    private static PixelRect Inflate(PixelRect rect, int by)
        => new(rect.X - by, rect.Y - by, rect.Width + by * 2, rect.Height + by * 2);

    private void QueuePositionOnDisplay()
    {
        if (!IsLoaded || _positionUpdateQueued) return;
        _positionUpdateQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _positionUpdateQueued = false;
            if (!IsVisible) return;
            UpdateLayout();
            PositionOnDisplay();
        }));
    }

    private void ApplyDockMetrics()
    {
        var scale = DockScale;
        // Keep the row on the Dock scale. The wider leading inset and compact item gaps
        // align the chevron, language, network, volume, battery, clock and alert groups
        // with the Windows taskbar tray.
        SystemTrayVisualRoot.LayoutTransform = new ScaleTransform(scale, scale);
        InputLanguageText.FontSize = 11 / scale;
        InputLanguageRegionText.FontSize = 8 / scale;
        NetworkGlyph.Width = 20 / scale;
        NetworkGlyph.Height = 20 / scale;
        VolumeGlyph.FontSize = 18 / scale;
        BatteryGlyph.Width = 20 / scale;
        BatteryGlyph.Height = 20 / scale;
        NotificationsGlyph.Width = 20 / scale;
        NotificationsGlyph.Height = 20 / scale;
        // The taskbar clock keeps its screen size when DockScale changes, so
        // compensate for the parent transform and match that effective 12-DIP text.
        ClockText.FontSize = 12 / scale;
        DateText.FontSize = 12 / scale;
        SystemTrayContentLayer.Margin = new Thickness(_windowsTaskbarHidden ? 16 : 10, 8, 8, 8);
    }

    private double DockScale
    {
        get
        {
            var scale = _dockSettings?.Settings.SizeScale ?? 1;
            return double.IsFinite(scale)
                ? Math.Clamp(scale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale)
                : 1;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PrimaryDisplaySession is { IsAvailable: false })
        {
            Hide();
            return;
        }

        WindowStyles.MakeNoActivate(new WindowInteropHelper(this).Handle);
        PositionOnDisplay();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        PositionOnDisplay();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_mainDock is not null)
        {
            _mainDock.WakeSlide();
        }
        else if (_autoHideFrameTimer is not null && !_autoHideFrameTimer.IsEnabled)
        {
            StartSlide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _nativeOverflowGeneration++;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _nativeOverflowWatcherTimer.Stop();
        _autoHideFrameTimer?.Stop();
        TrayIconContextMenuRelay.CloseNativeOverflow();
        TrayIconContextMenuRelay.RestoreNativeOverflowStyle(immediately: true);
        _keyboardLayoutFlyoutWindow?.Close();
        _notificationsFlyoutWindow?.Close();
        WindowsDoNotDisturbController.StateChanged -= OnDoNotDisturbStateChanged;
        if (!_isSecondary)
        {
            Tray.ManagedShellNotificationArea.Instance.Dispose();
        }
        if (_dockSettings is not null) _dockSettings.Changed -= OnDockSettingsChanged;
        base.OnClosed(e);
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        RefreshStatus();
        if (_windowsTaskbarHidden && !_hiddenIconsPopupOpening
            && HiddenIconsButton.Tag is true
            && !TrayIconContextMenuRelay.IsNativeOverflowVisible())
        {
            _nativeOverflowWatcherTimer.Stop();
            SetHiddenIconsPopupOpen(false);
            CloseNativeOverflowForDock(restoreStyle: true, taskbarsMustStayHidden: true);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out NativeSystemPowerStatus status);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("winmm.dll", EntryPoint = "waveOutGetVolume")]
    private static extern uint WaveOutGetVolume(nint deviceHandle, out uint volume);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, [Out] char[] className, int maxCount);

    private bool _hasActiveExternalApp;
    private DateTimeOffset _desktopFocusStartTime = DateTimeOffset.MinValue;
    private bool _desktopSlideTriggered = true;
    private bool _isForegroundHookSubscribed;
    private DispatcherTimer? _desktopDebounceTimer;
    private bool _revealMaterialPrepared;

    private void OnSystemForegroundChanged(nint currentFg)
    {
        if (_performanceSleeping || _autoHide is null || _restingRect.Width <= 0) return;
        if (IsNativeOverflowSessionActive)
        {
            _desktopFocusStartTime = DateTimeOffset.MinValue;
            return;
        }

        if (IsExternalApp(currentFg))
        {
            _hasActiveExternalApp = true;
            _desktopSlideTriggered = false;
            _desktopFocusStartTime = DateTimeOffset.MinValue;
            var covered = ForegroundCoverage.IsCovered(_restingRect);
            if (covered)
            {
                _autoHide.ForceHide(DateTime.UtcNow);
                StartSlide();
            }
        }
        else if (IsDesktopWindow(currentFg))
        {
            if (_hasActiveExternalApp && !_desktopSlideTriggered)
            {
                _desktopFocusStartTime = DateTimeOffset.UtcNow;
                _desktopDebounceTimer?.Stop();
                _desktopDebounceTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(60)
                };
                _desktopDebounceTimer.Tick += (_, _) =>
                {
                    _desktopDebounceTimer?.Stop();
                    _desktopDebounceTimer = null;
                    CheckDesktopReturnAfterDebounce();
                };
                _desktopDebounceTimer.Start();
            }
            else
            {
                _desktopFocusStartTime = DateTimeOffset.MinValue;
            }
        }
        else
        {
            _desktopFocusStartTime = DateTimeOffset.MinValue;
        }
    }

    private void CheckDesktopReturnAfterDebounce()
    {
        if (_performanceSleeping || _autoHide is null || _restingRect.Width <= 0) return;
        if (IsNativeOverflowSessionActive) return;

        var currentFg = WindowControl.GetForegroundWindow();
        if (IsDesktopWindow(currentFg) && _hasActiveExternalApp && !_desktopSlideTriggered)
        {
            _hasActiveExternalApp = false;
            _desktopSlideTriggered = true;
            _desktopFocusStartTime = DateTimeOffset.MinValue;

            if (_slideOffsetPixels > 10.0 || _autoHide.State == AutoHideState.Hidden)
            {
                _slideOffsetPixels = _fullyHiddenOffsetPixels;
                ApplySlide(_restingRect, _slideOffsetPixels);
                if (_dockSettings?.Settings.AutoHide != true)
                {
                    _autoHide.ForceShown(DateTime.UtcNow);
                }
                StartSlide();
            }
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetAncestor(nint hwnd, uint flags);
    private const uint GaRoot = 2;

    private static bool IsOurProcessWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    public void WakeForDesktop()
    {
        Dispatcher.VerifyAccess();
        _hasActiveExternalApp = false;
        _desktopSlideTriggered = true;
        _desktopFocusStartTime = DateTimeOffset.MinValue;
        if (_performanceSleeping)
        {
            SetPerformanceSleeping(false);
        }
        if (_mainDock is not null)
        {
            _mainDock.WakeForDesktop();
            return;
        }
        if (_autoHide is not null)
        {
            _autoHide.ForceShown(DateTime.UtcNow);
            StartSlide();
        }
    }

    private static bool IsDesktopWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        var root = GetAncestor(hwnd, GaRoot);
        if (root == 0) root = hwnd;
        var name = new char[64];
        var len = GetClassName(root, name, name.Length);
        if (len > 0)
        {
            var rootClassName = new string(name, 0, len);
            if (rootClassName is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32") return true;
        }
        len = GetClassName(hwnd, name, name.Length);
        if (len <= 0) return false;
        var className = new string(name, 0, len);
        return className is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32";
    }

    private static bool IsExternalApp(nint hwnd)
    {
        if (hwnd == 0) return false;
        if (IsOurProcessWindow(hwnd)) return false;
        if (IsDesktopWindow(hwnd)) return false;
        if (TrayIconContextMenuRelay.IsNativeTrayWindow(hwnd)) return false;
        return true;
    }
}
