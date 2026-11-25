using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;
using Glassquid.AppServices;
using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Storage;
using Glassquid.Platform;
using Glassquid.Platform.Displays;
using Glassquid.Platform.Shell;
using Glassquid.UI.Controls;
using Glassquid.UI.Settings;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>
/// Cửa sổ thanh Dock.
/// </summary>
/// <remarks>
/// Cấu trúc: cửa sổ chỉ làm ba việc — đặt vị trí (<see cref="PositionOnDisplay"/>, pixel vật lý trên màn hình chính),
/// tự động ẩn (máy trạng thái <see cref="AutoHideController"/> + trượt nội dung bên trong cửa sổ màn hình chính) và ủy thác dịch vụ cho
/// <see cref="DockTileStrip"/> (ô biểu tượng, phóng to, cuộn, kiểm tra trúng, nhấp chuột).
/// Trong <see cref="OnSourceInitialized"/>, cửa sổ tự gắn cờ <c>WS_EX_NOACTIVATE</c>:
/// nhấp vào Dock sẽ không đẩy ứng dụng người dùng đang sử dụng xuống nền. Đánh đổi là các thành phần bật lên từ Dock
/// (menu ngữ cảnh, bảng thư mục, danh sách chọn cửa sổ) cũng không tự nhận tiêu điểm khi nhấp; khi cần tương tác bàn phím
/// phải gọi tường minh <see cref="WindowActivation.TryBringToForeground"/>.
/// </remarks>
public partial class DockWindow : GlassWindow
{
    private const string RuntimeTilePrefix = "runtime:";
    private static readonly HashSet<string> WindowsHelperExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost.exe",
        "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe",
        "SearchHost.exe",
        "SearchApp.exe",
        "RuntimeBroker.exe",
        "LockApp.exe",
        "Widgets.exe",
        "WidgetService.exe",
    };
    /// <summary>Thời lượng hiển thị thông báo lỗi khởi chạy trên dòng trạng thái. Sau đó khôi phục về văn bản chẩn đoán thông thường.</summary>
    private static readonly TimeSpan FailureNoticeDuration = TimeSpan.FromSeconds(5);

    /// <summary>Khoảng thời gian khung hình dùng chung cho tự động ẩn và hiệu ứng trượt.</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private AppServiceHost? _services;
    private DockSettingsHost? _dockSettings;
    private Action<PixelRect?>? _toggleLauncher;
    /// <summary>Host cung cấp hành động giống phím tắt biểu tượng màn hình nền, không phụ thuộc vào việc phím tắt có được bật hay không.</summary>
    public Action? ToggleDesktopIcons { get; set; }
    private System.Windows.Threading.DispatcherTimer? _failureNoticeTimer;
    private readonly DockTileStrip _tileStrip;

    private readonly List<DockTileModel> _models = [];
    private readonly Dictionary<string, IReadOnlyList<WindowIdentityInfo>> _runtimeTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppEntry> _runtimeEntries = new(StringComparer.Ordinal);

    private readonly System.Windows.Threading.DispatcherTimer _previewHoverTimer;
    private readonly System.Windows.Threading.DispatcherTimer _previewDismissTimer;
    private string? _hoveredEntryId;
    private Rect _hoveredTileBounds;
    private DwmPreviewWindow? _activeDwmPreview;
    private AltTabController? _altTabController;

    // ---- Tự động ẩn (D-19) ----

    private AutoHideController? _autoHide;
    private System.Windows.Threading.DispatcherTimer? _frameTimer;
    private SystemTrayBarWindow? _companionTray;
    private SearchDockWindow? _companionSearch;

    public void AttachCompanionDocks(SystemTrayBarWindow? tray, SearchDockWindow? search = null)
    {
        _companionTray = tray;
        _companionSearch = search;
        tray?.AttachMainDock(this);
        search?.AttachMainDock(this);
    }

    public bool IsPointerOverDock((int X, int Y) cursor)
        => Inflate(GetVisibleDockRect(), 2).Contains(cursor.X, cursor.Y);

    public bool IsPointerOverDockPosition((int X, int Y) cursor)
        => Inflate(_restingRect, 2).Contains(cursor.X, cursor.Y);

    public bool HasActivePopup()
        => _launcherContainerActive || _tileStrip.IsDragging || (_autoHide?.Suppressed == true);

    public void WakeSlide()
    {
        if (_performanceSleepRequested || _autoHide is null || _services is null || _restingRect.Width == 0) return;
        if (SystemTrayBarWindow.IsNativeOverflowSessionActive) return;
        if (_frameTimer is not null)
        {
            _frameTimer.Interval = FrameInterval;
            if (!_frameTimer.IsEnabled) _frameTimer.Start();
        }
        OnAutoHideFrame();
    }
    private PixelRect _restingRect;
    private int _fullyHiddenOffsetPixels;
    private bool _revealLocked = true;
    public bool IsRevealLocked => _revealLocked;
    public void PrepareRevealForStartup() => GlassSurface.PrepareRevealForWindow(this);
    private bool _edgeRevealedDuringSuppression;
    private bool _launcherContainerActive;
    private Rect? _slidePanelBoundsDip;
    private bool _backdropWasActive;
    private bool _panelWasConcealed;
    private long _revealPreparationStarted;
    private bool _windowsTaskbarHidden;

    /// <summary>Keep the collapsed destination present until the expanded container has returned.</summary>
    public void SetLauncherContainerState(bool expanded)
    {
        _launcherContainerActive = expanded;
        if (expanded && !_performanceSleepRequested && _restingRect.Width > 0)
        {
            _autoHide?.ForceShown(DateTime.UtcNow);
            _slideOffsetPixels = 0;
            ApplySlide(_restingRect, 0);
        }
    }

    /// <summary>Độ trượt ra hiện tại (pixel vật lý). 0 = hiện hoàn toàn tại chỗ; <see cref="SlideOutDistancePixels"/> = ẩn hoàn toàn.</summary>
    private double _slideOffsetPixels;

    internal bool IsOpticalSurfaceVisible => _slideOffsetPixels < Math.Max(1, _restingRect.Height - 4);
    internal PixelRect OpticalBackdropBounds => _restingRect;

    private DisplayInfo? CurrentPrimaryDisplay => PrimaryDisplaySession is { } session
        ? session.IsAvailable ? session.Display : null
        : DisplayService.Primary();

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    protected override Geometry CreatePanelGeometryCore(Rect bounds, double radius) =>
        SquircleGlassGeometry.Create(bounds, radius);

    protected override bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) =>
        SquircleGlassGeometry.Contains(_slidePanelBoundsDip ?? bounds, radius, point);

    protected override bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        SquircleGlassGeometry.TryGetEdge(_slidePanelBoundsDip ?? bounds, radius, point, out depth, out inwardNormal);

    protected override bool UsesSquirclePanelGeometryCore => true;

    public DockWindow(bool isSecondary = false)
    {
        InitializeComponent();
        if (LoadThisPcShellIcon() is { } thisPcIcon)
        {
            ThisPcIcon.Source = thisPcIcon;
            ThisPcIcon.Visibility = Visibility.Visible;
            ThisPcFallbackIcon.Visibility = Visibility.Collapsed;
        }

        InitializeEntryHover();
        PanelCornerRadius = new CornerRadius(40);
        GlassSurface.IsBackdropActive = () => IsOpticalSurfaceVisible;
        GlassSurface.SuppressAmbientFallback = true;

        _tileStrip = new DockTileStrip();
        _tileStrip.TileClicked += OnTileClicked;
        _tileStrip.TileContextMenuRequested += OnTileContextMenuRequested;
        _tileStrip.ReorderFinished += (_, _) => RefreshTiles();
        _tileStrip.UnpinRequested += OnUnpinRequested;
        _tileStrip.TileHoverChanged += OnDockTileHoverChanged;
        _tileStrip.SetReorderModel(new DockReorderModel(SaveDockOrder));
        TileStripHost.Content = _tileStrip;

        _previewHoverTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _previewHoverTimer.Tick += OnPreviewHoverTimerTick;

        _previewDismissTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _previewDismissTimer.Tick += OnPreviewDismissTimerTick;

        if (!isSecondary)
        {
            _altTabController = new AltTabController(() => _windowsTaskbarHidden, Dispatcher, GetCandidateWindowIcon);
            _altTabController.Start();
        }

        // Các mục tích hợp thường trực không phụ thuộc vào host: sau khi khởi tạo xong sẽ xuất hiện ngay trên hàng biểu tượng.
        RefreshTiles();
    }

    /// <summary>
    /// Đường dẫn lưu xuống đĩa khi xác nhận sắp xếp: chỉ ghi một lần tại đây (R-08) và **chỉ ghi các mục thực có trong thư viện ứng dụng**.
    /// </summary>
    /// <remarks>
    /// Danh sách từ mô hình sắp xếp vốn chỉ nên chứa các ô ứng dụng, tại đây kiểm tra lại một lần nữa:
    /// loại bỏ mục tích hợp (<c>entry:*</c>), loại bỏ id không tồn tại trong thư viện, khử trùng lặp và giữ nguyên thứ tự.
    /// Tệp bố cục lưu lại “người dùng đã ghim những ứng dụng nào và theo thứ tự nào”, nếu lẫn id giả sẽ không còn phản ánh đúng điều đó.
    /// </remarks>
    private void SaveDockOrder(IReadOnlyList<string> order)
    {
        if (_services is null)
        {
            return;
        }

        var sanitized = DockLayoutPolicy.Sanitize(order, id => _services.Library.Find(id) is not null);

        _services.Layouts.Dock.PinnedItemIds = sanitized;
        _services.Layouts.SaveDock();
    }

    /// <summary>
    /// Dọn dẹp các id trong bố cục không thuộc thư viện ứng dụng; chỉ ghi xuống đĩa khi có thay đổi.
    /// </summary>
    /// <remarks>
    /// Các phiên bản trước đã ghi ba mục tích hợp (<c>entry:launcher</c>, v.v.) vào bố cục; những mục đã có trong tệp
    /// cần được xóa khi tải, đồng thời **giữ nguyên thứ tự ứng dụng ban đầu**.
    ///
    /// <para>
    /// Điều kiện an toàn: chỉ dọn dẹp khi thư viện ứng dụng **thực sự đã tải được mục**.
    /// Nếu thư viện trống có thể là do lỗi tải, khi đó mọi id đều “không tìm thấy” và việc dọn dẹp sẽ xóa sạch toàn bộ bố cục.
    /// </para>
    /// </remarks>
    public void SanitizePinnedLayout()
    {
        if (_services is null || _services.Library.Items.Count == 0)
        {
            return;
        }

        var pinned = _services.Layouts.Dock.PinnedItemIds;
        var sanitized = DockLayoutPolicy.Sanitize(pinned, id => _services.Library.Find(id) is not null);

        if (DockLayoutPolicy.SameOrder(pinned, sanitized))
        {
            return;
        }

        var dropped = pinned.Count - sanitized.Count;
        _services.Layouts.Dock.PinnedItemIds = sanitized;
        try
        {
            _services.Layouts.SaveDock();
        }
        catch (StorageException)
        {
            AppendLayoutNotice("Không thể lưu sau khi dọn mục đã ghim cũ; bố cục trên đĩa vẫn được giữ nguyên và Dock tiếp tục khởi chạy. Hãy kiểm tra quyền ghi của thư mục dữ liệu.");
            return;
        }

        AppendLayoutNotice($"Đã xóa {dropped} mục ghim khỏi tệp bố cục vì chúng không có trong thư viện ứng dụng (mục tích hợp sẵn hoặc mục không còn hoạt động).");
    }

    /// <summary>
    /// Kéo ra để bỏ ghim (D-03): chỉ bỏ ghim, <strong>không xóa</strong> bản ghi trong thư viện ứng dụng —
    /// mục này vẫn còn trong Trình khởi chạy.
    /// </summary>
    /// <remarks>
    /// Các mục tích hợp không chịu ảnh hưởng của luồng này: chúng không phải mục được ghim và cũng không có bộ xử lý kéo thả.
    /// Chặn thêm một lớp tại đây để phòng trường hợp bên gọi sau này truyền nhầm <c>entry:*</c> vào.
    /// </remarks>
    private void OnUnpinRequested(object? sender, string entryId)
    {
        if (_services is null || DockLayoutPolicy.IsEntryTile(entryId))
        {
            return;
        }

        if (!_services.Layouts.UnpinFromDock(entryId))
        {
            return;
        }

        try
        {
            _services.Layouts.SaveDock();
        }
        catch (StorageException)
        {
            ShowFailureNotice("Không thể lưu thao tác bỏ ghim; các mục ghim ban đầu đã được khôi phục.");
        }

        RefreshTiles();
    }

    /// <summary>Ghi một dòng thông báo liên quan đến bố cục vào nhật ký khởi động để tiện đối chiếu hành vi tự phục hồi sau này.</summary>
    private void AppendLayoutNotice(string message)
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            _services.Paths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(_services.Paths.LogDirectory, "startup.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Bố cục Dock: {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Lỗi ghi nhật ký tuyệt đối không được làm ảnh hưởng đến giao diện.
        }
    }

    /// <summary>Khi di chuột qua, chấp nhận mục ứng dụng khả dụng từ Trình khởi chạy hoặc danh sách tệp.</summary>
    private void OnExternalDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat)
            && e.Data.GetData(LauncherDragContract.SlotDataFormat) is string itemId)
        {
            e.Effects = _services?.Library.Find(itemId) is { IsUsable: true }
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop, true)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Kéo từ bên ngoài vào để nhập (D-01): tệp kéo từ File Explorer đi qua dịch vụ nhập,
    /// các mục thành công sẽ được ghim luôn vào cuối Dock — nếu không người dùng kéo xong sẽ không thấy gì.
    /// </summary>
    /// <remarks>
    /// Quyền đăng ký (mục 5 kế hoạch sửa đổi): WPF tự đăng ký đích kéo thả OLE cho handle cửa sổ
    /// (đã xác nhận qua đo thử C), ở đây chỉ dùng sự kiện Drop của WPF, tuyệt đối không gọi RegisterDragDrop nữa.
    /// </remarks>
    private void OnExternalDrop(object sender, DragEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat)
            && e.Data.GetData(LauncherDragContract.SlotDataFormat) is string itemId)
        {
            e.Handled = true;
            PinLauncherItem(itemId, e);
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop, true)) return;
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop, true) is not string[] { Length: > 0 } sources)
        {
            return;
        }

        // Trong khi kéo vào, bộ đếm tự động ẩn bị tạm dừng và sẽ khôi phục sau khi nhập xong.
        _autoHide?.PushSuppress(DateTime.UtcNow);

        try
        {
            var requests = sources.Select(source => new ImportRequest { Source = source }).ToList();
            var result = _services.Import.ImportItems(requests);

            // Gộp các id đã ghim với “mục mới thêm / đã có trong thư viện” — các mục trùng cũng phải được ghim:
            // khi kéo lại một ứng dụng từng bỏ khỏi Dock, trình nhập báo là “trùng lặp”;
            // nếu chỉ xét Added thì thao tác này sẽ không có phản hồi gì (vì mục vốn đã nằm trong thư viện).
            var pinned = _services.Layouts.Dock.PinnedItemIds;
            var merged = DockPinMerge.Merge(
                pinned,
                result.Added.Select(entry => entry.Id),
                result.Duplicates.Select(entry => entry.Id));

            var newlyPinned = merged.Count - pinned.Count;

            if (newlyPinned > 0)
            {
                _services.Layouts.Dock.PinnedItemIds = merged;
                try
                {
                    _services.Layouts.SaveDock();
                }
                catch (StorageException)
                {
                    ShowFailureNotice("Đã nhập vào thư viện ứng dụng nhưng không thể lưu mục ghim trên Dock; thứ tự ban đầu đã được khôi phục.");
                    RefreshTiles();
                    return;
                }
            }

            ShowTemporaryNotice(result.Failures.Count == 0
                ? $"Đã ghim {newlyPinned} mục ({result.Added.Count} mục mới, {result.Duplicates.Count} mục đã có trong thư viện)."
                : $"Đã ghim {newlyPinned} mục; {result.Failures.Count} mục không thành công.");

            RefreshTiles();
        }
        finally
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
        }
    }

    private void PinLauncherItem(string itemId, DragEventArgs e)
    {
        var entry = _services?.Library.Find(itemId);
        if (entry is not { IsUsable: true })
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var pinned = _services!.Layouts.Dock.PinnedItemIds;
        var merged = DockPinMerge.Merge(pinned, [entry.Id], []);
        if (merged.Count == pinned.Count)
        {
            ShowTemporaryNotice("Ứng dụng này đã có trên Dock.");
            e.Effects = DragDropEffects.Copy;
            RefreshTiles();
            return;
        }

        _services.Layouts.Dock.PinnedItemIds = merged;
        try
        {
            _services.Layouts.SaveDock();
            ShowTemporaryNotice("Đã thêm vào Dock.");
            e.Effects = DragDropEffects.Copy;
            RefreshTiles();
        }
        catch (StorageException)
        {
            ShowFailureNotice("Không thể lưu bố cục Dock; thứ tự biểu tượng ban đầu đã được khôi phục.");
            e.Effects = DragDropEffects.None;
            RefreshTiles();
        }
    }

    /// <summary>Cài đặt Dock đang có hiệu lực; khi chưa gắn host sẽ trả về giá trị mặc định.</summary>
    public DockSettings Settings => _dockSettings?.Settings ?? new DockSettings();

    /// <summary>
    /// Tổng số ô biểu tượng hiện tại (các mục đã ghim + ba mục tích hợp). Dùng cho chẩn đoán và kiểm thử.
    /// </summary>
    public int TileCount => _models.Count;

    /// <summary>
    /// Trao cho Dock các thành phần do host khởi tạo: cổng dịch vụ ứng dụng, kênh thông báo cài đặt Dock và hàm gọi lại “chuyển đổi Trình khởi chạy”.
    /// </summary>
    /// <remarks>
    /// Ràng buộc kiến trúc (tài liệu khởi công 4.7): Dock sử dụng dịch vụ ứng dụng thông qua <see cref="AppServiceHost"/>,
    /// tuyệt đối không tham chiếu dự án host <c>Glassquid.App</c>; thay đổi cài đặt được đẩy vào qua
    /// <see cref="DockSettingsHost.Changed"/>, Dock không thăm dò tệp cài đặt;
    /// trạng thái chạy được đẩy vào qua <see cref="RunningAppTracker.Changed"/>,
    /// Dock không tự truy vấn cửa sổ nào.
    /// </remarks>
    public void AttachServices(AppServiceHost services, DockSettingsHost settings, Action toggleLauncher)
    {
        ArgumentNullException.ThrowIfNull(toggleLauncher);
        AttachServices(services, settings, _ => toggleLauncher());
    }

    public void AttachServices(AppServiceHost services, DockSettingsHost settings, Action<PixelRect?> toggleLauncher)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(toggleLauncher);

        if (_dockSettings is not null)
        {
            _dockSettings.Changed -= OnDockSettingsChanged;
        }

        if (_services is not null)
        {
            _services.Running.Changed -= OnRunningChanged;
        }

        _services = services;
        _dockSettings = settings;
        _toggleLauncher = toggleLauncher;

        _dockSettings.Changed += OnDockSettingsChanged;
        _services.Running.Changed += OnRunningChanged;

        // Trong bố cục có thể còn sót mục tích hợp (entry:*) do phiên bản cũ ghi vào hoặc id không còn tồn tại;
        // dọn sạch trước lần vẽ ô biểu tượng đầu tiên.
        SanitizePinnedLayout();

        RebuildAutoHide();
        RefreshTiles();
        RefreshFromSettings();
    }

    /// <summary>Raise the non-activating Dock above Launcher while it is a drag target.</summary>
    public void RaiseAboveLauncherDuringDrag() => _ = WindowStyles.BringToTopWithoutActivation(Handle);

    /// <summary>Dock sử dụng bán kính bo góc riêng của thanh Dock, không dùng của Trình khởi chạy.</summary>
    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(40, appearance.DockCornerRadius) * DockScale;
    }

    // ---- Ô biểu tượng ----

    /// <summary>
    /// Dựng lại hàng ô biểu tượng theo thứ tự ghim trong tài liệu bố cục.
    /// </summary>
    /// <remarks>
    /// Thứ tự ghim đến từ danh sách có thứ tự liên tục của <c>dock-layout.json</c>: vị trí chính là chỉ mục.
    /// Các id được tham chiếu trong bố cục nhưng không còn trong thư viện sẽ bị bỏ qua trực tiếp — điều đó chỉ xảy ra
    /// khi dữ liệu bị chỉnh sửa thủ công; trên luồng bình thường, “Bỏ ghim khỏi Dock” sẽ cập nhật đồng bộ bố cục.
    /// </remarks>
    public void RefreshTiles()
    {
        if (_performanceSleepRequested) { _tilesDirty = true; return; }
        var settings = Settings;
        var dotBrush = ThemeService.Current?.Palette.ForegroundBrush;

        _models.Clear();
        _runtimeTargets.Clear();
        _runtimeEntries.Clear();

        // Các mục ghim phụ thuộc vào dịch vụ ứng dụng; khi chưa gắn host (trường hợp truy vấn ngay sau khi khởi tạo) chỉ thiếu mục ghim,
        // các mục tích hợp độc lập với mục ghim và do cài đặt Dock kiểm soát hiển thị.
        if (_services is { } services)
        {
            var tracker = services.Running;
            var pinnedIds = services.Layouts.Dock.PinnedItemIds.ToHashSet(StringComparer.Ordinal);
            var representedWindows = new HashSet<nint>();

            foreach (var id in services.Layouts.Dock.PinnedItemIds)
            {
                var entry = services.Library.Find(id);

                if (entry is null || !entry.IsUsable)
                {
                    continue;
                }

                _models.Add(CreateTile(entry, settings, tracker, dotBrush));
                foreach (var window in tracker.FindWindows(entry))
                {
                    representedWindows.Add(window.Handle);
                }
            }

            // Unpinned library apps appear only while they have a window open.
            foreach (var entry in services.Library.Items)
            {
                if (pinnedIds.Contains(entry.Id)
                    || !entry.IsUsable
                    || entry.Kind != AppItemKind.Program)
                {
                    continue;
                }

                var windows = tracker.FindWindows(entry)
                    .Where(window => !representedWindows.Contains(window.Handle))
                    .ToArray();

                if (windows.Length == 0)
                {
                    continue;
                }

                _models.Add(CreateTile(entry, settings, tracker, dotBrush, isPinned: false, isRunning: true));
                foreach (var window in windows)
                {
                    representedWindows.Add(window.Handle);
                }
            }

            // Include visible apps that have never been imported into the library. These
            // entries are transient: closing the last window removes the Dock tile.
            var unknownWindows = tracker.LastWindows
                .Where(window => window.ProcessId != (uint)Environment.ProcessId
                    && !IsWindowsSystemHelper(window)
                    && !representedWindows.Contains(window.Handle))
                .ToArray();

            foreach (var group in unknownWindows.GroupBy(RuntimeGroupKey, StringComparer.OrdinalIgnoreCase))
            {
                var windows = group.ToArray();
                if (windows.Length == 0)
                {
                    continue;
                }

                var imagePath = windows.Select(window => window.ImagePath)
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
                var id = RuntimeTilePrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key)));
                var entry = new AppEntry
                {
                    Id = id,
                    DisplayName = RuntimeDisplayName(imagePath, windows),
                    Kind = AppItemKind.Program,
                    Target = imagePath ?? string.Empty,
                    IconSource = imagePath,
                    AppUserModelId = windows.Select(window => window.EffectiveAumid)
                        .FirstOrDefault(aumid => !string.IsNullOrWhiteSpace(aumid)),
                };

                _runtimeTargets[id] = windows;
                _runtimeEntries[id] = entry;
                _models.Add(CreateTile(entry, settings, tracker, dotBrush, isPinned: false, isRunning: true));
            }
        }

        _tileStrip.SetTiles(_models);
        ApplyStripMetrics();
    }

    private DockTileModel CreateTile(
        AppEntry entry,
        DockSettings settings,
        RunningAppTracker tracker,
        Brush? dotBrush,
        bool isPinned = true,
        bool isRunning = false)
    {
        var tooltip = entry.DisplayName;

        if (entry.IsTargetMissing)
        {
            // Mục không hợp lệ (R-07) hiện được giải thích rõ trong chú giải công cụ; giao diện “hiển thị không hợp lệ”
            // và mục định vị lại trong menu chuột phải sẽ được nối ở đợt 5.
            tooltip += "(Mục tiêu đã di chuyển; bạn có thể tìm lại vị trí.)";
        }

        return new DockTileModel
        {
            EntryId = entry.Id,
            DisplayName = entry.DisplayName,
            ToolTipText = tooltip,
            Icon = LoadTileIcon(entry),
            DotBrush = dotBrush,
            IconSize = DockSettings.FixedIconSize * DockScale,
            TileMargin = new Thickness(DockSettings.FixedIconSpacing * DockScale / 2, 0, DockSettings.FixedIconSpacing * DockScale / 2, 0),
            IsMissing = entry.IsTargetMissing,
            IsPinned = isPinned,
            RunningVisibility = isRunning || tracker.IsRunning(entry)
                ? Visibility.Visible
                : Visibility.Collapsed,
        };
    }

    private static string RuntimeGroupKey(WindowIdentityInfo window)
    {
        if (!string.IsNullOrWhiteSpace(window.EffectiveAumid))
        {
            return "aumid:" + window.EffectiveAumid;
        }

        if (!string.IsNullOrWhiteSpace(window.ImagePath))
        {
            return "path:" + window.ImagePath;
        }

        return "pid:" + window.ProcessId;
    }

    private static bool IsWindowsSystemHelper(WindowIdentityInfo window)
    {
        if (string.IsNullOrWhiteSpace(window.ImagePath))
        {
            return false;
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            return false;
        }

        var normalizedWindowsDirectory = windowsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var windowsPrefix = normalizedWindowsDirectory + Path.DirectorySeparatorChar;
        if (!window.ImagePath.StartsWith(windowsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var systemAppsPrefix = Path.Combine(normalizedWindowsDirectory, "SystemApps") + Path.DirectorySeparatorChar;
        return window.ImagePath.StartsWith(systemAppsPrefix, StringComparison.OrdinalIgnoreCase)
            || WindowsHelperExecutables.Contains(Path.GetFileName(window.ImagePath));
    }

    private static string RuntimeDisplayName(string? imagePath, IReadOnlyList<WindowIdentityInfo> windows)
    {
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(imagePath);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
            catch (ArgumentException)
            {
                // Fall back to the visible window title below.
            }
        }

        return windows.Select(window => window.Title)
            .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title))
            ?? "Ứng dụng đang chạy";
    }

    /// <summary>
    /// Giao việc giới hạn kích thước và cuộn cho dải ô biểu tượng.
    /// </summary>
    /// <remarks>
    /// Chiều rộng tối đa = chiều rộng DIP vùng làm việc của màn hình đích − khoảng đệm bảng, các mục tích hợp đã bật và phần dư phóng to.
    /// Khi các mục ghim vượt quá giới hạn, dải ô biểu tượng tự cuộn ngang (mục 6 kế hoạch sửa đổi), cửa sổ không bao giờ vượt ra khỏi vùng làm việc.
    /// </remarks>
    private void ApplyStripMetrics()
    {
        var settings = Settings;
        var display = CurrentPrimaryDisplay;
        var scale = DockScale;
        DockContentLayer.Margin = new Thickness(10 * scale, 8 * scale, 10 * scale, 8 * scale);
        if ((LauncherButton.Visibility == Visibility.Visible) != settings.ShowLauncherEntry
            || (ThisPcButton.Visibility == Visibility.Visible) != settings.ShowThisPcEntry
            || (DesktopIconsButton.Visibility == Visibility.Visible) != settings.ShowDesktopIconsEntry)
            ResetEntryHover();
        LauncherButton.Visibility = settings.ShowLauncherEntry ? Visibility.Visible : Visibility.Collapsed;
        ThisPcButton.Visibility = settings.ShowThisPcEntry ? Visibility.Visible : Visibility.Collapsed;
        DesktopIconsButton.Visibility = settings.ShowDesktopIconsEntry ? Visibility.Visible : Visibility.Collapsed;
        var entryIconSize = DockSettings.FixedIconSize * scale;
        var entrySpacing = DockSettings.FixedIconSpacing * scale;
        foreach (var button in new[] { LauncherButton, ThisPcButton, DesktopIconsButton })
        {
            button.Width = entryIconSize;
            button.Height = (DockSettings.FixedIconSize + 16) * scale;
            button.Margin = new Thickness(0, 0, entrySpacing, 0);
        }
        LaunchpadTile.Width = LaunchpadTile.Height = ThisPcTile.Width = ThisPcTile.Height
            = DesktopIconsTile.Width = DesktopIconsTile.Height = entryIconSize;
        LaunchpadTile.CornerRadius = new CornerRadius(14 * scale);
        PanelCornerRadius = new CornerRadius(40 * scale);

        var contentLimit = display is null
            ? 0.0
            : Math.Max(1, display.ToDips((int)GetPlacementArea(display).Width) - 32 - (28 + BuiltinEntryWidthDip) * scale);

        if (settings.FrameLengthDip > 0)
            contentLimit = Math.Max(1, ResolveEffectiveFrameLengthDip(settings.FrameLengthDip, display) - (22 + BuiltinEntryWidthDip) * scale);

        _tileStrip.SetMetrics(
            DockSettings.FixedIconSize * scale,
            DockSettings.FixedIconSpacing * scale,
            DockSettings.FixedMagnificationPeak,
            8 * scale,
            contentLimit);
    }

    private double DockScale => double.IsFinite(Settings.SizeScale)
        ? Math.Clamp(Settings.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale) : 1;

    private double BuiltinEntryWidthDip => (DockSettings.FixedIconSize + DockSettings.FixedIconSpacing) * ((Settings.ShowLauncherEntry ? 1 : 0)
        + (Settings.ShowThisPcEntry ? 1 : 0) + (Settings.ShowDesktopIconsEntry ? 1 : 0));

    private double ResolveEffectiveFrameLengthDip(double requested, DisplayInfo? display)
    {
        var entryMinimum = Math.Max(DockSettings.MinimumFrameLengthDip, BuiltinEntryWidthDip + 22) * DockScale;
        if (display is null)
            return Math.Clamp(requested, entryMinimum, DockSettings.MaximumFrameLengthDip);
        var maxWidth = Math.Max(64, display.ToDips((int)GetPlacementArea(display).Width) - 32);
        maxWidth = Math.Min(DockSettings.MaximumFrameLengthDip, maxWidth);
        var minimum = Math.Min(entryMinimum, maxWidth);
        return Math.Clamp(requested, minimum, maxWidth);
    }

    private void ApplyFrameLength()
    {
        var requested = Settings.FrameLengthDip;
        if (requested <= 0)
        {
            SizeToContent = System.Windows.SizeToContent.WidthAndHeight;
            Width = double.NaN;
            return;
        }

        var display = CurrentPrimaryDisplay;
        SizeToContent = System.Windows.SizeToContent.Height;
        Width = ResolveEffectiveFrameLengthDip(requested, display);
    }

    /// <summary>Lấy biểu tượng cho một ô: ưu tiên biểu tượng người dùng tự chọn, tiếp đến là bản trích xuất trong bộ nhớ đệm, nếu không có thì để trống.</summary>
    private BitmapImage? LoadTileIcon(AppEntry entry)
    {
        var custom = entry.CustomIconPath;

        if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
        {
            return LoadBitmap(custom);
        }

        if (IsWindowsSettingsEntry(entry))
        {
            var win11Logo = ResolveWindows11SettingsIconPath();
            if (win11Logo != null && File.Exists(win11Logo))
            {
                return LoadBitmap(win11Logo);
            }
        }

        if (_services is null || string.IsNullOrEmpty(entry.IconSource))
        {
            return null;
        }

        var cached = _services.Icons.PathFor(entry.IconSource);
        if (!File.Exists(cached))
        {
            cached = _services.Icons.EnsureFor(entry.IconSource)?.Path ?? cached;
        }

        return File.Exists(cached) ? LoadBitmap(cached) : null;
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            // OnLoad: đọc pixel vào bộ nhớ rồi giải phóng handle tệp ngay, nếu không người dùng trong File Explorer
            // sẽ không xóa được biểu tượng đang được Dock hiển thị.
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or System.IO.FileFormatException)
        {
            return null;
        }
    }

    private static BitmapSource? LoadThisPcShellIcon()
    {
        // Windows shell namespace identity for "This PC".
        var icon = IconExtractor.Extract("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
        if (icon is null || icon.Width <= 0 || icon.Height <= 0 || icon.Bgra.Length < icon.Width * icon.Height * 4)
        {
            return null;
        }

        try
        {
            var source = BitmapSource.Create(icon.Width, icon.Height, 96, 96, PixelFormats.Bgra32,
                null, icon.Bgra, icon.Width * 4);
            source.Freeze();
            return source;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Nhấp vào ô biểu tượng: thu nhỏ nếu ứng dụng đang ở tiền cảnh; đưa lên trước nếu ở nền; khởi chạy nếu chưa chạy.</summary>
    private void OnTileClicked(object? sender, string entryId)
    {
        if (_runtimeTargets.ContainsKey(entryId))
        {
            ActivateRuntimeTile(entryId, minimizeIfForeground: true);
            return;
        }

        OpenEntry(entryId, minimizeIfForeground: true);
    }

    private void ActivateRuntimeTile(string entryId, bool minimizeIfForeground)
    {
        if (!_runtimeTargets.TryGetValue(entryId, out var candidates))
        {
            RefreshTiles();
            return;
        }

        var windows = candidates.Where(window => WindowControl.IsAlive(window.Handle)).ToArray();
        if (windows.Length == 0)
        {
            ShowFailureNotice("Cửa sổ này đã đóng. Hãy nhấn biểu tượng lần nữa.");
            RefreshTiles();
            return;
        }

        if (windows.Length > 1)
        {
            ShowWindowChooser(windows.Select(window => new WindowCandidate
            {
                Handle = window.Handle,
                Title = window.Title,
            }).ToList());
            return;
        }

        var handle = windows[0].Handle;
        if (minimizeIfForeground
            && handle == WindowControl.GetForegroundWindow()
            && !WindowControl.IsMinimized(handle))
        {
            if (!WindowControl.Minimize(handle))
            {
                ShowFailureNotice("Không thể thu nhỏ cửa sổ này. Hãy thử lại.");
            }

            return;
        }

        var outcome = LaunchService.ActivateWindow(handle);
        if (outcome.Outcome != ActivationOutcome.Activated)
        {
            ShowFailureNotice(outcome.Message ?? "Không thể đưa cửa sổ đó lên trước.");
        }
    }

    private void OpenEntry(string entryId, bool minimizeIfForeground)
    {
        if (entryId.StartsWith("entry:", StringComparison.Ordinal))
        {
            RunEntryAction(entryId["entry:".Length..]);
            return;
        }

        if (_services is null)
        {
            return;
        }

        var entry = _services.Library.Find(entryId);

        if (entry is not null && entry.Kind == AppItemKind.Folder && !entry.IsTargetMissing)
        {
            ShowFolderPanel(entry);
            return;
        }

        var outcome = _services.Launch.ActivateOrLaunch(entryId, minimizeIfForeground);

        switch (outcome.Outcome)
        {
            case ActivationOutcome.MultipleWindowsFound:
                ShowWindowChooser(outcome.Candidates);
                break;

            case ActivationOutcome.Failed:
                ShowFailureNotice(outcome.Message);
                break;

            default:
                // Đưa lên trước, khởi chạy hoặc thu nhỏ cửa sổ bản thân nó đã là phản hồi.
                break;
        }
    }

    /// <summary>Điều phối hành động cho các mục tích hợp.</summary>
    private void RunEntryAction(string name)
    {
        switch (name)
        {
            case "launcher":
                // D-12: ô mục tích hợp kích hoạt hàm gọi lại chuyển đổi của host (Dock không giữ trực tiếp Trình khởi chạy).
                RequestLauncherToggle();
                break;

            case "showdesktop":
                // D-13: triển khai dựa trên kết quả đo thực tế của phép thử D (gọi SW_MINIMIZE từng cửa sổ).
                _ = DesktopCommands.ShowDesktop();
                break;

            case "recyclebin":
                // D-14: Thùng rác là mục tích hợp, mở trực tiếp qua shell; tuyệt đối không nới lỏng kiểm tra tồn tại khi nhập
                // chỉ để nhét vào thư viện ứng dụng (cảnh báo rõ ràng ở mục 4.6 tài liệu khởi công giai đoạn 4).
                var opened = ShellExecutor.Open("shell:RecycleBinFolder", null, null);
                if (!opened.Started)
                {
                    ShowFailureNotice("Không thể mở Thùng rác.");
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Bảng bật lên của thư mục (D-11): liệt kê các mục trong thư mục, nhấp để mở qua shell.
    /// </summary>
    /// <remarks>
    /// Khi bảng mở sẽ lấy tiêu điểm bàn phím, hỗ trợ Esc; khi đóng chỉ trả lại cửa sổ cũ nếu tiêu điểm vẫn thuộc về Dock,
    /// không giành lại từ ứng dụng khác mà người dùng đã chuyển sang. Tạm dừng tự động ẩn trong lúc bảng đang mở.
    /// Mọi lượt nhấp trong bảng đều đi qua cùng một đường dẫn shell (<see cref="ShellExecutor"/>).
    /// </remarks>
    private void ShowFolderPanel(AppEntry folderEntry)
    {
        _autoHide?.PushSuppress(DateTime.UtcNow);
        var foregroundBefore = WindowControl.GetForegroundWindow();

        var palette = ThemeService.Current?.Palette;

        var header = new TextBlock
        {
            Text = folderEntry.DisplayName,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(6, 4, 6, 2),
        };

        var list = new StackPanel();

        _ = list.Children.Add(header);
        _ = list.Children.Add(PanelItem("Mở thư mục này", () =>
        {
            var open = ShellExecutor.Open(folderEntry.Target, null, null);
            if (!open.Started)
            {
                ShowFailureNotice("Không thể mở thư mục.");
            }
        }));

        string[] children;

        try
        {
            children = Directory
                .EnumerateFileSystemEntries(folderEntry.Target)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCulture)
                .Take(50)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            children = [];
        }

        if (children.Length == 0)
        {
            _ = list.Children.Add(new TextBlock
            {
                Text = "(Thư mục trống hoặc không thể đọc nội dung.)",
                Margin = new Thickness(10, 4, 10, 6),
            });
        }

        foreach (var child in children)
        {
            var path = child;
            _ = list.Children.Add(PanelItem(Path.GetFileName(path), () =>
            {
                var open = ShellExecutor.Open(path, null, null);
                if (!open.Started)
                {
                    ShowFailureNotice($"Không thể mở: {Path.GetFileName(path)}");
                }
            }));
        }

        var panel = new PhoneBorder
        {
            Background = palette?.ResolvePanelBrush(false) ?? Brushes.DimGray,
            BorderBrush = palette?.PanelBorderBrush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            MinWidth = 220,
            MaxHeight = 420,
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };

        var popup = new Popup
        {
            Child = panel,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        panel.Focusable = true;
        panel.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Escape) return;
            popup.IsOpen = false;
            args.Handled = true;
        };
        popup.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, 0);
        };
        _ = WindowActivation.TryBringToForeground(Handle);
        popup.IsOpen = true;
        _ = panel.Focus();
    }

    private static TextBlock PanelItem(string text, Action onClick)
    {
        var item = new TextBlock
        {
            Text = text,
            Padding = new Thickness(10, 5, 10, 5),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        item.MouseLeftButtonUp += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// Menu ngữ cảnh chuột phải của ô biểu tượng (D-04): các mục menu khớp với hành vi thực tế, không đặt mục giữ chỗ.
    /// </summary>
    /// <remarks>
    /// Thanh chính mang cờ <c>WS_EX_NOACTIVATE</c>, nếu không kích hoạt sẽ không bắt được nhấp bên ngoài menu —
    /// việc tự động đóng menu cần ngữ nghĩa “thu gọn khi nhấp ra ngoài”, nên trước khi mở phải đưa Dock lên tiền cảnh tường minh.
    /// Sau khi đóng sẽ kiểm tra lại tiền cảnh có trễ và chỉ trả lại cửa sổ trước nếu tiêu điểm vẫn thuộc lớp bật lên hoặc thanh chính này.
    /// Mục không hợp lệ (R-07) sẽ cung cấp mục “Tìm lại vị trí…” tại đây.
    /// </remarks>
    private void OnTileContextMenuRequested(object? sender, string entryId)
    {
        if (_services is null)
        {
            return;
        }

        var entry = _services.Library.Find(entryId);
        var isTransient = false;
        if (entry is null && _runtimeEntries.TryGetValue(entryId, out var transientEntry))
        {
            entry = transientEntry;
            isTransient = true;
        }

        if (entry is null)
        {
            return;
        }

        _autoHide?.PushSuppress(DateTime.UtcNow);

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
        };
        var foregroundBefore = WindowControl.GetForegroundWindow();
        nint menuHandle = 0;
        menu.Opened += (_, _) => menuHandle = (PresentationSource.FromVisual(menu) as System.Windows.Interop.HwndSource)?.Handle ?? 0;

        if (isTransient)
        {
            _ = menu.Items.Add(MenuItem("Đưa cửa sổ lên trước", () => ActivateRuntimeTile(entryId, minimizeIfForeground: false)));
        }
        else
        {
            _ = menu.Items.Add(MenuItem("Mở", () => OpenEntry(entryId, minimizeIfForeground: false)));
        }

        if (!isTransient && entry.Kind == AppItemKind.Folder)
        {
            _ = menu.Items.Add(MenuItem("Xem nội dung trong bảng", () => ShowFolderPanel(entry)));
        }

        _ = menu.Items.Add(new Separator());

        if (!isTransient && entry.IsTargetMissing)
        {
            // Điểm vào giao diện của R-07: sau khi mục tiêu bị xóa, người dùng trỏ lại mục này sang vị trí mới từ đây.
            _ = menu.Items.Add(MenuItem("Tìm lại vị trí…", () => RelocateFromDialog(entry)));
        }

        var isPinned = _services.Layouts.Dock.PinnedItemIds.Contains(entryId, StringComparer.Ordinal);
        if (isPinned)
        {
            _ = menu.Items.Add(MenuItem("Bỏ ghim khỏi Dock", () => OnUnpinRequested(this, entryId)));
        }
        else if (isTransient)
        {
            if (string.IsNullOrWhiteSpace(entry.AppUserModelId) && File.Exists(entry.Target))
            {
                _ = menu.Items.Add(MenuItem("Thêm và ghim vào Dock", () => PinRuntimeEntry(entry)));
            }
        }
        else
        {
            _ = menu.Items.Add(MenuItem("Ghim vào Dock", () => PinRuntimeEntry(entry)));
        }

        menu.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, menuHandle);
        };

        _ = WindowActivation.TryBringToForeground(Handle);
        menu.IsOpen = true;
    }

    private void PinRuntimeEntry(AppEntry entry)
    {
        if (_services is null)
        {
            return;
        }

        AppEntry? libraryEntry = _services.Library.Find(entry.Id);
        if (libraryEntry is null)
        {
            var result = _services.Import.ImportItems([new ImportRequest
            {
                Source = entry.Target,
                DisplayName = entry.DisplayName,
            }]);

            libraryEntry = result.Added.Concat(result.Duplicates).FirstOrDefault();
            if (libraryEntry is null)
            {
                ShowFailureNotice(result.Failures.Count > 0
                    ? result.Failures[0].Message
                    : "Không thể thêm ứng dụng này vào thư viện.");
                return;
            }
        }

        var pinned = _services.Layouts.Dock.PinnedItemIds;
        if (pinned.Contains(libraryEntry.Id, StringComparer.Ordinal))
        {
            RefreshTiles();
            return;
        }

        _services.Layouts.Dock.PinnedItemIds = DockPinMerge.Merge(pinned, [libraryEntry.Id], []);
        try
        {
            _services.Layouts.SaveDock();
            ShowTemporaryNotice("Đã ghim vào Dock.");
        }
        catch (StorageException)
        {
            ShowFailureNotice("Không thể lưu mục ghim; hãy thử lại.");
        }

        RefreshTiles();
    }

    /// <summary>Hộp thoại tìm lại vị trí: chọn một mục tiêu mới thực sự tồn tại rồi chuyển cho dịch vụ bảo trì.</summary>
    private void RelocateFromDialog(AppEntry entry)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Tìm lại vị trí của “{entry.DisplayName}”",
            CheckFileExists = false,
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        var outcome = _services!.Maintenance.Relocate(entry.Id, dialog.FileName);

        if (outcome.Result != RelocateResult.Relocated)
        {
            ShowFailureNotice(outcome.Message);
        }

        RefreshTiles();
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// Danh sách chọn nhiều cửa sổ (D-10): liệt kê tiêu đề các cửa sổ ứng viên, nhấp vào mục nào sẽ kích hoạt cửa sổ đó.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tương tác (quy tắc tiêu điểm của bản sửa đổi 4 được áp dụng tại đây):
    /// <list type="bullet">
    /// <item>Nhấp một dòng → kích hoạt cửa sổ của dòng đó; <b>handle phải lấy từ mục danh sách tương ứng</b>,
    /// không được chỉ đọc Tag của nguồn nhấp (nguồn thường là phần chữ bên trong vốn không có Tag,
    /// dẫn đến nhấp vào không có phản hồi mà cũng không báo lỗi — xem <see cref="WindowChooserHit"/>);</item>
    /// <item>Enter → kích hoạt dòng đang chọn; Esc → đóng (tương đương hủy);</item>
    /// <item>Thanh chính mang cờ <c>WS_EX_NOACTIVATE</c>, lớp bật lên muốn nhận tiêu điểm bàn phím phải tự đưa mình lên tiền cảnh tường minh;
    /// vì vậy khi mở cần ghi nhớ cửa sổ tiền cảnh lúc đó và <b>trả lại khi hủy (Esc / nhấp bên ngoài)</b>,
    /// còn khi chọn và kích hoạt thành công thì tiêu điểm tự nhiên sẽ ở lại trên ứng dụng đích.</item>
    /// </list>
    /// </para>
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hWnd, uint Msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern nint GetClassLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    internal static BitmapSource? GetCandidateWindowIcon(nint hwnd)
    {
        if (hwnd == 0) return null;

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        var exePath = pid != 0 ? TryGetProcessPath(pid) : null;
        if (!string.IsNullOrEmpty(exePath) && IsWindowsSettings(exePath))
        {
            var win11Logo = ResolveWindows11SettingsIconPath();
            if (win11Logo != null && File.Exists(win11Logo))
            {
                return LoadBitmap(win11Logo);
            }
        }

        const uint WmGetIcon = 0x007F;
        const nint IconSmall2 = 2;
        const nint IconSmall = 0;
        const nint IconBig = 1;
        const int GclpHIconSm = -34;
        const int GclpHIcon = -14;
        const uint SmtoAbortIfHung = 0x0002;

        try
        {
            if (SendMessageTimeout(hwnd, WmGetIcon, IconSmall2, 0, SmtoAbortIfHung, 100, out var hIcon) != 0 && hIcon != 0)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }

            if (SendMessageTimeout(hwnd, WmGetIcon, IconSmall, 0, SmtoAbortIfHung, 100, out hIcon) != 0 && hIcon != 0)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }

            if (SendMessageTimeout(hwnd, WmGetIcon, IconBig, 0, SmtoAbortIfHung, 100, out hIcon) != 0 && hIcon != 0)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }

            hIcon = GetClassLongPtr(hwnd, GclpHIconSm);
            if (hIcon == 0)
            {
                hIcon = GetClassLongPtr(hwnd, GclpHIcon);
            }

            if (hIcon != 0)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }

            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (sysIcon != null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(sysIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
        }
        catch
        {
            // Ignore fallback
        }

        return null;
    }

    private static string? TryGetProcessPath(uint pid)
    {
        if (pid == 0) return null;
        const uint ProcessQueryLimitedInformation = 0x1000;
        var hProcess = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProcess == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            if (QueryFullProcessImageName(hProcess, 0, buffer, ref size))
            {
                return new string(buffer, 0, (int)size);
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            _ = CloseHandle(hProcess);
        }
        return null;
    }

    private static bool IsWindowsSettings(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("SystemSettings", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ImmersiveControlPanel", StringComparison.OrdinalIgnoreCase)
            || text.Contains("immersivecontrolcenter", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ms-settings", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsSettingsEntry(AppEntry entry)
    {
        return IsWindowsSettings(entry.Target)
            || IsWindowsSettings(entry.IconSource)
            || IsWindowsSettings(entry.AppUserModelId);
    }

    private static string? ResolveWindows11SettingsIconPath()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(winDir, "ImmersiveControlPanel", "images", "logo.targetsize-256.png");
        if (File.Exists(path)) return path;

        path = Path.Combine(winDir, "ImmersiveControlPanel", "images", "logo.scale-400.png");
        if (File.Exists(path)) return path;

        path = Path.Combine(winDir, "ImmersiveControlPanel", "images", "logo.png");
        if (File.Exists(path)) return path;

        return null;
    }

    private List<WindowCandidate> GetCandidateWindowsForEntry(string entryId)
    {
        if (_runtimeTargets.TryGetValue(entryId, out var winArray))
        {
            var alive = winArray
                .Where(w => WindowControl.IsAlive(w.Handle))
                .Select(w => new WindowCandidate { Handle = w.Handle, Title = w.Title })
                .ToList();
            if (alive.Count > 0) return alive;
        }

        if (_services?.Library.Find(entryId) is { } entry)
        {
            try
            {
                var liveWindows = WindowMatcher.FindWindows(entry);
                if (liveWindows.Count > 0)
                {
                    return liveWindows
                        .Where(w => WindowControl.IsAlive(w.Handle))
                        .Select(w => new WindowCandidate { Handle = w.Handle, Title = w.Title })
                        .ToList();
                }
            }
            catch
            {
                // Fallback
            }

            var windows = _services.Running.FindWindows(entry);
            if (windows.Count > 0)
            {
                return windows
                    .Where(w => WindowControl.IsAlive(w.Handle))
                    .Select(w => new WindowCandidate { Handle = w.Handle, Title = w.Title })
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(entry.Target))
            {
                var targetPath = entry.Target;
                if (targetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var info = ShortcutResolver.Resolve(targetPath);
                        if (info != null && !string.IsNullOrWhiteSpace(info.Target))
                        {
                            targetPath = info.Target;
                        }
                    }
                    catch
                    {
                    }
                }

                var direct = WindowLocator.FindWindowsForImage(targetPath);
                return direct
                    .Where(w => WindowControl.IsAlive(w.Handle))
                    .Select(w => new WindowCandidate { Handle = w.Handle, Title = w.Title })
                    .ToList();
            }
        }

        return [];
    }

    private void OnDockTileHoverChanged(object? sender, DockTileHoverEventArgs e)
    {
        _hoveredEntryId = e.EntryId;
        _hoveredTileBounds = e.ScreenBounds;

        if (string.IsNullOrEmpty(e.EntryId) || e.EntryId.StartsWith("entry:", StringComparison.Ordinal))
        {
            _previewHoverTimer.Stop();
            if (_activeDwmPreview != null && _activeDwmPreview.IsLoaded)
            {
                _previewDismissTimer.Stop();
                _previewDismissTimer.Start();
            }
            return;
        }

        var candidates = GetCandidateWindowsForEntry(e.EntryId);
        if (candidates.Count == 0)
        {
            _previewHoverTimer.Stop();
            if (_activeDwmPreview != null && _activeDwmPreview.IsLoaded)
            {
                _previewDismissTimer.Stop();
                _previewDismissTimer.Start();
            }
            return;
        }

        _previewDismissTimer.Stop();

        var bounds = _hoveredTileBounds;
        if (bounds.IsEmpty)
        {
            bounds = _tileStrip.GetTileScreenBounds(e.EntryId);
        }

        if (_activeDwmPreview != null && _activeDwmPreview.IsLoaded)
        {
            _previewHoverTimer.Stop();
            ShowDwmPreview(candidates, bounds);
        }
        else
        {
            _previewHoverTimer.Stop();
            _previewHoverTimer.Start();
        }
    }

    private void OnPreviewHoverTimerTick(object? sender, EventArgs e)
    {
        _previewHoverTimer.Stop();

        if (string.IsNullOrEmpty(_hoveredEntryId))
        {
            return;
        }

        var candidates = GetCandidateWindowsForEntry(_hoveredEntryId);
        if (candidates.Count == 0)
        {
            return;
        }

        var bounds = _hoveredTileBounds;
        if (bounds.IsEmpty)
        {
            bounds = _tileStrip.GetTileScreenBounds(_hoveredEntryId);
        }

        ShowDwmPreview(candidates, bounds);
    }

    private void OnPreviewDismissTimerTick(object? sender, EventArgs e)
    {
        _previewDismissTimer.Stop();

        if (_activeDwmPreview != null)
        {
            if (_activeDwmPreview.IsMouseOverPreview())
            {
                return;
            }

            CloseDwmPreview();
        }
    }

    private void ShowDwmPreview(IReadOnlyList<WindowCandidate> candidates, Rect tileScreenBounds)
    {
        if (candidates.Count == 0)
        {
            CloseDwmPreview();
            return;
        }

        if (_activeDwmPreview != null)
        {
            _activeDwmPreview.Close();
            _activeDwmPreview = null;
        }

        if (tileScreenBounds.IsEmpty || tileScreenBounds.Width <= 0)
        {
            tileScreenBounds = new Rect(Left + (ActualWidth / 2.0) - 24, Top, 48, 48);
        }

        var preview = new DwmPreviewWindow(
            candidates,
            tileScreenBounds,
            activateHandle =>
            {
                CloseDwmPreview();
                var outcome = LaunchService.ActivateWindow(activateHandle);
                if (outcome.Outcome != ActivationOutcome.Activated)
                {
                    ShowFailureNotice(outcome.Message ?? "Không thể đưa cửa sổ đó lên trước.");
                }
            },
            closeHandle =>
            {
                _ = WindowControl.Close(closeHandle);
            },
            GetCandidateWindowIcon);

        preview.PreviewMouseEnter += (_, _) =>
        {
            _previewDismissTimer.Stop();
        };

        preview.PreviewMouseLeave += (_, _) =>
        {
            _previewDismissTimer.Stop();
            _previewDismissTimer.Start();
        };

        preview.Closed += (_, _) =>
        {
            if (_activeDwmPreview == preview)
            {
                _activeDwmPreview = null;
            }
        };

        _activeDwmPreview = preview;
        preview.Show();
    }

    private void CloseDwmPreview()
    {
        _previewHoverTimer.Stop();
        _previewDismissTimer.Stop();

        if (_activeDwmPreview != null)
        {
            try
            {
                _activeDwmPreview.Close();
            }
            catch
            {
                // Ignore closing exceptions
            }
            _activeDwmPreview = null;
        }
    }

    private void ShowWindowChooser(IReadOnlyList<WindowCandidate> candidates)
    {
        if (candidates.Count == 0) return;

        var bounds = Rect.Empty;
        if (!string.IsNullOrEmpty(_hoveredEntryId))
        {
            bounds = _tileStrip.GetTileScreenBounds(_hoveredEntryId);
        }

        ShowDwmPreview(candidates, bounds);
    }

    // Closed can run before the menu action or window activation completes. Defer the
    // decision, and restore only if focus is still ours. Clicking another app must win.
    private void RestorePopupForeground(nint previous, nint popupHandle)
    {
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!WindowControl.IsAlive(previous)) return;
            var current = WindowControl.GetForegroundWindow();
            if (current == Handle || current == 0 || (popupHandle != 0 && current == popupHandle))
                _ = WindowActivation.TryBringToForeground(previous);
        }));
    }

    /// <summary>Lỗi khởi chạy không thể trôi qua trong im lặng: hiện lý do lên dòng trạng thái rồi tự khôi phục sau vài giây.</summary>
    private void ShowFailureNotice(string? message)
    {
        ShowTemporaryNotice(message);
    }

    private void ShowTemporaryNotice(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;

        _failureNoticeTimer ??= NewFailureNoticeTimer();
        _failureNoticeTimer.Stop();
        _failureNoticeTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer NewFailureNoticeTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = FailureNoticeDuration,
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefreshDiagnostics();
            RefreshFromSettings();
        };

        return timer;
    }

    // ---- Chấm trạng thái đang chạy (D-07) ----

    private void OnRunningChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // Đối chiếu được kích hoạt trên thread pool; cập nhật chấm tròn quay về luồng giao diện. Chỉ đổi hiển thị, không dựng lại ô biểu tượng.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_services is null || _performanceSleepRequested)
            {
                return;
            }

            // Running apps are part of the tile list itself, so reconcile the row when
            // a window opens or closes instead of only updating the old running dots.
            RefreshTiles();
        });
    }

    // ---- Thông báo và làm mới ----

    /// <summary>Cập nhật văn bản mô tả theo trạng thái chủ đề và chất liệu hiện tại.</summary>
    public void RefreshDiagnostics()
    {
        var theme = ThemeService.Current;

        var surface = SurfaceMode == GlassSurfaceMode.Acrylic
            ? "Làm mờ nền acrylic"
            : "Bảng nền bán trong suốt tự vẽ (không làm mờ nền)";

        StatusText.Text = $"{surface}｜{(theme?.IsDark == true ? "Tối" : "Sáng")}";
        StatusText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Làm mới văn bản mô tả theo cài đặt Dock đang có hiệu lực (một trong các điểm thể hiện trực quan của D-16).
    /// </summary>
    public void RefreshFromSettings()
    {
        StatusText.Visibility = Visibility.Collapsed;
        ApplyFrameLength();
        ApplyStripMetrics();
    }

    private void OnDockSettingsChanged(object? sender, EventArgs e)
    {
        RebuildAutoHide();
        RefreshFromSettings();
        RefreshTiles();

        // Vị trí và độ lệch cũng có hiệu lực tức thì (D-16): cài đặt vừa đổi, Dock đang mở chuyển vị trí ngay tại chỗ.
        _slideOffsetPixels = 0;
        PositionOnDisplay();
        StartSlide();
    }

    /// <summary>Yêu cầu host chuyển đổi Trình khởi chạy; khi nhấp nút sẽ kèm theo hình chữ nhật màn hình vật lý hiện tại của nút.</summary>
    private void RequestLauncherToggle(PixelRect? sourceBounds = null) => _toggleLauncher?.Invoke(sourceBounds);

    private void OnLauncherButtonClick(object sender, RoutedEventArgs e) => RequestLauncherToggle(GetLauncherButtonPixelBounds());

    /// <summary>Read the launcher button's current physical screen rectangle after Dock layout.</summary>
    public PixelRect? GetLauncherButtonPixelBounds()
    {
        if (!Settings.ShowLauncherEntry || LauncherButton.Visibility != Visibility.Visible
            || !IsVisible || !IsOpticalSurfaceVisible
            || LauncherButton.ActualWidth < 1 || LauncherButton.ActualHeight < 1) return null;

        try
        {
            var topLeft = LaunchpadTile.PointToScreen(new Point());
            var bottomRight = LaunchpadTile.PointToScreen(new Point(LaunchpadTile.ActualWidth, LaunchpadTile.ActualHeight));
            return new PixelRect((int)Math.Floor(topLeft.X), (int)Math.Floor(topLeft.Y),
                Math.Max(1, (int)Math.Ceiling(bottomRight.X - topLeft.X)),
                Math.Max(1, (int)Math.Ceiling(bottomRight.Y - topLeft.Y)));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void OnThisPcButtonClick(object sender, RoutedEventArgs e)
    {
        var opened = ShellExecutor.Open("shell:MyComputerFolder", null, null);
        if (!opened.Started)
        {
            ShowFailureNotice("Không thể mở Máy tính này.");
        }
    }

    private void OnDesktopIconsButtonClick(object sender, RoutedEventArgs e) => ToggleDesktopIcons?.Invoke();

    // ---- Đặt vị trí ----

    /// <summary>
    /// Đặt Dock lên màn hình chính được gắn khi khởi động theo cài đặt đang có hiệu lực (D-15/D-23).
    /// </summary>
    /// <remarks>
    /// Việc đặt vị trí dùng <strong>pixel vật lý và DPI của Primary Monitor lúc khởi động</strong>; <c>TargetDisplayId</c> trong cấu hình cũ
    /// chỉ giữ lại để tương thích dữ liệu, không tham gia chọn cửa sổ thực tế. Hình chữ nhật được giao cho
    /// <see cref="DisplayService.MoveWindowToPixelRect"/>.
    /// Phần hình học nằm trong <see cref="DockPlacementCalculator"/> (hàm thuần túy, có thể kiểm thử đơn vị ngoại tuyến).
    /// </remarks>
    public bool SuppressPassiveTriggersWhenFullscreen { get; set; } = true;

    public void RefreshDisplayPlacement()
    {
        ApplyFrameLength();
        ApplyStripMetrics();
        PositionOnDisplay();
    }

    public void SetWindowsTaskbarHidden(bool hidden)
    {
        if (_windowsTaskbarHidden == hidden) return;
        _windowsTaskbarHidden = hidden;
        RefreshDisplayPlacement();
    }

    public void StopForPrimaryDisplayChange()
    {
        _frameTimer?.Stop();
        Hide();
    }

    public void ResumeAfterPrimaryDisplayChange(bool show)
    {
        RebuildAutoHide();
        RefreshDisplayPlacement();
        if (show && !_performanceSleepRequested) Show();
    }

    public void PositionOnDisplay()
    {
        var handle = Handle;

        if (handle == 0)
        {
            return;
        }

        var display = CurrentPrimaryDisplay;
        if (display is null)
        {
            return;
        }

        // Dùng SizeToContent nên kích thước cuối cùng chỉ biết được sau khi bố cục chạy xong.
        var widthDips = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        var heightDips = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;

        if (widthDips <= 0 || heightDips <= 0)
        {
            return;
        }

        // Ở mức thu phóng cực đoan, làm tròn có thể ra 0; cửa sổ phải có ít nhất 1 pixel vật lý.
        var widthPixels = Math.Max(1, display.ToPixels(widthDips));
        var heightPixels = Math.Max(1, display.ToPixels(heightDips));

        var workAreaRect = DockPlacementCalculator.Calculate(
            GetPlacementArea(display),
            Settings.Placement,
            Settings.OffsetX,
            Settings.OffsetY,
            widthPixels,
            heightPixels,
            display.Dpi);
        _restingRect = _windowsTaskbarHidden && Settings.Placement != DockPlacement.TopCenter
            ? new PixelRect(
                workAreaRect.X,
                DockPlacementCalculator.Calculate(
                    display.Bounds,
                    Settings.Placement,
                    Settings.OffsetX,
                    Settings.OffsetY,
                    widthPixels,
                    heightPixels,
                    display.Dpi).Y,
                workAreaRect.Width,
                workAreaRect.Height)
            : workAreaRect;

        if (Settings.Placement != DockPlacement.TopCenter)
        {
            var bottomMargin = display.ToPixels(10);
            _restingRect = new PixelRect(_restingRect.X, _restingRect.Y - bottomMargin, _restingRect.Width, _restingRect.Height);
        }

        if (PrimaryDisplaySession is { IsAvailable: true } session)
            _restingRect = ClampToPlacementArea(_restingRect, display);

        _fullyHiddenOffsetPixels = SlideOutDistancePixels(_restingRect, display.Bounds, Settings.Placement);

        ApplySlide(_restingRect, _slideOffsetPixels);
    }

    private static PixelRect GetPlacementArea(DisplayInfo display)
    {
        // Keep horizontal placement anchored to the work area: hiding Explorer's
        // taskbar window doesn't release its appbar reservation, and using Bounds
        // for X would push a right-edge dock underneath the hidden taskbar area.
        return display.WorkArea;
    }

    private PixelRect ClampToPlacementArea(PixelRect rect, DisplayInfo display)
    {
        // When the taskbar is hidden, allow only the vertical placement to use
        // the newly exposed screen area. X remains based on the stable work area.
        var area = _windowsTaskbarHidden ? display.Bounds : display.WorkArea;
        var width = Math.Max(1, Math.Min(rect.Width, area.Width));
        var height = Math.Max(1, Math.Min(rect.Height, area.Height));
        var bottomMargin = Settings.Placement != DockPlacement.TopCenter ? display.ToPixels(10) : 0;
        return new PixelRect(
            Math.Clamp(rect.X, area.X, area.Right - width),
            Math.Clamp(rect.Y, area.Y, Math.Max(area.Y, area.Bottom - height - bottomMargin)),
            width,
            height);
    }

    /// <summary>Giữ HWND nằm trong vùng làm việc của màn hình chính, chỉ trượt nội dung và hình học bảng dùng chung trong phạm vi cắt xén của cửa sổ.</summary>
    private void ApplySlide(PixelRect restingRect, double slideOffsetPixels)
    {
        var slidePixels = (int)Math.Round(slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var concealed = restingRect.Height > 0 && Math.Abs(slidePixels) >= restingRect.Height;
        // HWND equality describes layout, not the pixels painted while hidden.
        // Retire old glass only once the entire panel is clipped, so every reveal
        // prepares current content without disturbing the visible hide animation.
        if (concealed && !_panelWasConcealed)
        {
            GlassSurface.RetireConcealedMaterial();
        }
        _panelWasConcealed = concealed;
        var backdropActive = IsOpticalSurfaceVisible;
        if (backdropActive && !_backdropWasActive) GlassSurface.RequestBackdropRefresh(revealing: true);
        _backdropWasActive = backdropActive;
        if (PrimaryDisplaySession is { } session)
        {
            if (!session.IsAvailable) return;
            var fixedWindow = ClampToPlacementArea(restingRect, session.Display);
            if (DisplayService.GetWindowPixelBounds(Handle) != fixedWindow)
                _ = DisplayService.MoveWindowToPixelRect(Handle, fixedWindow);
            var scale = Math.Max(.01, session.Display.ScaleFactor);
            var visualOffsetDip = slidePixels / scale;
            DockSlideTransform.Y = visualOffsetDip;
            _slidePanelBoundsDip = new Rect(0, visualOffsetDip, ActualWidth, ActualHeight);
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                // Match the launcher: a fixed desktop texture with a moving GPU lens.
                // Only Content translates. New capture frames keep the same coordinates
                // throughout the slide and therefore remain consumable during motion.
                var hiddenOffsetDip = _fullyHiddenOffsetPixels * SlideOutDirection() / scale;
                GlassSurface.SetPanelShape(_slidePanelBoundsDip.Value, PanelCornerRadius.TopLeft);
                GlassSurface.SetContainerTransitionBounds(
                    new Rect(0, Math.Min(0, hiddenOffsetDip), ActualWidth,
                        ActualHeight + Math.Abs(hiddenOffsetDip)));
                SetSharedPanelClip(_slidePanelBoundsDip.Value, PanelCornerRadius.TopLeft);
            }
            var shadowStrength = Math.Clamp(1 - Math.Abs(visualOffsetDip) / Math.Max(1, ActualHeight), 0, 1);
            SetTransitionShadowShape(Math.Abs(visualOffsetDip) < .01
                    ? null
                    : _slidePanelBoundsDip,
                PanelCornerRadius.TopLeft, shadowStrength);
            return;
        }

        // Unbound design/test windows keep their legacy placement behavior. AppHost binds
        // every production GlassWindow to one immutable PrimaryDisplaySession.
        DockSlideTransform.Y = 0;
        SetTransitionShadowShape(null, 0);
        var rect = restingRect with { Y = restingRect.Y + slidePixels };
        _ = DisplayService.MoveWindowToPixelRect(Handle, rect);
    }

    /// <summary>Hướng trượt ra của nội dung: Dock ở dưới trượt xuống +1, Dock ở trên trượt lên −1.</summary>
    private int SlideOutDirection()
        => Settings.Placement == DockPlacement.TopCenter ? -1 : 1;

    /// <summary>Độ lệch pixel vật lý cần thiết để ẩn hoàn toàn nội dung; bản thân cửa sổ HWND luôn ở trong màn hình chính.</summary>
    private static int SlideOutDistancePixels(PixelRect restingRect, PixelRect displayBounds, DockPlacement placement)
    {
        var distanceToDisplayEdge = placement == DockPlacement.TopCenter
            ? restingRect.Bottom - displayBounds.Y
            : displayBounds.Bottom - restingRect.Y;

        // Dock neo sát cạnh vùng làm việc, giữa vùng làm việc và cạnh màn hình có thể có thanh tác vụ; dịch thêm 2 pixel
        // để tránh việc làm tròn pixel vật lý hoặc đường biên bề mặt cửa sổ để lại vệt mảnh.
        return Math.Max(0, distanceToDisplayEdge) + 2;
    }

    // ---- Tự động ẩn (D-19) ----

    /// <summary>Dựng lại máy trạng thái theo cài đặt hiện tại. Cài đặt có thể đổi bất cứ lúc nào, máy trạng thái là bất biến nên thay mới toàn bộ.</summary>
    private void RebuildAutoHide()
    {
        var settings = Settings;

        _autoHide = new AutoHideController(settings.AutoHideLeaveDelayMs);

        _autoHide.ForceShown(DateTime.UtcNow);
        _slideOffsetPixels = 0;

        if (_frameTimer is null)
        {
            // Capture completion/icon work can keep Normal queued while the panel
            // is concealed. Poll/slide admission must still advance to its first
            // visible pixel; settled idle remains 200 ms and game sleep stops it.
            _frameTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = FrameInterval,
            };
            _frameTimer.Tick += (_, _) => OnAutoHideFrame();
        }

        if (!_isForegroundHookSubscribed)
        {
            _isForegroundHookSubscribed = true;
            ForegroundEventWatcher.Subscribe(OnSystemForegroundChanged);
        }

        _frameTimer.Interval = FrameInterval;
        if (Settings.AutoHide)
        {
            _frameTimer.IsEnabled = true;
            _frameTimer.Start();
        }
        else
        {
            _frameTimer.IsEnabled = false;
        }
    }

    /// <summary>
    /// Khung hình dùng chung: thăm dò con trỏ → máy trạng thái → nội suy trượt.
    /// </summary>
    /// <remarks>
    /// Toàn bộ phán đoán dùng <strong>hình chữ nhật tĩnh đã lưu đệm</strong> và phép liệt kê hiển thị, không chạm vào cây trực quan WPF;
    /// chi phí mỗi lần chạy 16 ms chỉ là một lần truy vấn con trỏ và một ít phép tính số học.
    /// </remarks>
    private void OnAutoHideFrame()
    {
        if (_performanceSleepRequested) { AdvancePerformanceSleep(); return; }
        if (_autoHide is null || _services is null || _restingRect.Width == 0)
        {
            return;
        }

        if (SystemTrayBarWindow.IsNativeOverflowSessionActive)
        {
            _desktopFocusStartTime = DateTimeOffset.MinValue;
            _frameTimer?.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        var cursor = DisplayService.GetCursorPixelPoint();
        var fullscreen = FullscreenDetector.IsFullscreenActive();
        var covered = ForegroundCoverage.IsCovered(_restingRect);
        var suppressed = fullscreen || covered;
        var pointerOverDock = cursor is { } currentPoint
            && Inflate(GetVisibleDockRect(), 2).Contains(currentPoint.X, currentPoint.Y);

        var pointerOverDockPosition = cursor is { } position
            && Inflate(_restingRect, 2).Contains(position.X, position.Y);

        var pointerOverAnyVisible = pointerOverDock
            || (cursor is { } cp && _companionTray?.IsPointerOverTray(cp) == true)
            || (cursor is { } cp2 && _companionSearch?.IsPointerOverSearch(cp2) == true);

        var pointerOverAnyPosition = pointerOverDockPosition
            || (cursor is { } pos && _companionTray?.IsPointerOverTrayPosition(pos) == true)
            || (cursor is { } pos2 && _companionSearch?.IsPointerOverSearchPosition(pos2) == true);

        UpdateAutoHideState(now, fullscreen, covered, pointerOverAnyPosition, pointerOverAnyVisible);

        var target = _autoHide.State == AutoHideState.Hidden
            ? (double)_fullyHiddenOffsetPixels
            : 0.0;

        // Admit the reveal intent before preparation starts the native owner.
        // Waiting for optical visibility lets a foreground-policy tick put that
        // new owner back to sleep while the panel is still crossing the edge.
        NotifyPerformanceSurfaceVisibility();

        // Prepare a complete material before the first pixels slide back into view.
        if (target == 0 && _backgroundCaptureSleeping) SetBackgroundCaptureSleeping(false);
        else if (target == 0) PrepareConcealedReveal();
        if (target == 0 && !CanStartPreparedReveal(System.Diagnostics.Stopwatch.GetTimestamp()))
        {
            if (_frameTimer is not null) _frameTimer.Interval = FrameInterval;
            NotifyPerformanceSurfaceVisibility();
            return;
        }
        if (target != 0)
        {
            _revealPreparationStarted = 0;
        }

        if (Math.Abs(_slideOffsetPixels - target) > 0.5)
        {
            _slideOffsetPixels += (target - _slideOffsetPixels) * 0.22;

            if (Math.Abs(_slideOffsetPixels - target) <= 0.5)
            {
                _slideOffsetPixels = target;
            }

            ApplySlide(_restingRect, _slideOffsetPixels);
        }
        else if (_slideOffsetPixels != target)
        {
            _slideOffsetPixels = target;
            ApplySlide(_restingRect, _slideOffsetPixels);
        }

        // Đồng bộ tuyệt đối tiến độ trượt sang các dock phụ (khay hệ thống phải, tìm kiếm trái)
        var progress = _fullyHiddenOffsetPixels > 0
            ? Math.Clamp(_slideOffsetPixels / _fullyHiddenOffsetPixels, 0.0, 1.0)
            : (target == 0 ? 0.0 : 1.0);

        _companionTray?.SyncSlideProgress(progress, _autoHide.State);
        _companionSearch?.SyncSlideProgress(progress, _autoHide.State);

        if (_frameTimer is not null)
        {
            if (Math.Abs(_slideOffsetPixels - target) > 0.5)
            {
                _frameTimer.Interval = FrameInterval;
            }
            else
            {
                // Khi đã hoàn tất trượt: tiếp tục polling con trỏ ở tần số nghỉ (100ms) nếu auto-hide bật hoặc đang ẩn/bị che
                if (Settings.AutoHide || _autoHide.State == AutoHideState.Hidden || covered)
                {
                    _frameTimer.Interval = TimeSpan.FromMilliseconds(100);
                }
                else
                {
                    _frameTimer.Stop();
                }
            }
        }

        NotifyPerformanceSurfaceVisibility();
    }

    private bool CanStartPreparedReveal(long now)
    {
        if (_restingRect.Height <= 0 || !_panelWasConcealed
            || GlassSurface.IsCurrentRevealMaterialReady)
        { _revealPreparationStarted = 0; return true; }
        if (_revealPreparationStarted == 0) _revealPreparationStarted = now;
        return now - _revealPreparationStarted >= (long)(System.Diagnostics.Stopwatch.Frequency * 2.5);
    }

    private void UpdateAutoHideState(DateTime now, bool fullscreen, bool covered,
        bool pointerOverDockPosition, bool pointerOverVisibleDock)
    {
        if (_autoHide is null) return;
        var popupOpen = _launcherContainerActive
            || _tileStrip.IsDragging
            || (_companionTray?.HasActivePopup() == true)
            || (_companionSearch?.HasActivePopup() == true);

        if (popupOpen)
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }
        else if (_autoHide.Suppressed || _tileStrip.IsDragging)
        {
            _edgeRevealedDuringSuppression = fullscreen || covered;
            _autoHide.ForceShown(now);
        }
        else if (fullscreen || covered)
        {
            if (_revealLocked)
            {
                _edgeRevealedDuringSuppression = false;
                _autoHide.ForceHide(now);
            }
            else if (_autoHide.State == AutoHideState.Hidden)
            {
                if (PassiveRevealPolicy.CanRevealDuringSuppression(pointerOverDockPosition, _revealLocked)
                    && _autoHide.TryReveal(false, now))
                    _edgeRevealedDuringSuppression = true;
            }
            else if (_edgeRevealedDuringSuppression)
            {
                if (pointerOverVisibleDock) _autoHide.PointerEnter(now);
                else _autoHide.PointerLeave(now);
                _autoHide.Poll(now);
            }
            else
                _autoHide.ForceHide(now);
        }
        else if (Settings.AutoHide)
        {
            _edgeRevealedDuringSuppression = false;
            if (pointerOverDockPosition || pointerOverVisibleDock)
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

        if (_autoHide.State == AutoHideState.Hidden)
            _edgeRevealedDuringSuppression = false;
    }

    private PixelRect GetVisibleDockRect()
    {
        var slidePixels = (int)Math.Round(_slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var translated = _restingRect with { Y = _restingRect.Y + slidePixels };
        if (PrimaryDisplaySession is null) return translated;
        var left = Math.Max(_restingRect.X, translated.X);
        var top = Math.Max(_restingRect.Y, translated.Y);
        var right = Math.Min(_restingRect.Right, translated.Right);
        var bottom = Math.Min(_restingRect.Bottom, translated.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(_restingRect.X, _restingRect.Y, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>Chuyển đổi khóa đánh thức khi bị che khuất trong phiên chạy này; không ảnh hưởng đến Dock không bị che phủ.</summary>
    public void ToggleRevealLock()
    {
        _revealLocked = !_revealLocked;
        if (_performanceSleepRequested) return;
        if (_frameTimer is not null)
        {
            _frameTimer.Interval = FrameInterval;
            _frameTimer.IsEnabled = true;
        }
        OnAutoHideFrame();
    }

    private static PixelRect Inflate(PixelRect rect, int by)
        => new(rect.X - by, rect.Y - by, rect.Width + by * 2, rect.Height + by * 2);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        RefreshDiagnostics();

        // Phải sửa kiểu mở rộng sau khi handle được tạo: khi nhấp vào cửa sổ này không được nhường tiền cảnh cho cửa sổ khác.
        // Khi thành phần bật lên cần lấy tiêu điểm thì gọi tường minh WindowActivation.TryBringToForeground.
        WindowStyles.MakeNoActivate(Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        _altTabController?.Dispose();
        _altTabController = null;
        _frameTimer?.Stop();
        _failureNoticeTimer?.Stop();
        if (_dockSettings is not null)
        {
            _dockSettings.Changed -= OnDockSettingsChanged;
        }

        if (_services is not null)
        {
            _services.Running.Changed -= OnRunningChanged;
        }

        base.OnClosed(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // Sau khi kích thước cửa sổ (nội dung) thay đổi thì đặt lại vị trí theo cài đặt; lần bố cục đầu tiên của SizeToContent cũng đi qua đây.
        PositionOnDisplay();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_frameTimer is not null && !_frameTimer.IsEnabled)
        {
            WakeSlide();
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, [Out] char[] className, int maxCount);

    private bool _hasActiveExternalApp;
    private DateTimeOffset _desktopFocusStartTime = DateTimeOffset.MinValue;
    private bool _desktopSlideTriggered = true;
    private bool _isForegroundHookSubscribed;
    private System.Windows.Threading.DispatcherTimer? _desktopDebounceTimer;

    private void StartSlide()
    {
        if (_performanceSleepRequested || _autoHide is null || _services is null || _restingRect.Width == 0) return;
        if (SystemTrayBarWindow.IsNativeOverflowSessionActive) return;
        if (_frameTimer is not null)
        {
            _frameTimer.Interval = FrameInterval;
            _frameTimer.Start();
        }
    }

    private void OnSystemForegroundChanged(nint currentFg)
    {
        if (_performanceSleepRequested || _autoHide is null || _restingRect.Width == 0) return;
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
                _desktopDebounceTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
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
        if (_performanceSleepRequested || _autoHide is null || _restingRect.Width == 0) return;
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
                if (!Settings.AutoHide)
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
        if (_autoHide is not null && !Settings.AutoHide)
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
