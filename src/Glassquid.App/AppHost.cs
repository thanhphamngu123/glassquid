using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Glassquid.App.Startup;
using Glassquid.App.Tray;
using Glassquid.AppServices;
using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Storage;
using Glassquid.Dock;
using Glassquid.Launcher;
using Glassquid.Platform;
using Glassquid.Platform.Displays;
using Glassquid.Platform.Installation;
using Glassquid.Platform.Shell;
using Glassquid.Platform.Startup;
using Glassquid.Platform.Storage;
using Glassquid.Settings;
using Glassquid.UI.Settings;
using Glassquid.UI.Theming;
// WPF có sẵn System.Windows.ThemeMode, trùng tên với enum trong mô hình cài đặt, dùng bí danh để cố định về enum của riêng chúng ta.
using ThemeMode = Glassquid.Core.Models.ThemeMode;

namespace Glassquid.App;

/// <summary>
/// Quản lý toàn bộ vòng đời ứng dụng: đơn thể (single-instance), dữ liệu, chủ đề, khay hệ thống và các cửa sổ.
/// </summary>
/// <remarks>
/// Mọi thứ tương tác với hệ điều hành đều được tạo tại đây rồi truyền cho từng cửa sổ,
/// nhờ đó các cửa sổ không phải tự đi lấy đường dẫn, handle hay registry.
/// </remarks>
internal sealed partial class AppHost : IDisposable
{
    /// <summary>
    /// Định danh thực thể của sản phẩm này.
    /// </summary>
    /// <remarks>
    /// Khi đặt tên cố ý tránh <c>MacDock_SingleInstance_7E3A9B1F</c> của MacDock,
    /// cũng như tránh khóa Electron của Launchpad, để Glassquid
    /// có thể chạy đồng thời với bất kỳ ứng dụng nào trong số đó đã cài sẵn.
    /// </remarks>
    private const string InstanceName = "Glassquid.Host";

    /// <summary>Tên lớp cửa sổ của cửa sổ ẩn dùng để nhận thông điệp khay hệ thống.</summary>
    private const string MessageWindowName = "Glassquid.TrayMessageWindow";

    /// <summary>HWND_MESSAGE: loại cửa sổ chỉ tồn tại để nhận thông điệp.</summary>
    private static readonly nint MessageOnlyWindow = new(-3);

    private readonly Application _application;
    private readonly CommandLineOptions _options;

    private SingleInstanceGuard? _guard;
    private UserDataLease? _userDataLease;
    private GlassquidDataStore? _data;
    private AppServiceHost? _services;
    private DataLoadReport? _loadReport;
    private HwndSource? _messageWindow;
    private TrayIcon? _tray;
    private nint _trayIconHandle;
    private MissingTargetReport? _missingReport;
    private PrimaryDisplaySession? _primaryDisplaySession;
    private TaskbarVisibilityController? _taskbarVisibility;
    private string? _taskbarVisibilityStartupError;
    private IDisposable? _backdropDiagnostics;

    private DockWindow? _dock;
    private SystemTrayBarWindow? _systemTrayBar;
#pragma warning disable CS0649
    private SearchDockWindow? _searchDock;
#pragma warning restore CS0649
    private readonly List<(DockWindow Dock, SystemTrayBarWindow Tray, SearchDockWindow? Search)> _secondaryDocks = new();
    private LauncherWindow? _launcher;
    private SettingsWindow? _settings;
    private DockSettingsHost? _dockSettings;
    private bool _shuttingDown;
    private bool _disposed;
    private readonly DeferredCommandGate _deferredCommandGate = new();

    public AppHost(Application application, CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(options);

        _application = application;
        _options = options;
    }

    /// <summary>
    /// Khởi chạy sản phẩm.
    /// </summary>
    /// <returns>Kết quả của lần khởi chạy này, quyết định tiến trình chủ cần làm gì tiếp theo.</returns>
    public StartupOutcome Start()
    {
        _guard = SingleInstanceGuard.Acquire(InstanceName);

        if (!_guard.IsFirstInstance)
        {
            // UI thuộc về thực thể khác. Chuyển tiếp ý định khởi chạy lần này sang đó rồi nhường quyền.
            HandOffToRunningInstance();
            return StartupOutcome.HandedOffToRunningInstance;
        }

        // Đã là tiến trình chính duy nhất: dọn dẹp dứt điểm các tiến trình mồ côi hoặc worker bị sót lại từ các lần crash trước
        TerminateOrphanedInstances();

        // Thực thể đầu tiên của phiên bản mới cũng giữ tên mutex mà phiên bản cũ sử dụng, ngăn phiên bản cũ khởi chạy sau đó và tự coi mình là thực thể đầu tiên.
        // Lúc này chưa tải hoặc tạo dữ liệu người dùng; nếu khóa cũ đã tồn tại hoặc không thể xác nhận, giải phóng khóa mới rồi thoát an toàn.
        if (!_guard.TryAcquireLegacyProtocolLease(out var legacyStatus))
        {
            _guard.Dispose();
            _guard = null;

            MessageBox.Show(
                StartupIntent.GetLegacyInstanceBlockMessage(legacyStatus),
                "Glassquid không thể khởi động",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return StartupOutcome.LegacyInstanceBlocked;
        }

        // Là thực thể đầu tiên, nhưng lần khởi chạy này chỉ yêu cầu "Thoát".
        //
        // Ngữ nghĩa của --exit là "yêu cầu thực thể đang chạy thoát". Vì không có thực thể nào khác nên không có việc gì để làm;
        // trước đây chỗ này vẫn tiếp tục khởi chạy toàn bộ sản phẩm, khiến việc chạy --exit khi chưa có thực thể nào lại bật dock lên,
        // hoàn toàn trái ngược với yêu cầu của người dùng.
        if (StartupIntent.ShouldExitWithoutExistingInstance(_options))
        {
            return StartupOutcome.NothingToDo;
        }

        if (!UserDataLease.TryAcquireShared(AppPaths.ForCurrentUser().RootDirectory, out _userDataLease))
        {
            MessageBox.Show(
                "Không thể tạo khóa sử dụng dữ liệu người dùng Glassquid. Có thể dữ liệu đang được bảo trì hoặc thư mục hiện tại không truy cập được. Để bảo vệ dữ liệu, ứng dụng đã dừng khởi chạy. Hãy thử lại sau.",
                "Dữ liệu Glassquid hiện không khả dụng",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return StartupOutcome.UserDataLeaseUnavailable;
        }

        _guard.CommandReceived += OnCommandReceived;
        _guard.StartListening();

        if (DisplayService.Primary() is not { } startupPrimary)
        {
            MessageBox.Show("Không thể xác định màn hình chính hiện tại của Windows. Glassquid chưa tạo cửa sổ nào.",
                "Glassquid không thể khởi động", MessageBoxButton.OK, MessageBoxImage.Warning);
            return StartupOutcome.PrimaryDisplayUnavailable;
        }
        _primaryDisplaySession = new PrimaryDisplaySession(startupPrimary);

        var theme = ThemeService.Initialize(_application.Dispatcher);

        _data = new GlassquidDataStore(AppPaths.ForCurrentUser());
        _loadReport = _data.LoadAll();
        _taskbarVisibility = new TaskbarVisibilityController();
        _taskbarVisibilityStartupError = _taskbarVisibility.RecoveryError;
        if (_data.Settings.Current.DesktopTaskbar.HideWindowsTaskbar)
            _taskbarVisibilityStartupError = _taskbarVisibility.SetHidden(true);
        _backdropDiagnostics = Glassquid.UI.BackdropRecovery.BeginAutomaticDiagnostics(_data.Paths.LogDirectory);

        // Các dịch vụ ứng dụng (nhập, khởi chạy, biểu tượng, kiểm tra lại mục hỏng) được tạo một lần tại đây,
        // sau đó các cửa sổ dùng chung một thực thể. Trên giao diện không có bất kỳ chỗ nào chạm trực tiếp vào tệp, tiến trình hay handle cửa sổ,
        // quy tắc kiến trúc này được duy trì qua đầu mối này từ giai đoạn 3.
        _services = new AppServiceHost(_data);

        // Kiểm tra lại xem "mục tiêu còn tồn tại hay không". Người dùng có thể đã xóa một chương trình sau lần chạy trước,
        // hoặc rút ổ đĩa di động; không xóa mục mà chỉ đánh dấu, nên bước này mang tính lũy đẳng (idempotent).
        // Chỉ ghi tệp khi trạng thái thực sự thay đổi, do đó khởi động bình thường sẽ không làm thay đổi thư viện ứng dụng.
        _missingReport = _services.Maintenance.RefreshMissingTargets();

        // Áp dụng cài đặt giao diện đã lưu trước khi tạo bất kỳ cửa sổ nào, để lần vẽ đầu tiên đã hiển thị đúng.
        var appearance = _data.Settings.Current.Appearance;
        ApplySampleOverride(appearance, _options.Sample);
        theme.Update(appearance);

        // Đầu mối thông báo cài đặt dock cũng tương tự: lắp đặt trước khi tạo cửa sổ,
        // để khi Dock làm mới lần đầu sẽ thấy cài đặt thật chứ không phải giá trị mặc định.
        // Sau này hễ cài đặt thay đổi sẽ đẩy từ đây tới các Dock đang mở (D-16/D-17 có hiệu lực tức thì).
        _dockSettings = DockSettingsHost.Initialize(_application.Dispatcher);
        _dockSettings.Update(_data.Settings.Current.Dock);

        // Theo dõi trạng thái đang chạy cũng phải hoàn tất liệt kê ban đầu trước khi dựng ô biểu tượng:
        // chỉ như vậy "những ứng dụng đã chạy trước khi Dock khởi động" mới có chấm trạng thái ngay từ lần vẽ đầu tiên.
        _services.Running.Start();

        CreateMessageWindow();
        CreateTrayIcon();

        CreateWindows();
        StartInstalledAppsAutoDiscovery();
        InitializePerformanceCoordinator();
        InitializeInteractionController();

        theme.Changed += OnThemeChanged;

        WriteStartupLog();

        InitializeUpdates();

        return StartupOutcome.Started;
    }

    /// <summary>
    /// Ghi đè giao diện của lần chạy này theo yêu cầu mẫu.
    /// </summary>
    /// <remarks>
    /// Chỉ sửa bản sao trong bộ nhớ, **không bao giờ ghi lại vào tệp cài đặt**: việc này nhằm chụp các mẫu có thể tái lập,
    /// không phải quyết định thay người dùng. Khi cần tạo ba mẫu sáng, tối, và suy giảm,
    /// bắt buộc phải cố định đầu vào, nếu không khi chủ đề hệ thống thay đổi, hai ảnh trước và sau sẽ không thể so sánh được.
    /// </remarks>
    private static void ApplySampleOverride(AppearanceSettings appearance, AppearanceSample sample)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        switch (sample)
        {
            case AppearanceSample.Light:
                appearance.Theme = ThemeMode.Light;
                appearance.UseSystemBackdrop = true;
                appearance.ReduceTransparency = false;
                break;

            case AppearanceSample.Dark:
                appearance.Theme = ThemeMode.Dark;
                appearance.UseSystemBackdrop = true;
                appearance.ReduceTransparency = false;
                break;

            case AppearanceSample.Degraded:
                // Điểm mấu chốt nằm ở UseSystemBackdrop = false: nó khiến cửa sổ không yêu cầu chất liệu hệ thống,
                // từ đó đi theo nhánh tự vẽ màu nền "chất liệu không khả dụng" — tức giao diện trên các hệ thống cũ.
                appearance.Theme = ThemeMode.Light;
                appearance.UseSystemBackdrop = false;
                appearance.ReduceTransparency = false;
                break;

            default:
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _deferredCommandGate.Close();

        StopInstalledAppsAutoDiscovery();

        _taskbarVisibility?.Dispose();
        _taskbarVisibility = null;

        DisposeUpdates();

        if (ThemeService.Current is { } theme)
        {
            theme.Changed -= OnThemeChanged;
        }

        if (_guard is not null)
        {
            _guard.CommandReceived -= OnCommandReceived;
        }

        DisposeInteractionController();
        DisposePerformanceCoordinator();
        _backdropDiagnostics?.Dispose(); _backdropDiagnostics = null;

        _tray?.Dispose();
        _tray = null;

        _services?.Dispose();
        _services = null;

        _messageWindow?.Dispose();
        _messageWindow = null;

        _userDataLease?.Dispose();
        _userDataLease = null;

        _guard?.Dispose();
        _guard = null;

        ThemeService.Current?.Dispose();
        _disposed = true;
    }

    private string? ApplyWindowsTaskbarVisibility(bool hidden)
    {
        if (_taskbarVisibility is null) return "Bộ điều khiển taskbar chưa được khởi tạo.";

        if (!hidden)
        {
            try { _systemTrayBar?.PrepareForWindowsTaskbarRestore(); }
            catch (Exception ex) { return "Không thể đóng khay hệ thống trước khi hiện taskbar: " + ex.Message; }
        }

        var error = _taskbarVisibility.SetHidden(hidden);
        if (error is not null) return error;

        try
        {
            _dock?.SetWindowsTaskbarHidden(hidden);
            _systemTrayBar?.SetWindowsTaskbarHidden(hidden);
            _searchDock?.SetWindowsTaskbarHidden(hidden);
            foreach (var sec in _secondaryDocks)
            {
                sec.Dock.SetWindowsTaskbarHidden(hidden);
                sec.Tray.SetWindowsTaskbarHidden(hidden);
                sec.Search?.SetWindowsTaskbarHidden(hidden);
            }
            return null;
        }
        catch (Exception ex)
        {
            var rollbackError = _taskbarVisibility.SetHidden(!hidden);
            try
            {
                _dock?.SetWindowsTaskbarHidden(!hidden);
                _systemTrayBar?.SetWindowsTaskbarHidden(!hidden);
                _searchDock?.SetWindowsTaskbarHidden(!hidden);
                foreach (var sec in _secondaryDocks)
                {
                    sec.Dock.SetWindowsTaskbarHidden(!hidden);
                    sec.Tray.SetWindowsTaskbarHidden(!hidden);
                    sec.Search?.SetWindowsTaskbarHidden(!hidden);
                }
            }
            catch (Exception rollbackLayoutError)
            {
                return $"{ex.Message} Khôi phục vị trí thanh Glassquid thất bại: {rollbackLayoutError.Message}";
            }

            return rollbackError is null ? ex.Message : $"{ex.Message} Khôi phục taskbar thất bại: {rollbackError}";
        }
    }

    /// <summary>Hiển thị Launchpad trên màn hình chính đã gắn lúc khởi động, có thể bắt đầu hiệu ứng chuyển tiếp từ biểu tượng Dock.</summary>
    private void ShowLauncher() => ShowLauncher(null);

    private void ShowLauncher(PixelRect? sourceBounds)
    {
        if (_launcher is null || _primaryDisplaySession?.IsAvailable != true)
        {
            return;
        }

        RequestPerformanceActivation();
        try
        {
            _launcher.RefreshDiagnostics();
            _dock?.SetLauncherContainerState(true);
            _launcher.ShowWithTransition(sourceBounds ?? _dock?.GetLauncherButtonPixelBounds());
            NotifyPendingInstalledApps();
            _launcher.Activate();
        }
        finally
        {
            _performanceActivationPending = false;
            ReconcilePerformance();
        }
    }

    private void HideLauncher() => _launcher?.CloseWithTransition();

    private void ShowSettings()
    {
        if (_settings is null || _primaryDisplaySession?.IsAvailable != true)
        {
            return;
        }

        _settings.RefreshDiagnostics(_loadReport);
        _settings.RefreshStartupRegistration();
        _settings.RefreshInstallationStatus(() =>
            new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty));

        if (!_settings.IsVisible)
        {
            _settings.PositionOnPrimary();
            _settings.Show();
        }

        _settings.Activate();
    }

    private void ExitApplication()
    {
        // Phải cho phép đóng cửa sổ thì quá trình tắt mới hoàn tất được,
        // nếu không hàm xử lý sự kiện đóng bên dưới sẽ chặn lại.
        _deferredCommandGate.Close();
        _shuttingDown = true;

        foreach (var sec in _secondaryDocks)
        {
            try { sec.Dock.Close(); } catch { }
            try { sec.Tray.Close(); } catch { }
            try { sec.Search?.Close(); } catch { }
        }

        // Dọn dẹp dứt điểm các worker capture trước khi thoát ứng dụng
        try
        {
            var currentPid = Environment.ProcessId;
            var currentSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("Glassquid"))
            {
                try
                {
                    if (p.Id != currentPid && p.SessionId == currentSessionId)
                    {
                        p.Kill();
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        // Tắt tường minh: cửa sổ Launchpad và Cài đặt bình thường hay ở trạng thái ẩn,
        // nếu dùng chế độ OnLastWindowClose mặc định thì khi đóng chúng chương trình sẽ kết thúc luôn.
        _application.Shutdown();
    }

    /// <summary>
    /// Dọn dẹp dứt điểm các tiến trình Glassquid mồ côi (zombie processes) hoặc worker bị sót lại từ các lần crash trước.
    /// </summary>
    private static void TerminateOrphanedInstances()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            var currentSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            var processes = System.Diagnostics.Process.GetProcessesByName("Glassquid");
            foreach (var p in processes)
            {
                try
                {
                    if (p.Id != currentPid && p.SessionId == currentSessionId)
                    {
                        p.Kill();
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }
    }

    private void HandOffToRunningInstance()
    {
        // Khởi động khi đăng nhập chỉ đảm bảo thực thể thường trú tồn tại; tuyệt đối không gửi ý định "hiển thị Launchpad" sang thực thể cũ.
        var command = StartupIntent.GetDuplicateInstanceCommand(_options);
        if (command is null) return;

        // Đợi đồng bộ: tiến trình sắp thoát ngay, không còn việc gì khác để làm.
        // Thời gian chờ nhằm bao quát trường hợp thực thể đang chạy bận khởi động.
        SingleInstanceGuard.TrySendCommandAsync(InstanceName, command, TimeSpan.FromSeconds(3))
            .GetAwaiter()
            .GetResult();
    }

    private void CreateWindows()
    {
        _dock = new DockWindow();
        _dock.SetWindowsTaskbarHidden(_taskbarVisibility?.IsHidden == true);
        if (_taskbarVisibility is not null)
        {
            _taskbarVisibility.ShouldSuppressHide = () => SystemTrayBarWindow.IsNativeOverflowActive();
        }
        _systemTrayBar = new SystemTrayBarWindow(
            (_taskbarVisibility ?? throw new InvalidOperationException("Taskbar visibility controller must be initialized before creating windows.")).SuspendRefresh);
        _systemTrayBar.SetWindowsTaskbarHidden(_taskbarVisibility?.IsHidden == true);
        _systemTrayBar.AttachDockSettings(_dockSettings
            ?? throw new InvalidOperationException("Dock settings must be initialized before creating windows."));
        _dock.AttachCompanionDocks(_systemTrayBar, _searchDock);
        // Comment out thanh dock search bên trái theo yêu cầu:
        // _searchDock = new SearchDockWindow();
        // _searchDock.SetWindowsTaskbarHidden(_taskbarVisibility?.IsHidden == true);
        // _searchDock.AttachDockSettings(_dockSettings);
        _launcher = new LauncherWindow();
        var primaryDisplay = _primaryDisplaySession
            ?? throw new InvalidOperationException("Primary Display must be captured before creating Glassquid windows.");
        _dock.BindPrimaryDisplay(primaryDisplay);
        _systemTrayBar.BindPrimaryDisplay(primaryDisplay);
        // _searchDock.BindPrimaryDisplay(primaryDisplay);
        _launcher.BindPrimaryDisplay(primaryDisplay);
        _launcher.ContainerAnchorProvider = () => _dock?.GetLauncherButtonPixelBounds();
        _launcher.IsVisibleChanged += (_, e) => _dock?.SetLauncherContainerState(e.NewValue is true);
        _settings = new SettingsWindow();
        _settings.BindPrimaryDisplay(primaryDisplay);
        _settings.WindowsTaskbarVisibilityApply = ApplyWindowsTaskbarVisibility;

        _settings.AttachDataStore(_data!, _loadReport);
        if (_taskbarVisibilityStartupError is not null)
            _settings.ShowTaskbarVisibilityStatus("Không thể đồng bộ trạng thái taskbar khi khởi động: " + _taskbarVisibilityStartupError);
        _settings.AttachStartupRegistration(
            new StartupRegistration(),
            Environment.ProcessPath ?? string.Empty);
        _settings.AttachInstallation(new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty));
        _dock.AttachServices(_services!, _dockSettings!, (PixelRect? source) =>
            ToggleLauncherFromDock(_launcher, source, ShowLauncher));
        _dock.ToggleDesktopIcons = () => ExecuteInteraction(Glassquid.Core.Input.InputAction.ToggleDesktopIcons);
        _launcher.AttachServices(_services!, _loadReport?.LibraryStatus != LoadStatus.Unrecoverable);
        _launcher.InternalDragStarted += (_, _) => _dock?.RaiseAboveLauncherDuringDrag();
        _launcher.InternalDragFinished += (_, _) => _launcher?.RaiseAboveDockAfterDrag();
        _services!.LibraryChanged += (_, _) =>
        {
            if (_application.Dispatcher.CheckAccess())
            {
                RefreshLibraryForPerformance();
            }
            else
                _application.Dispatcher.BeginInvoke(RefreshLibraryForPerformance);
        };
        _settings.LauncherSettingsChanged += (_, settings) => _launcher?.ApplySettings(settings);

        _launcher.ApplySettings(_data!.Settings.Current.Launcher);

        var icon = CreateWindowIcon();
        var startupWindows = new List<Window> { _dock, _systemTrayBar, _launcher, _settings };
        if (_searchDock is not null) startupWindows.Add(_searchDock);
        foreach (Window window in startupWindows)
        {
            window.Icon = icon;

            // Một yêu cầu đóng do bấm nhầm (ví dụ nhấn Alt+F4 trên dock) không thể khiến sản phẩm bị thoát;
            // thoát ứng dụng là trách nhiệm của menu khay hệ thống.
            window.Closing += OnWindowClosing;
        }

        _dock.PrepareRevealForStartup();
        _dock.Show();
        _systemTrayBar.PrepareRevealForStartup();
        _systemTrayBar.Show();
        // _searchDock.Show();

        foreach (var display in DisplayService.Enumerate())
        {
            if (display.IsPrimary) continue;
            CreateSecondaryDock(display, icon);
        }

        if (StartupIntent.ShouldOpenLauncher(_options))
        {
            ShowLauncher();
        }

        if (StartupIntent.ShouldOpenSettings(_options))
        {
            ShowSettings();
        }
    }

    private void CreateSecondaryDock(DisplayInfo display, BitmapSource icon)
    {
        var secondarySession = new PrimaryDisplaySession(display);

        var secDock = new DockWindow(isSecondary: true);
        secDock.SetWindowsTaskbarHidden(_taskbarVisibility?.IsHidden == true);
        secDock.BindPrimaryDisplay(secondarySession);
        secDock.AttachServices(_services!, _dockSettings!, (PixelRect? source) =>
            ToggleLauncherFromDock(_launcher, source, ShowLauncher));
        secDock.ToggleDesktopIcons = () => ExecuteInteraction(Glassquid.Core.Input.InputAction.ToggleDesktopIcons);

        var secTray = new SystemTrayBarWindow(
            _taskbarVisibility != null ? _taskbarVisibility.SuspendRefresh : null,
            isSecondary: true);
        secTray.SetWindowsTaskbarHidden(_taskbarVisibility?.IsHidden == true);
        secTray.AttachDockSettings(_dockSettings!);
        secTray.BindPrimaryDisplay(secondarySession);
        secDock.AttachCompanionDocks(secTray, null);

        // Comment out thanh dock search màn hình phụ theo yêu cầu:
        // var secSearch = new SearchDockWindow();
        // secSearch.SetWindowsTaskbarHidden(_taskbarVisibility?.IsHidden == true);
        // secSearch.AttachDockSettings(_dockSettings!);
        // secSearch.BindPrimaryDisplay(secondarySession);

        secDock.Icon = icon;
        secTray.Icon = icon;
        // secSearch.Icon = icon;

        secDock.Closing += OnWindowClosing;
        secTray.Closing += OnWindowClosing;
        // secSearch.Closing += OnWindowClosing;

        _secondaryDocks.Add((secDock, secTray, null));

        secDock.Show();
        secTray.Show();
        // secSearch.Show();
    }

    private static BitmapSource CreateWindowIcon()
    {
        return TrayIconArtwork.RenderBitmap(32);
    }

    private void CreateMessageWindow()
    {
        var parameters = new HwndSourceParameters(MessageWindowName)
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ParentWindow = MessageOnlyWindow,
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(OnMessageWindowProc);
    }

    private void CreateTrayIcon()
    {
        if (_messageWindow is null)
        {
            return;
        }

        _trayIconHandle = TrayIconArtwork.CreateHandle(32);

        _tray = new TrayIcon(_messageWindow.Handle);
        _tray.Show(_trayIconHandle, "Glassquid");
    }

    private nint OnMessageWindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312)
        {
            if (_settings?.TryCaptureRegisteredHotkey(lParam) != true)
            {
                var key = (uint)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (_performance?.IsSuppressionRequested == true && _launcher?.IsVisible != true && key is 5 or 6 or 0x100 or 0x101)
                { handled = true; return 0; }
                _interaction?.HandleHotkey(wParam.ToInt32());
            }
            handled = true;
            return 0;
        }
        if (msg != TrayIcon.CallbackMessage)
        {
            return 0;
        }

        switch (TrayIcon.Classify((int)lParam))
        {
            case TrayEvent.Activate:
            case TrayEvent.DoubleClick:
                ShowLauncher();
                handled = true;
                break;

            case TrayEvent.ContextMenu:
                ShowTrayMenu(hwnd);
                handled = true;
                break;

            default:
                break;
        }

        return 0;
    }

    private void ShowTrayMenu(nint hwnd)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            PlacementTarget = _dock,
            StaysOpen = false,
        };

        menu.Items.Add(CreateMenuItem("Mở Launchpad", ShowLauncher));
        menu.Items.Add(CreateMenuItem("Ẩn Launchpad", HideLauncher));
        var gameSleep = new MenuItem { Header = "Tạm dừng khi chơi game", IsCheckable = true, IsChecked = RuntimeManualGameSleep };
        gameSleep.Click += (_, _) => SetManualGameSleep(gameSleep.IsChecked);
        menu.Items.Add(gameSleep);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Cài đặt…", ShowSettings));
        menu.Items.Add(CreateMenuItem("Mở thư mục dữ liệu", OpenDataFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Thoát", ExitApplication));

        // Menu khay hệ thống chỉ tự thu lại khi nhấp ra ngoài nếu cửa sổ sở hữu nó đã được đưa lên tiền cảnh trước;
        // shell sẽ không chủ động kích hoạt cửa sổ chỉ nhận thông điệp đó của chúng ta.
        WindowActivation.TryBringToForeground(hwnd);

        menu.IsOpen = true;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenDataFolder()
    {
        if (_data is null)
        {
            return;
        }

        try
        {
            _data.Paths.EnsureCreated();

            // Khởi chạy Explorer qua shell là cách chính thức để mở thư mục,
            // đồng thời tránh phải phụ thuộc vào một hàm trợ giúp tạo tiến trình nào đó.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _data.Paths.RootDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Mở thư mục chỉ là một tính năng tiện ích, nếu không làm được cũng không được làm gián đoạn chính ứng dụng.
            AppendToLog($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Không thể mở thư mục dữ liệu: {ex.Message}");
        }
    }

    private void ExportBackdropDiagnostics()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Xuất báo cáo chẩn đoán nền (không bao gồm ảnh màn hình)", Filter = "Báo cáo JSON|*.json",
            FileName = "Glassquid-background-diagnostics.json", AddExtension = true, DefaultExt = ".json"
        };
        if (dialog.ShowDialog() != true) return;
        try { Glassquid.UI.BackdropRecovery.ExportDiagnosticReport(dialog.FileName); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ReportInteractionError("Không thể xuất báo cáo chẩn đoán nền: " + error.Message); }
    }

    private void OnCommandReceived(object? sender, string command)
    {
        if (_deferredCommandGate.IsClosed || _shuttingDown || _application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // Callback từ pipe đến trên luồng nền; còn mọi thao tác cửa sổ đều phải chạy trên
        // luồng điều phối (Dispatcher).
        _application.Dispatcher.BeginInvoke(() => _deferredCommandGate.TryRun(() =>
            {
                if (_shuttingDown || _application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                switch (command)
                {
                    case InstanceCommands.ShowLauncher:
                        ShowLauncher();
                        break;

                    case InstanceCommands.HideLauncher:
                        HideLauncher();
                        break;

                    case InstanceCommands.ShowSettings:
                        ShowSettings();
                        break;

                    case InstanceCommands.Exit:
                        ExitApplication();
                        break;

                    default:
                        break;
                }
            }));
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // Biểu tượng này được vẽ riêng theo nền sáng hoặc nền tối, nên bắt buộc phải kết xuất lại.
        if (_tray is null)
        {
            return;
        }

        _tray.Update(TrayIconArtwork.CreateHandle(32), "Glassquid");

        var icon = CreateWindowIcon();
        var allWindows = new List<Window> { _dock!, _systemTrayBar!, _launcher!, _settings! };
        if (_searchDock is not null) allWindows.Add(_searchDock);
        foreach (Window window in allWindows)
        {
            window.Icon = icon;
        }
        foreach (var sec in _secondaryDocks)
        {
            sec.Dock.Icon = icon;
            sec.Tray.Icon = icon;
            if (sec.Search is not null) sec.Search.Icon = icon;
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shuttingDown)
        {
            // Đây là một lần thoát thực sự: cho phép đóng cửa sổ.
            return;
        }

        // Tuyệt đối không để một yêu cầu đóng do bấm nhầm — ví dụ nhấn Alt+F4 trên dock — kết thúc cả phiên làm việc.
        // Thoát ứng dụng là trách nhiệm của menu khay hệ thống.
        e.Cancel = true;

        if (sender is Window window)
        {
            if (window is LauncherWindow launcher)
                launcher.CloseWithTransition();
            else
                window.Hide();
        }
    }

    /// <summary>
    /// Ghi lại kết quả khởi động, bao gồm mọi thông báo tải dữ liệu, đặt ngay cạnh dữ liệu.
    /// </summary>
    private void WriteStartupLog()
    {
        if (_data is null)
        {
            return;
        }

        var lines = new List<string>
        {
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Glassquid đã khởi chạy",
            $"  Phiên bản: {typeof(AppHost).Assembly.GetName().Version}",
            $"  Thư mục dữ liệu: {_data.Paths.RootDirectory}",
            $"  {WindowsCapabilities.Describe()}",
            $"  {ThemeService.Current?.Describe()}",
            $"  Số mục trong thư viện ứng dụng: {_data.Library.Items.Count}",
        };

        if (_missingReport is not null)
        {
            lines.Add(
                $"  Mục tiêu không còn khả dụng: {_missingReport.Missing} mục (đã kiểm tra {_missingReport.Checked} mục, "
                + $"phát hiện mới {_missingReport.NewlyBroken.Count} mục, đã khôi phục {_missingReport.Recovered.Count} mục)");
        }

        var withArguments = _data.Library.Items.Count(entry => entry is not null && !string.IsNullOrEmpty(entry.Arguments));
        lines.Add($"   Mục có tham số khởi chạy: {withArguments} mục");
        lines.Add($"   Biểu tượng trong bộ nhớ đệm: {_services?.Icons.FileCount ?? 0} mục");

        if (_loadReport?.HasNotices == true)
        {
            lines.Add("  Thông báo tải dữ liệu:");
            lines.AddRange(_loadReport.Notices.Select(notice => $"    · {notice}"));
        }

        if (_loadReport?.PreservedFiles.Count > 0)
        {
            lines.Add("  Tệp lỗi đã được giữ lại:");
            lines.AddRange(_loadReport.PreservedFiles.Select(path => $"    · {path}"));
        }

        AppendToLog(lines);
    }

    /// <summary>Ghi thêm các dòng vào nhật ký khởi động, tuyệt đối không để việc ghi nhật ký làm sập ứng dụng.</summary>
    private void AppendToLog(params IEnumerable<string> lines)
    {
        if (_data is null)
        {
            return;
        }

        try
        {
            _data.Paths.EnsureCreated();
            File.AppendAllLines(Path.Combine(_data.Paths.LogDirectory, "startup.log"), lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Việc ghi nhật ký tuyệt đối không được cản trở quá trình khởi động.
        }
    }
}
