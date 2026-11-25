using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Storage;
using Glassquid.Platform;
using Glassquid.Platform.Displays;
using Glassquid.Platform.Startup;
using Glassquid.UI.Controls;
using Glassquid.UI.Settings;
using Glassquid.UI.Theming;

namespace Glassquid.Settings;

/// <summary>
/// Trung tâm cài đặt.
/// </summary>
/// <remarks>
/// Giai đoạn 1 trình bày cấu trúc trang và báo cáo môi trường theo thời gian thực. Báo cáo này không phải
/// để trang trí: trên một máy cụ thể, đây là cách nhanh nhất để xác nhận chủ đề nào được phân giải,
/// chất liệu hệ thống có được chấp nhận hay không, dữ liệu được ghi ở đâu — và tất cả những điều này
/// đều phải được ghi nhận ở giai đoạn 2.
/// </remarks>
public partial class SettingsWindow : GlassWindow
{

    protected override bool UseOpaquePanel => true;

    public static readonly DependencyProperty PlannedPagesProperty = DependencyProperty.Register(
        nameof(PlannedPages),
        typeof(IReadOnlyList<string>),
        typeof(SettingsWindow),
        new PropertyMetadata(Array.Empty<string>()));

    private GlassquidDataStore? _dataStore;
    private StartupRegistration? _startupRegistration;
    private string _startupExecutablePath = string.Empty;
    private bool _loadingStartupRegistration;
    private bool _loadingDockControls;
    private bool _loadingLauncherControls;
    private bool _loadingAppearanceControls;
    private bool _dockValuesOutOfRange;
    private bool _launcherValuesOutOfRange;
    private readonly FrameworkElement[] _sectionHeadings;
    private static readonly string[] PageKeys = ["Giao diện", "Dock", "Launchpad", "Thao tác nhanh", "Hiệu suất", "Màn hình nền", "Sao lưu và khôi phục", "Chuyển khoản", "Giới thiệu"];
    private bool _syncingCategoryFromScroll;
    private bool _sectionNavigationPending;
    private int _sectionNavigationVersion;

    public event EventHandler<LauncherSettings>? LauncherSettingsChanged;

    public SettingsWindow()
    {
        _loadingAppearanceControls = true;
        try
        {
            InitializeComponent();
            AutostartCheck.IsEnabled = false;
            Title = "Glassquid - Cài đặt";
        }
        finally
        {
            _loadingAppearanceControls = false;
        }

        _sectionHeadings = [AppearanceSectionHeading, DockSectionHeading, LauncherSectionHeading,
            ShortcutsSectionHeading, PerformanceSectionHeading, DesktopSectionHeading, BackupSectionHeading,
            TransferQrSectionHeading, AboutSectionHeading];
        PlannedPages =
        [
            "Giao diện",
            "Dock",
            "Launchpad",
            "Thao tác nhanh",
            "Hiệu suất",
            "Màn hình nền",
            "Sao lưu và khôi phục",
            "Chuyển khoản",
            "Giới thiệu",
        ];
        ResetPageButton.IsEnabled = false;
        LoadTransferQr();
    }

    private void OnSettingsWindowLoaded(object sender, RoutedEventArgs e) => SyncCategoryFromScroll();

    private bool CanResetSelectedPage => SettingsPageList.SelectedIndex is >= 0 and < 6;


    private void UpdateResetPageButton() => ResetPageButton.IsEnabled = SettingsPageList.IsEnabled
        && _dataStore is not null && CanResetSelectedPage;

    /// <summary>Tên trang cố định cho trung tâm cài đặt theo kế hoạch.</summary>
    public IReadOnlyList<string> PlannedPages
    {
        get => (IReadOnlyList<string>)GetValue(PlannedPagesProperty);
        private set => SetValue(PlannedPagesProperty, value);
    }

    /// <summary>Trung tâm cài đặt dùng chung bán kính bo góc của Launchpad để đồng bộ với kích thước bảng của nó.</summary>
    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(80, appearance.LauncherCornerRadius);
    }

    /// <summary>Truyền kho lưu trữ dữ liệu cho cửa sổ để nó có thể báo cáo vị trí lưu dữ liệu.</summary>
    public void AttachDataStore(GlassquidDataStore dataStore, DataLoadReport? loadReport)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        _dataStore = dataStore;
        LoadDockControls();
        LoadLauncherControls();
        LoadAppearanceControls();
        LoadInteractionControls();
        LoadPerformanceControls();
        LoadWindowsTaskbarVisibility();
        LoadDesktopStatus();
        RefreshDiagnostics(loadReport);
    }

    /// <summary>Kết nối với mục đăng nhập thực tế của người dùng hiện tại; giao diện chỉ hiển thị trạng thái đăng ký tương ứng với đường dẫn tiến trình hiện tại.</summary>
    public void AttachStartupRegistration(StartupRegistration registration, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(executablePath);

        _startupRegistration = registration;
        _startupExecutablePath = executablePath;
        AutostartExecutableText.Text = "Ứng dụng hiện tại: " + executablePath;
        RefreshStartupRegistration();
    }

    /// <summary>Đọc lại từ registry, không sử dụng giá trị đệm trong tệp cài đặt.</summary>
    public void RefreshStartupRegistration()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshStartupRegistration);
            return;
        }

        _loadingStartupRegistration = true;
        try
        {
            if (_startupRegistration is null)
            {
                AutostartCheck.IsChecked = false;
                AutostartCheck.IsEnabled = false;
                AutostartStatus.Text = "Chưa thể xác định trạng thái khởi chạy cùng Windows của ứng dụng.";
                return;
            }

            var status = _startupRegistration.Query(_startupExecutablePath);
            AutostartCheck.IsChecked = status.State == StartupRegistrationState.Owned;
            AutostartCheck.IsEnabled = status.State is StartupRegistrationState.Missing or StartupRegistrationState.Owned;
            AutostartStatus.Text = status.State switch
            {
                StartupRegistrationState.Missing => "Chưa đăng ký; mặc định đang tắt.",
                StartupRegistrationState.Owned => "Đã bật khởi chạy khi người dùng hiện tại đăng nhập. Có thể tắt bằng công tắc này.",
                StartupRegistrationState.OtherTarget => "Mục khởi chạy cùng tên đang trỏ đến vị trí khác. Để bảo vệ bản cài đó, Glassquid sẽ không ghi đè hoặc xóa mục này.",
                _ => "Không thể đọc hoặc xác minh mục khởi chạy cùng Windows của người dùng hiện tại: " + (status.Error ?? "Không rõ nguyên nhân."),
            };
        }
        finally
        {
            _loadingStartupRegistration = false;
        }
    }

    public void PositionOnPrimary() => CenterOnPrimaryWorkArea();

    private void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        if (_loadingStartupRegistration || _startupRegistration is null) return;

        var enabled = AutostartCheck.IsChecked == true;
        var result = _startupRegistration.SetEnabled(_startupExecutablePath, enabled);
        RefreshStartupRegistration();
        if (!result.Succeeded)
        {
            AutostartStatus.Text = "Không thể thay đổi mục khởi chạy cùng Windows: " + (result.Error ?? "Lỗi không xác định.")
                + Environment.NewLine + "Trạng thái hiện tại: " + AutostartStatus.Text;
        }
    }

    private void LoadDockControls()
    {
        if (_dataStore is null) return;
        _loadingDockControls = true;
        try
        {
            var current = _dataStore.Settings.Current;
            var dock = current.Dock;
            DockDisplayModeCombo.SelectedIndex = dock.AutoHide ? 1 : 0;
            ShowLauncherEntryCheckBox.IsChecked = dock.ShowLauncherEntry;
            ShowThisPcEntryCheckBox.IsChecked = dock.ShowThisPcEntry;
            ShowDesktopIconsEntryCheckBox.IsChecked = dock.ShowDesktopIconsEntry;
            DockScaleSlider.Value = dock.SizeScale;
            DockScaleReadout.Text = $"{dock.SizeScale:P0} · biểu tượng {DockSettings.FixedIconSize * dock.SizeScale:0} DIP";
            DockPlacementCombo.SelectedIndex = (int)dock.Placement;
            SystemTrayPlacementCombo.SelectedIndex = dock.SystemTrayOnLeft ? 1 : 0;
            var display = PrimaryDisplaySession is { IsAvailable: true } session
                ? session.Display
                : PrimaryDisplaySession is null ? DisplayService.Primary() : null;
            var availableWidthDip = display is null
                ? DockSettings.MaximumFrameLengthDip
                : display.ToDips((int)display.WorkArea.Width) - 32;
            var maximum = Math.Min(DockSettings.MaximumFrameLengthDip, Math.Max(64, availableWidthDip));
            var entryCount = (dock.ShowLauncherEntry ? 1 : 0) + (dock.ShowThisPcEntry ? 1 : 0)
                + (dock.ShowDesktopIconsEntry ? 1 : 0);
            var minimum = Math.Min(Math.Max(DockSettings.MinimumFrameLengthDip,
                22 + (DockSettings.FixedIconSize + DockSettings.FixedIconSpacing) * entryCount) * dock.SizeScale, maximum);
            DockFrameLengthSlider.Minimum = minimum;
            DockFrameLengthSlider.Maximum = maximum;
            var frameLengthValid = dock.FrameLengthDip == 0
                || Within(dock.FrameLengthDip, 64, DockSettings.MaximumFrameLengthDip);
            DockFrameLengthSlider.Value = dock.FrameLengthDip > 0
                ? Math.Clamp(dock.FrameLengthDip, minimum, maximum)
                : minimum;
            DockFrameLengthReadout.Text = dock.FrameLengthDip > 0
                ? $"Khung Dock cố định: {DockFrameLengthSlider.Value:0} DIP"
                : "Chiều dài khung Dock hiện tự điều chỉnh theo nội dung; kéo thanh trượt để đặt chiều dài cố định.";
            _dockValuesOutOfRange = !Enum.IsDefined(dock.Placement)
                || !Within(dock.OffsetX, -200, 200) || !Within(dock.OffsetY, -200, 200)
                || !frameLengthValid;
            DockOffsetXSlider.Value = Coerce(dock.OffsetX, -200, 200);
            DockOffsetYSlider.Value = Coerce(dock.OffsetY, -200, 200);
            DockSettingsStatus.Text = _dockValuesOutOfRange
                ? "Tệp cấu hình có giá trị vượt ngoài phạm vi hỗ trợ. Giá trị gần nhất được hiển thị; hãy khôi phục mặc định của trang này trước để tránh ghi đè dữ liệu gốc."
                : "Kích thước tổng thể điều chỉnh biểu tượng và chiều cao Dock; có thể chỉnh riêng chiều dài khung. Thay đổi được lưu và áp dụng ngay.";
        }
        finally { _loadingDockControls = false; }
    }

    private static bool Within(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
    private static double Coerce(double value, double minimum, double maximum) => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;

    private void LoadLauncherControls()
    {
        if (_dataStore is null) return;
        _loadingLauncherControls = true;
        try
        {
            var settings = _dataStore.Settings.Current.Launcher;
            _launcherValuesOutOfRange = settings.Rows is < 2 or > 10 || settings.Columns is < 2 or > 12;
            LauncherRowsSlider.Value = Math.Clamp(settings.Rows, 2, 10);
            LauncherColumnsSlider.Value = Math.Clamp(settings.Columns, 2, 12);
            LauncherFocusSearchCheck.IsChecked = settings.FocusSearchOnOpen;
            LauncherRememberPageCheck.IsChecked = settings.RememberLastPage;
            LauncherSettingsStatus.Text = _launcherValuesOutOfRange
                ? "Tệp cấu hình có số hàng hoặc cột vượt ngoài phạm vi hỗ trợ. Giá trị gần nhất được hiển thị; hãy khôi phục mặc định của trang này trước để tránh ghi đè dữ liệu gốc."
                : "Thay đổi được lưu và áp dụng ngay cho Launchpad.";
        }
        finally { _loadingLauncherControls = false; }
    }

    private void OnLauncherControlChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherControls || _dataStore is null) return;
        if (_launcherValuesOutOfRange) { LoadLauncherControls(); return; }
        var current = _dataStore.Settings.Current;
        var old = current.Launcher;
        var next = new LauncherSettings
        {
            Rows = (int)LauncherRowsSlider.Value,
            Columns = (int)LauncherColumnsSlider.Value,
            WindowWorkAreaRatio = old.WindowWorkAreaRatio,
            FullscreenMode = false,
            FocusSearchOnOpen = LauncherFocusSearchCheck.IsChecked == true,
            RememberLastPage = LauncherRememberPageCheck.IsChecked == true,
        };
        var candidate = SettingsPageEditor.Clone(current);
        candidate.Launcher = next;
        var validationError = SettingsPageEditor.Validate(candidate);
        if (validationError is not null)
        {
            LoadLauncherControls();
            LauncherSettingsStatus.Text = "Thiết lập không hợp lệ, chưa được lưu: " + validationError;
            return;
        }
        if (old.Rows == next.Rows && old.Columns == next.Columns && old.FullscreenMode == next.FullscreenMode
            && old.FocusSearchOnOpen == next.FocusSearchOnOpen && old.RememberLastPage == next.RememberLastPage) return;
        try
        {
            current.Launcher = next;
            _dataStore.Settings.Save();
            LauncherSettingsChanged?.Invoke(this, next);
            LauncherSettingsStatus.Text = $"Đã lưu: {next.Rows} hàng × {next.Columns} cột.";
        }
        catch (Exception ex)
        {
            current.Launcher = old;
            LoadLauncherControls();
            LauncherSettingsStatus.Text = "Lưu thất bại; đã khôi phục thiết lập trước đó: " + ex.Message;
        }
    }

    private void OnSettingsPageSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateResetPageButton();
        if (_syncingCategoryFromScroll || SettingsPageList.SelectedIndex < 0) return;
        if (SettingsPageList.SelectedIndex == 5) LoadWindowsTaskbarVisibility();
        ScrollToSection(_sectionHeadings[SettingsPageList.SelectedIndex]);
    }

    private void OnSettingsScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (!IsLoaded || _sectionNavigationPending || !ReferenceEquals(e.OriginalSource, SettingsScroll)) return;
        if (e.VerticalChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            SyncCategoryFromScroll();
    }

    private void SyncCategoryFromScroll()
    {
        var index = 0;
        for (var i = 1; i < _sectionHeadings.Length; i++)
        {
            if (_sectionHeadings[i].TranslatePoint(new Point(), SettingsScroll).Y > 13) break;
            index = i;
        }
        if (SettingsScroll.ScrollableHeight > 0
            && SettingsScroll.VerticalOffset >= SettingsScroll.ScrollableHeight - .5)
            index = _sectionHeadings.Length - 1;
        _syncingCategoryFromScroll = true;
        try { SettingsPageList.SelectedIndex = index; }
        finally { _syncingCategoryFromScroll = false; }
        UpdateResetPageButton();
    }

    private void ScrollToSection(FrameworkElement heading)
    {
        var version = ++_sectionNavigationVersion;
        _sectionNavigationPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (version != _sectionNavigationVersion) return;
            var relative = heading.TranslatePoint(new Point(0, 0), SettingsScroll);
            SettingsScroll.ScrollToVerticalOffset(SettingsScroll.VerticalOffset + relative.Y - 12);
            // ScrollViewer processes the queued offset during layout. Keep that event from
            // changing an explicitly selected category when its heading is near the bottom.
            Dispatcher.BeginInvoke(() =>
            {
                if (version == _sectionNavigationVersion) _sectionNavigationPending = false;
            }, System.Windows.Threading.DispatcherPriority.Background);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnDockControlChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingDockControls || _dataStore is null || DockPlacementCombo.SelectedIndex < 0
            || DockDisplayModeCombo.SelectedIndex < 0 || SystemTrayPlacementCombo.SelectedIndex < 0) return;
        if (_dockValuesOutOfRange) { LoadDockControls(); return; }

        var current = _dataStore.Settings.Current;
        var previousDock = current.Dock;
        var previousCorner = current.Appearance.DockCornerRadius;
        var next = new DockSettings
        {
            SizeScale = DockScaleSlider.Value,
            ShowLauncherEntry = ShowLauncherEntryCheckBox.IsChecked == true,
            ShowThisPcEntry = ShowThisPcEntryCheckBox.IsChecked == true,
            ShowDesktopIconsEntry = ShowDesktopIconsEntryCheckBox.IsChecked == true,
            IconSize = DockSettings.FixedIconSize,
            IconSpacing = DockSettings.FixedIconSpacing,
            MagnificationPeak = DockSettings.FixedMagnificationPeak,
            FrameLengthDip = ReferenceEquals(sender, DockFrameLengthSlider)
                ? DockFrameLengthSlider.Value
                : previousDock.FrameLengthDip,
            Placement = (DockPlacement)DockPlacementCombo.SelectedIndex,
            SystemTrayOnLeft = SystemTrayPlacementCombo.SelectedIndex == 1,
            OffsetX = DockOffsetXSlider.Value,
            OffsetY = DockOffsetYSlider.Value,
            AutoHide = DockDisplayModeCombo.SelectedIndex == 1,
            AutoHideLeaveDelayMs = previousDock.AutoHideLeaveDelayMs,
            EdgeRevealEnabled = false,
            EdgeHotzoneSize = previousDock.EdgeHotzoneSize,
            BlockRevealWhenCovered = previousDock.BlockRevealWhenCovered,
            TargetDisplayId = previousDock.TargetDisplayId,
            ShowRunningIndicator = previousDock.ShowRunningIndicator,
        };

        var candidate = SettingsPageEditor.Clone(current);
        candidate.Dock = next;
        candidate.Appearance.DockCornerRadius = 40;
        var validationError = SettingsPageEditor.Validate(candidate);
        if (validationError is not null)
        {
            LoadDockControls();
            DockSettingsStatus.Text = "Thiết lập không hợp lệ, chưa được lưu: " + validationError;
            return;
        }

        try
        {
            current.Dock = next;
            current.Appearance.DockCornerRadius = 40;
            _dataStore.Settings.Save();
            DockSettingsHost.Current?.Update(next);
            if (Math.Abs(previousCorner - current.Appearance.DockCornerRadius) > 0.001)
                ThemeService.Current?.Update(current.Appearance);
            DockFrameLengthReadout.Text = next.FrameLengthDip > 0
                ? $"Khung Dock cố định: {DockFrameLengthSlider.Value:0} DIP"
                : "Chiều dài khung Dock hiện tự điều chỉnh theo nội dung; kéo thanh trượt để đặt chiều dài cố định.";
            LoadDockControls();
            DockSettingsStatus.Text = $"Đã lưu; kích thước tổng thể {next.SizeScale:P0}, biểu tượng {DockSettings.FixedIconSize * next.SizeScale:0} DIP.";
        }
        catch (Exception ex)
        {
            current.Dock = previousDock;
            current.Appearance.DockCornerRadius = previousCorner;
            LoadDockControls();
            DockSettingsStatus.Text = "Lưu thất bại; đã khôi phục thiết lập trước đó: " + ex.Message;
        }
    }

    private void OnDockAutoLengthClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null || _dockValuesOutOfRange) return;
        var snapshot = SettingsPageEditor.Clone(_dataStore.Settings.Current);
        _dataStore.Settings.Current.Dock.FrameLengthDip = 0;
        SavePageSnapshot(snapshot, "Dock", DockSettingsStatus);
    }

    /// <summary>Tạo lại báo cáo môi trường.</summary>
    public void RefreshDiagnostics(DataLoadReport? loadReport = null)
    {
        var theme = ThemeService.Current;

        // Dùng culture cố định xuyên suốt: đây là bản xuất chẩn đoán gồm số phiên bản,
        // bộ đếm và đường dẫn, cần hiển thị nhất quán bất kể người dùng đang dùng thiết lập vùng nào.
        var culture = CultureInfo.InvariantCulture;
        var builder = new StringBuilder();

        builder.AppendLine(culture, $"Windows: {WindowsCapabilities.OsVersion} (Build {WindowsCapabilities.BuildNumber})");
        builder.AppendLine(culture, $"Chất liệu nền hệ thống: {(WindowsCapabilities.SupportsSystemBackdrop ? "Được hỗ trợ (Build 22621 trở lên)" : "Không được hỗ trợ; đang dùng bảng nền tự vẽ")}");
        builder.AppendLine(culture, $"Bo góc cửa sổ: {(WindowsCapabilities.SupportsWindowCornerPreference ? "Do DWM xử lý" : "Không được hỗ trợ; đang dùng bo góc tự vẽ")}");

        if (theme is not null)
        {
            builder.AppendLine(culture, $"Chủ đề: {theme.Appearance.Theme} (hiện tại {(theme.IsDark ? "Tối" : "Sáng")})");
            builder.AppendLine(culture, $"Bảng nền: {(theme.UseOpaquePanels ? "Đục" : "Kính bán trong suốt")}");
            builder.AppendLine(culture, $"Hoạt ảnh: {(theme.AnimationsEnabled ? "Bật" : "Đã giảm")}");
        }

        builder.AppendLine(culture, $"Hiệu ứng kính cửa sổ: {(SurfaceMode == GlassSurfaceMode.Acrylic ? "Làm mờ nền acrylic" : "Bảng nền bán trong suốt tự vẽ (không làm mờ nền)")}");

        if (_dataStore is not null)
        {
            builder.AppendLine(culture, $"Thư mục dữ liệu: {_dataStore.Paths.RootDirectory}");
            builder.AppendLine(culture, $"Số mục trong thư viện ứng dụng: {_dataStore.Library.Items.Count}");

            if (loadReport?.HasNotices == true)
            {
                builder.AppendLine();
                builder.AppendLine("Thông báo khi tải dữ liệu:");
                foreach (var notice in loadReport.Notices)
                {
                    builder.AppendLine(culture, $"· {notice}");
                }
            }
        }

        DiagnosticsText.Text = builder.ToString().TrimEnd();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RefreshDiagnostics();
        RefreshStartupRegistration();
    }

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        // Tiêu đề và khoảng trống xung quanh dùng chung vùng kéo, các điều khiển nội dung chính giữ nguyên thao tác chuột riêng.
        if (e.ChangedButton == MouseButton.Left && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            e.Handled = true;
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
