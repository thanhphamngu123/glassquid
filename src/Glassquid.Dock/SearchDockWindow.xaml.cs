using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glassquid.Core.Models.Settings;
using Glassquid.Platform;
using Glassquid.Platform.Displays;
using Glassquid.Platform.Shell;
using Glassquid.UI.Controls;
using Glassquid.UI.Settings;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>
/// Thanh dock nút tìm kiếm (Search Dock) đặt ở mép màn hình:
/// - Mặc định ở góc dưới bên trái màn hình (DockPlacement.BottomCenter / BottomLeft / BottomRight).
/// - Hoặc đặt ở góc trên bên trái khi người dùng chọn cấu hình cạnh trên (DockPlacement.TopCenter).
/// - Cung cấp nút tìm kiếm dạng viên thuốc (Capsule), mở cửa sổ tìm kiếm gần đây (RecentSearchFlyoutWindow)
///   hoặc kích hoạt tìm kiếm Windows (Win + S).
/// - Hỗ trợ hiệu ứng kính mờ lỏng (LiquidGlassSurface), auto-hide, phát hiện ứng dụng che khuất và hover animation.
/// </summary>
public partial class SearchDockWindow : GlassWindow
{
    // =========================================================================================
    // HẰNG SỐ & ĐỊNH THÌ AUTO-HIDE
    // =========================================================================================

    /// <summary>Khoảng thời gian mỗi frame cập nhật animation trượt và kiểm tra trạng thái auto-hide (~60 FPS: 16ms).</summary>
    private static readonly TimeSpan AutoHideFrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>Khoảng thời gian cập nhật khi dock đang ở trạng thái nghỉ.</summary>
    private static readonly TimeSpan AutoHideIdleInterval = TimeSpan.FromMilliseconds(200);

    // =========================================================================================
    // CÁC BIẾN QUẢN LÝ TRẠNG THÁI & HIỆU ỨNG TRƯỢT (SLIDE ANIMATION)
    // =========================================================================================

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

    public bool IsPointerOverSearch((int X, int Y) cursor)
        => Inflate(GetVisibleSearchRect(), 2).Contains(cursor.X, cursor.Y);

    public bool IsPointerOverSearchPosition((int X, int Y) cursor)
    {
        var displayBounds = PrimaryDisplaySession?.Display.Bounds;
        var leftLimit = displayBounds?.X ?? _restingRect.X;
        var searchHotzone = IsTopPlacement
            ? new PixelRect(
                leftLimit - 10,
                displayBounds?.Y ?? (_restingRect.Y - 10),
                Math.Max(_restingRect.Width + 10, _restingRect.Right - leftLimit + 10),
                Math.Max(_restingRect.Height + 10, _restingRect.Bottom - (displayBounds?.Y ?? _restingRect.Y) + 10))
            : new PixelRect(
                leftLimit - 10,
                _restingRect.Y - 10,
                Math.Max(_restingRect.Width + 10, _restingRect.Right - leftLimit + 10),
                Math.Max(_restingRect.Height + 10, (displayBounds?.Bottom ?? _restingRect.Bottom) - _restingRect.Y + 10));
        return searchHotzone.Contains(cursor.X, cursor.Y) || Inflate(_restingRect, 2).Contains(cursor.X, cursor.Y);
    }

    public bool HasActivePopup() => _recentSearchFlyoutWindow?.IsVisible == true;

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
    private DockSettingsHost? _dockSettings;

    /// <summary>Kiểm tra xem dock hiện tại có đang được cấu hình đặt ở mép trên màn hình hay không.</summary>
    public bool IsTopPlacement => _dockSettings?.Settings.Placement == DockPlacement.TopCenter;

    /// <summary>
    /// Hướng trượt khi ẩn dock:
    /// - Khi ở trên cùng (IsTopPlacement): trượt ngược lên trên (-1) ra khỏi mép trên màn hình.
    /// - Khi ở dưới cùng: trượt xuống dưới (+1) ra khỏi mép đáy màn hình.
    /// </summary>
    private int SlideOutDirection() => IsTopPlacement ? -1 : 1;

    private bool _performanceSleeping;
    private bool _positionUpdateQueued;
    private bool _windowsTaskbarHidden;
    private PixelRect _restingRect;
    private int _fullyHiddenOffsetPixels;
    private double _slideOffsetPixels;
    private bool _edgeRevealedDuringSuppression;
    private bool _panelWasConcealed;
    private bool _backdropWasActive;
    private long _revealPreparationStarted;
    private Rect? _slidePanelBoundsDip;

    // =========================================================================================
    // HIỆU ỨNG PHÓNG TO KHI RÊ CHUỘT (HOVER SCALE ANIMATION)
    // =========================================================================================

    private DockRenderLoop? _hoverLoop;
    private readonly ScaleTransform _searchHoverScale = new(1, 1);
    private bool _searchHovered;

    // =========================================================================================
    // HÌNH HỌC KÍNH MỜ (SQUIRCLE GLASS GEOMETRY & PALETTE)
    // =========================================================================================

    /// <summary>Độ đục màu nền acrylic: 54 cho Dark theme, 66 cho Light theme.</summary>
    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    /// <summary>Tạo hình học bo góc Squircle siêu mịn cho khung kính.</summary>
    protected override Geometry CreatePanelGeometryCore(Rect bounds, double radius) =>
        SquircleGlassGeometry.Create(bounds, radius);

    /// <summary>Kiểm tra toạ độ điểm trỏ chuột có nằm trong vùng hình học kính hay không.</summary>
    protected override bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) =>
        SquircleGlassGeometry.Contains(_slidePanelBoundsDip ?? bounds, radius, point);

    /// <summary>Lấy thông tin pháp tuyến và độ sâu mép kính để render hiệu ứng phản chiếu ánh sáng viền.</summary>
    protected override bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        SquircleGlassGeometry.TryGetEdge(_slidePanelBoundsDip ?? bounds, radius, point, out depth, out inwardNormal);

    /// <summary>Sử dụng hình học Squircle thay vì bo tròn thông thường.</summary>
    protected override bool UsesSquirclePanelGeometryCore => true;

    /// <summary>Tính toán bán kính góc bo dựa trên cấu hình giao diện và tỉ lệ scale của dock.</summary>
    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(40, appearance.DockCornerRadius) * DockScale;
    }

    // =========================================================================================
    // KHỞI TẠO CỬA SỔ & VÒNG ĐỜI
    // =========================================================================================

    public SearchDockWindow()
    {
        InitializeComponent();
        InitializeHover();
        PanelCornerRadius = new CornerRadius(40);

        // Kính mờ chỉ kích hoạt vẽ khi cửa sổ đang hiển thị và bề mặt quang học chưa bị che khuất hoàn toàn
        GlassSurface.IsBackdropActive = () => IsVisible && IsOpticalSurfaceVisible;
        GlassSurface.SuppressAmbientFallback = true;

        // Đưa việc định vị cửa sổ vào hàng đợi khi cửa sổ đã load xong
        Loaded += (_, _) => QueuePositionOnDisplay();

        // Xử lý khi trạng thái hiển thị của cửa sổ thay đổi
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                QueuePositionOnDisplay();
                if (_autoHideFrameTimer is not null) _autoHideFrameTimer.IsEnabled = !_performanceSleeping;
            }
            else
            {
                _autoHideFrameTimer?.Stop();
                ResetHover();
            }
        };
    }

    /// <summary>Khởi tạo transform và vòng lặp render animation phóng to khi rê chuột vào nút search.</summary>
    private void InitializeHover()
    {
        // Giao diện nút tìm kiếm đã được comment out theo yêu cầu:
        // SearchTile.RenderTransformOrigin = new Point(0.5, 0.5);
        // SearchTile.RenderTransform = _searchHoverScale;
        _hoverLoop = new DockRenderLoop(AnimateHoverFrame);
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) ResetHover(); };
        Closed += (_, _) => ResetHover();
    }

    // =========================================================================================
    // XỬ LÝ SỰ KIỆN RÊ CHUỘT (MOUSE HOVER)
    // =========================================================================================

    private void OnSearchMouseEnter(object sender, MouseEventArgs e) => SetHovered(true);
    private void OnSearchMouseLeave(object sender, MouseEventArgs e) => SetHovered(false);

    private void SetHovered(bool hovered)
    {
        _searchHovered = hovered;
        if (IsVisible && !_performanceSleeping) _hoverLoop?.Start();
    }

    /// <summary>Bước tính toán frame nội suy kích thước phóng to/thu nhỏ nút tìm kiếm khi hover.</summary>
    private bool AnimateHoverFrame(double deltaSeconds)
    {
        var target = _searchHovered ? DockSettings.FixedMagnificationPeak : 1;
        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            _searchHoverScale.ScaleX = _searchHoverScale.ScaleY = target;
            return true;
        }
        var scale = DockHoverScale.Advance(_searchHoverScale.ScaleX, target, deltaSeconds);
        _searchHoverScale.ScaleX = _searchHoverScale.ScaleY = scale;
        return scale == target;
    }

    /// <summary>Đặt lại kích thước nút tìm kiếm về mặc định khi rời chuột hoặc đóng cửa sổ.</summary>
    private void ResetHover()
    {
        _hoverLoop?.Stop();
        _searchHovered = false;
        _searchHoverScale.ScaleX = _searchHoverScale.ScaleY = 1;
    }

    // =========================================================================================
    // XỬ LÝ CLICK NÚT TÌM KIẾM & MỞ CỬA SỔ FLYOUT
    // =========================================================================================

    private RecentSearchFlyoutWindow? _recentSearchFlyoutWindow;

    private void OnSearchButtonClick(object sender, RoutedEventArgs e)
    {
        ToggleRecentSearchFlyout();
    }

    /// <summary>
    /// Đóng/mở cửa sổ tìm kiếm gần đây (RecentSearchFlyoutWindow).
    /// Nếu flyout đang mở thì đóng lại, ngược lại thì tạo mới và hiển thị.
    /// </summary>
    public void ToggleRecentSearchFlyout()
    {
        if (_recentSearchFlyoutWindow is { IsVisible: true } openFlyout)
        {
            openFlyout.Close();
            return;
        }

        var flyout = new RecentSearchFlyoutWindow(this);
        _recentSearchFlyoutWindow = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_recentSearchFlyoutWindow, flyout))
                _recentSearchFlyoutWindow = null;
        };

        try
        {
            flyout.PrepareForShow();
            flyout.Show();
            flyout.Activate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid SearchDock] Open flyout failed: {ex}");
            _recentSearchFlyoutWindow = null;
        }
    }

    /// <summary>
    /// Tính toán toạ độ điểm neo trên màn hình (Anchor Point) để căn vị trí hiển thị cho cửa sổ Flyout.
    /// - Khi ở mép đáy: neo tại đỉnh nút tìm kiếm (Flyout mở lên trên).
    /// - Khi ở mép trên (IsTopPlacement): neo tại đáy nút tìm kiếm (Flyout mở xuống dưới).
    /// </summary>
    public Point GetSearchAnchorPoint()
    {
        // Giao diện nút tìm kiếm đã được comment out theo yêu cầu:
        // var screenPoint = SearchButton.PointToScreen(new Point(0, IsTopPlacement ? SearchButton.ActualHeight : 0));
        var screenPoint = new Point(0, 0);
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(screenPoint)
            : screenPoint;
    }

    // =========================================================================================
    // GỌI WIN32 API MỞ TÌM KIẾM HỆ THỐNG WINDOWS (WIN + S)
    // =========================================================================================

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

    private const byte VkLWin = 0x5B;
    private const byte VkS = 0x53;
    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>Mô phỏng phím tắt Win + S để mở giao diện tìm kiếm mặc định của Windows.</summary>
    public static void OpenWindowsSearch()
    {
        keybd_event(VkLWin, 0, 0, 0);
        keybd_event(VkS, 0, 0, 0);
        keybd_event(VkS, 0, KeyEventKeyUp, 0);
        keybd_event(VkLWin, 0, KeyEventKeyUp, 0);
    }

    // =========================================================================================
    // KẾT NỐI & ĐỒNG BỘ CẤU HÌNH CÀI ĐẶT DOCK (DOCK SETTINGS)
    // =========================================================================================

    /// <summary>Gắn kết nguồn cài đặt dock và đăng ký lắng nghe sự kiện thay đổi.</summary>
    public void AttachDockSettings(DockSettingsHost settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (ReferenceEquals(_dockSettings, settings)) return;
        if (_dockSettings is not null) _dockSettings.Changed -= OnDockSettingsChanged;
        _dockSettings = settings;
        _dockSettings.Changed += OnDockSettingsChanged;
        ApplyDockMetrics();
        ApplyAppearance();
        RebuildAutoHide();
        QueuePositionOnDisplay();
    }

    private void OnDockSettingsChanged(object? sender, EventArgs e)
    {
        RebuildAutoHide();
        ApplyDockMetrics();
        ApplyAppearance();
        _slideOffsetPixels = 0;
        QueuePositionOnDisplay();
    }

    /// <summary>Hệ số tỉ lệ phóng to/thu nhỏ dock từ cài đặt người dùng.</summary>
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

    /// <summary>Áp dụng scale cho visual root và lề margin phù hợp với trạng thái ẩn taskbar của Windows.</summary>
    private void ApplyDockMetrics()
    {
        var scale = DockScale;
        SearchDockVisualRoot.LayoutTransform = new ScaleTransform(scale, scale);
        // Giao diện nút tìm kiếm đã được comment out theo yêu cầu:
        // SearchDockContentLayer.Margin = new Thickness(_windowsTaskbarHidden ? 16 : 10, 8, _windowsTaskbarHidden ? 16 : 10, 8);
    }

    /// <summary>Kiểm tra xem bề mặt kính có còn trong tầm nhìn hay đã trượt ẩn hoàn toàn.</summary>
    private bool IsOpticalSurfaceVisible => _slideOffsetPixels < Math.Max(1, _restingRect.Height - 4);

    // =========================================================================================
    // CƠ CHẾ TỰ ĐỘNG ẨN / HIỆN (AUTO-HIDE CONTROLLER & TIMER)
    // =========================================================================================

    /// <summary>Khởi tạo lại bộ điều khiển Auto-hide dựa trên cài đặt hiện tại.</summary>
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

        // Lắng nghe sự kiện chuyển đổi ứng dụng foreground của hệ thống
        if (!_isForegroundHookSubscribed)
        {
            _isForegroundHookSubscribed = true;
            ForegroundEventWatcher.Subscribe(OnSystemForegroundChanged);
        }

        _autoHideFrameTimer.Interval = AutoHideFrameInterval;
        _autoHideFrameTimer.IsEnabled = false;
    }

    /// <summary>
    /// Vòng lặp kiểm tra frame auto-hide (chạy mỗi 16ms):
    /// - Kiểm tra ứng dụng toàn màn hình (Fullscreen) -> ẩn dock.
    /// - Kiểm tra cửa sổ khác che khuất vị trí dock -> ẩn dock.
    /// - Kiểm tra con trỏ chuột trong vùng hotzone mép màn hình -> hiện dock.
    /// - Tính toán bước trượt _slideOffsetPixels và gọi ApplySlide để di chuyển thanh dock mượt mà.
    /// </summary>
    private void OnAutoHideFrame()
    {
        if (_mainDock is not null)
        {
            _autoHideFrameTimer?.Stop();
            return;
        }

        if (_performanceSleeping || _autoHide is null || _restingRect.Width <= 0) return;

        // Tạm dừng khi menu overflow khay hệ thống đang mở
        if (SystemTrayBarWindow.IsNativeOverflowSessionActive)
        {
            _desktopFocusStartTime = DateTimeOffset.MinValue;
            _autoHideFrameTimer?.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        var cursor = DisplayService.GetCursorPixelPoint();
        var fullscreen = FullscreenDetector.IsFullscreenActive();
        var covered = ForegroundCoverage.IsCovered(_restingRect);
        var isTop = IsTopPlacement;
        var displayBounds = PrimaryDisplaySession?.Display.Bounds;
        var leftLimit = displayBounds?.X ?? _restingRect.X;

        // Vùng hotzone kích hoạt dock xuất hiện khi rê chuột vào mép màn hình
        var searchHotzone = isTop
            ? new PixelRect(
                leftLimit,
                displayBounds?.Y ?? (_restingRect.Y - 10),
                Math.Max(_restingRect.Width + 10, _restingRect.Right - leftLimit + 10),
                Math.Max(_restingRect.Height + 10, _restingRect.Bottom - (displayBounds?.Y ?? _restingRect.Y) + 10))
            : new PixelRect(
                leftLimit,
                _restingRect.Y - 10,
                Math.Max(_restingRect.Width + 10, _restingRect.Right - leftLimit + 10),
                Math.Max(_restingRect.Height + 10, (displayBounds?.Bottom ?? _restingRect.Bottom) - _restingRect.Y + 10));

        var pointerOverVisibleSearch = cursor is { } currentPoint
            && Inflate(GetVisibleSearchRect(), 2).Contains(currentPoint.X, currentPoint.Y);
        var pointerOverSearchPosition = cursor is { } position
            && (searchHotzone.Contains(position.X, position.Y) || Inflate(_restingRect, 2).Contains(position.X, position.Y));

        if (fullscreen)
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
                if (PassiveRevealPolicy.CanRevealDuringSuppression(pointerOverSearchPosition, revealLocked)
                    && _autoHide.TryReveal(isCovered: false, now))
                    _edgeRevealedDuringSuppression = true;
            }
            else if (_edgeRevealedDuringSuppression)
            {
                if (pointerOverSearchPosition || pointerOverVisibleSearch) _autoHide.PointerEnter(now);
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
            if (pointerOverSearchPosition || pointerOverVisibleSearch)
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

        // Tính mục tiêu trượt (target offset)
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

        // Nội suy chuyển động mượt mà (Ease-out lerp)
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

        // Dừng timer khi đã trượt đến vị trí mục tiêu
        if (Math.Abs(_slideOffsetPixels - target) <= 0.5)
        {
            _autoHideFrameTimer?.Stop();
        }
    }

    /// <summary>Chuẩn bị vật liệu mờ nền (backdrop material) trước khi trượt hiện ra để chống giật khung hình.</summary>
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

    /// <summary>
    /// Áp dụng độ dời trượt (slide) cho cửa sổ:
    /// - Tính toán vị trí pixel trượt theo hướng (lên mép trên hoặc xuống mép dưới).
    /// - Cập nhật TranslateTransform cho nội dung dock.
    /// - Điều chỉnh bóng đổ (shadow) và hình dạng kính mờ theo độ dịch chuyển.
    /// </summary>
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
        _slidePanelBoundsDip = new Rect(0, visualOffsetDip, ActualWidth, ActualHeight);
        SearchDockSlideTransform.Y = visualOffsetDip;

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

    /// <summary>Tính toán vùng chữ nhật thực tế còn nhìn thấy được của dock trên màn hình.</summary>
    private PixelRect GetVisibleSearchRect()
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

    // =========================================================================================
    // TÍNH TOÁN & CĂN CHỈNH VỊ TRÍ TRÊN MÀN HÌNH (POSITION ON DISPLAY)
    // =========================================================================================

    /// <summary>Đưa tác vụ định vị vị trí dock vào hàng đợi Dispatcher để tránh spam tính toán.</summary>
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

    /// <summary>
    /// Tính toán toạ độ hiển thị chuẩn của thanh dock trên màn hình chính:
    /// - Xử lý DPI scale.
    /// - Khi IsTopPlacement (cạnh trên): đặt ở góc trên bên trái màn hình (Y = verticalArea.Y + offsetY).
    /// - Khi ở cạnh đáy: đặt ở góc dưới bên trái màn hình (Y = verticalArea.Bottom - height - bottomInset + offsetY).
    /// - Đảm bảo toạ độ nằm an toàn trong vùng làm việc của màn hình bằng Math.Clamp.
    /// - Cập nhật khoảng cách ẩn hoàn toàn (_fullyHiddenOffsetPixels).
    /// </summary>
    public void PositionOnDisplay()
    {
        var handle = Handle;
        if (handle == 0 || PrimaryDisplaySession is not { IsAvailable: true } session) return;

        var display = session.Display;
        var width = Math.Max(1, display.ToPixels(ActualWidth > 0 ? ActualWidth : DesiredSize.Width));
        var height = Math.Max(1, display.ToPixels(ActualHeight > 0 ? ActualHeight : DesiredSize.Height));
        var inset = display.ToPixels(8);
        var bottomInset = display.ToPixels(10);
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

        var requested = new PixelRect(work.X + inset, requestedY, width, height);
        var clamped = new PixelRect(
            Math.Clamp(requested.X, work.X, work.Right - width),
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

    // =========================================================================================
    // QUẢN LÝ TIẾT KIỆM HIỆU NĂNG & THAY ĐỔI MÀN HÌNH
    // =========================================================================================

    /// <summary>Bật/tắt chế độ ngủ hiệu năng (Performance Sleeping) khi máy chơi game hoặc chạy ứng dụng nặng.</summary>
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

    public void ResumeAfterPrimaryDisplayChange(bool show)
    {
        PositionOnDisplay();
        if (show && !_performanceSleeping) Show();
    }

    public void SetWindowsTaskbarHidden(bool hidden)
    {
        if (_windowsTaskbarHidden == hidden) return;
        _windowsTaskbarHidden = hidden;
        ApplyDockMetrics();
        QueuePositionOnDisplay();
    }

    // =========================================================================================
    // THIẾT LẬP THUỘC TÍNH CỬA SỔ WIN32 (NO-ACTIVATE) & DỌN DẸP
    // =========================================================================================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PrimaryDisplaySession is { IsAvailable: false })
        {
            Hide();
            return;
        }

        // Đảm bảo cửa sổ dock mang style WS_EX_NOACTIVATE để khi nhấn vào dock không cướp focus của cửa sổ đang hoạt động
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
        _autoHideFrameTimer?.Stop();
        _recentSearchFlyoutWindow?.Close();
        if (_dockSettings is not null) _dockSettings.Changed -= OnDockSettingsChanged;
        ResetHover();
        base.OnClosed(e);
    }

    // =========================================================================================
    // WIN32 INTEROP & THEO DÕI SỰ KIỆN FOCUS DESKTOP / CỬA SỔ NGOÀI
    // =========================================================================================

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, [Out] char[] className, int maxCount);

    private bool _hasActiveExternalApp;
    private DateTimeOffset _desktopFocusStartTime = DateTimeOffset.MinValue;
    private bool _desktopSlideTriggered = true;
    private bool _isForegroundHookSubscribed;
    private DispatcherTimer? _desktopDebounceTimer;
    private bool _revealMaterialPrepared;

    /// <summary>Bắt đầu kích hoạt timer trượt auto-hide.</summary>
    private void StartSlide()
    {
        if (_mainDock is not null)
        {
            _mainDock.WakeSlide();
            return;
        }
        if (_performanceSleeping || _autoHide is null || _restingRect.Width <= 0) return;
        if (SystemTrayBarWindow.IsNativeOverflowSessionActive) return;
        if (_autoHideFrameTimer is not null)
        {
            _autoHideFrameTimer.Interval = AutoHideFrameInterval;
            _autoHideFrameTimer.Start();
        }
    }

    /// <summary>
    /// Xử lý sự kiện khi cửa sổ foreground của Windows thay đổi:
    /// - Nếu chuyển sang ứng dụng bên thứ 3 và ứng dụng đó che dock -> kích hoạt ẩn dock.
    /// - Nếu người dùng bấm quay về màn hình Desktop -> kích hoạt hiện lại dock sau khoảng debounce 60ms.
    /// </summary>
    private void OnSystemForegroundChanged(nint currentFg)
    {
        if (_performanceSleeping || _autoHide is null || _restingRect.Width <= 0) return;
        if (SystemTrayBarWindow.IsNativeOverflowSessionActive)
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

    /// <summary>Kiểm tra sau debounce khi quay lại màn hình Desktop để trượt hiện dock.</summary>
    private void CheckDesktopReturnAfterDebounce()
    {
        if (_performanceSleeping || _autoHide is null || _restingRect.Width <= 0) return;
        if (SystemTrayBarWindow.IsNativeOverflowSessionActive) return;

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

    /// <summary>Đánh thức thanh dock khi người dùng bấm Desktop.</summary>
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

    /// <summary>Xác định cửa sổ hiện tại có phải là cửa sổ nền Desktop (Progman/WorkerW/SHELLDLL_DefView) hay không.</summary>
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

    /// <summary>Kiểm tra xem cửa sổ có thuộc ứng dụng bên ngoài hay không (không phải Glassquid và không phải Desktop).</summary>
    private static bool IsExternalApp(nint hwnd)
    {
        if (hwnd == 0) return false;
        if (IsOurProcessWindow(hwnd)) return false;
        if (IsDesktopWindow(hwnd)) return false;
        if (TrayIconContextMenuRelay.IsNativeTrayWindow(hwnd)) return false;
        return true;
    }
}
