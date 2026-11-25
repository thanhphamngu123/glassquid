using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Glassquid.Core.Geometry;
using Glassquid.Core.Models.Settings;
using Glassquid.Platform;
using Glassquid.Platform.Displays;
using Glassquid.UI.Theming;

// WPF có sẵn System.Windows.ThemeMode, xung đột với enum cùng tên trong mô hình cài đặt; dùng bí danh để cố định về enum của riêng chúng ta.
using ThemeMode = Glassquid.Core.Models.ThemeMode;

namespace Glassquid.UI.Controls;

/// <summary>Chế độ kính mà cửa sổ cuối cùng áp dụng.</summary>
public enum GlassSurfaceMode
{
    /// <summary>Đã có hiệu ứng làm mờ nền thực sự. Cảm giác thị giác gần giống kính mờ của macOS.</summary>
    Acrylic = 0,

    /// <summary>
    /// Chỉ có lớp tô bán trong suốt tự vẽ, phía sau không làm mờ.
    /// </summary>
    /// <remarks>
    /// Đây là nhánh dự phòng, không phải lỗi: giao diện dùng cho hiệu ứng làm mờ chưa được công khai và có thể bị gỡ bỏ trong các bản dựng tương lai,
    /// nên bắt buộc phải có một phương án hiển thị không phụ thuộc vào nó.
    /// </remarks>
    SelfDrawn = 1,

    /// <summary>Clear refractive material with an explicitly self-excluded desktop source.</summary>
    OpticalGlass = 2,
}

/// <summary>
/// Lớp cơ sở cho mọi cửa sổ do sản phẩm này hiển thị.
/// </summary>
/// <remarks>
/// Main windows share clear optical surfaces and continuous-curvature clipping. A verified
/// capture-exclusion session keeps their own pixels out of the live desktop source. This
/// intentionally also excludes Glassquid from supported screenshots and recording tools.
/// A separate input-transparent GlassShadow provides ambient elevation without changing
/// the main window's alpha-zero outside corners. Opaque accessibility mode remains available.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF window resources are acquired in OnSourceInitialized and released in OnClosed.")]
public abstract class GlassWindow : Window
{
    private CaptureExclusionSession? _captureExclusion;
    private GlassShadow? _shadow;
    private PrimaryDisplaySession? _primaryDisplaySession;
    /// <summary>
    /// Khoảng trống chừa ra bên ngoài bảng điều khiển, tính theo DIP. **Mặc định là 0**.
    /// </summary>
    /// <remarks>
    /// Keep the main window exactly the size of its panel. Nonzero shadow pixels in this
    /// window would intercept other applications' clicks. GlassShadow instead owns a separate
    /// WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE window around this zero-inset panel.
    /// </remarks>
    private const double DefaultChromeInset = 0;

    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(
        nameof(Palette),
        typeof(GlassPalette),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty PanelCornerRadiusProperty = DependencyProperty.Register(
        nameof(PanelCornerRadius),
        typeof(CornerRadius),
        typeof(GlassWindow),
        new PropertyMetadata(new CornerRadius(36)));

    public static readonly DependencyProperty ChromeInsetProperty = DependencyProperty.Register(
        nameof(ChromeInset),
        typeof(Thickness),
        typeof(GlassWindow),
        new PropertyMetadata(new Thickness(DefaultChromeInset)));

    public static readonly DependencyProperty PanelBackgroundBrushProperty = DependencyProperty.Register(
        nameof(PanelBackgroundBrush),
        typeof(Brush),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty PanelBorderBrushProperty = DependencyProperty.Register(
        nameof(PanelBorderBrush),
        typeof(Brush),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty ForegroundBrushProperty = DependencyProperty.Register(
        nameof(ForegroundBrush),
        typeof(Brush),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty DockBuiltinIconBrushProperty = DependencyProperty.Register(
        nameof(DockBuiltinIconBrush), typeof(Brush), typeof(GlassWindow), new PropertyMetadata(null));

    public static readonly DependencyProperty LaunchpadIconFilledProperty = DependencyProperty.Register(
        nameof(LaunchpadIconFilled), typeof(bool), typeof(GlassWindow), new PropertyMetadata(true));

    public static readonly DependencyProperty SecondaryForegroundBrushProperty = DependencyProperty.Register(
        nameof(SecondaryForegroundBrush),
        typeof(Brush),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    protected GlassWindow()
    {
        WindowStyle = WindowStyle.None;

        // Cửa sổ phân lớp, lý do xem mục 2 trong chú thích lớp.
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        DpiChanged += (_, e) =>
        {
            // Child Image DPI notifications bubble through Window when icons attach
            // during hover, dragging and paging, even at the identical DPI. Only an
            // actual scale change of this window invalidates desktop capture.
            if (!ReferenceEquals(e.OriginalSource, this) || e.OldDpi.Equals(e.NewDpi)) return;
            Glassquid.UI.BackdropRecovery.RetryBackground("WindowDpiChanged");
        };

        // Mặc định cửa sổ không thể thay đổi kích thước; cửa sổ nào cần thay đổi kích thước thì tự bật.
        ResizeMode = ResizeMode.NoResize;
    }

    /// <summary>Bảng màu của giao diện hiện tại. Được thay thế toàn bộ khi đổi giao diện.</summary>
    public GlassPalette? Palette
    {
        get => (GlassPalette?)GetValue(PaletteProperty);
        private set => SetValue(PaletteProperty, value);
    }

    /// <summary>Bán kính bo góc của bảng điều khiển trên cửa sổ này, do cài đặt giao diện quyết định.</summary>
    public CornerRadius PanelCornerRadius
    {
        get => (CornerRadius)GetValue(PanelCornerRadiusProperty);
        protected set => SetValue(PanelCornerRadiusProperty, value);
    }

    /// <summary>Builds the panel outline used by clipping, glass rendering, and its shadow.</summary>
    protected virtual Geometry CreatePanelGeometryCore(Rect bounds, double radius) =>
        SquircleGlassGeometry.Create(bounds, radius);

    /// <summary>Tests a point against the panel outline used by native hit testing.</summary>
    protected virtual bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) =>
        SquircleGlassGeometry.Contains(bounds, radius, point);

    /// <summary>Gets the inward edge data used by the optical refraction map.</summary>
    protected virtual bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        SquircleGlassGeometry.TryGetEdge(bounds, radius, point, out depth, out inwardNormal);

    /// <summary>Whether the active panel outline uses the shared phone-like superellipse.</summary>
    protected virtual bool UsesSquirclePanelGeometryCore => true;

    /// <summary>Allows readable utility windows to opt out of desktop glass independently.</summary>
    protected virtual bool UseOpaquePanel => false;

    /// <summary>Only the Launcher opts into the independent text brightness setting.</summary>
    protected virtual bool UseLauncherTextBrightness => false;

    internal Geometry CreateGlassPanelGeometry(Rect bounds, double radius) =>
        CreatePanelGeometryCore(bounds, radius);

    internal bool TryGetGlassPanelEdge(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        TryGetPanelGeometryEdgeCore(bounds, radius, point, out depth, out inwardNormal);

    internal bool UsesSquirclePanelGeometry => UsesSquirclePanelGeometryCore;

    /// <summary>Tracks the animated panel shape in the independent, input-transparent shadow.</summary>
    protected void SetTransitionShadowShape(Rect? panelBounds, double radius, double strength = 1) =>
        _shadow?.SetTransitionShape(panelBounds, radius, strength);

    private Geometry? _sharedPanelClip;
    protected void SetSharedPanelClip(Rect bounds, double radius)
    {
        SetSharedPanelGeometry(CreateGlassPanelGeometry(bounds, radius));
    }

    protected void SetSharedPanelGeometry(Geometry geometry)
    {
        _sharedPanelClip = geometry;
        Clip = _sharedPanelClip;
    }

    /// <summary>
    /// Lề ngoài chừa bên ngoài bảng điều khiển, tính theo DIP. Phần tử gốc XAML của bảng điều khiển liên kết thuộc tính này làm Margin.
    /// </summary>
    /// <remarks>
    /// <strong>Hiện luôn bằng 0, tức là hình chữ nhật cửa sổ trùng khít với bảng điều khiển hiển thị.</strong>
    /// Thuộc tính được giữ lại để việc quy đổi giữa bảng điều khiển và ranh giới cửa sổ chỉ nằm ở một nơi duy nhất (cả kiểm tra trúng đích và cắt vùng hiển thị đều dùng),
    /// chứ không phải vì vẫn còn chừa lề. Về việc có nên khôi phục lề hay không, và tại sao nếu khôi phục thì đồng thời phải đổi cách vẽ bóng đổ,
    /// hãy xem giải thích ở <see cref="DefaultChromeInset"/>.
    /// </remarks>
    public Thickness ChromeInset
    {
        get => (Thickness)GetValue(ChromeInsetProperty);
        protected set => SetValue(ChromeInsetProperty, value);
    }

    /// <summary>
    /// Lớp tô nền bảng điều khiển. Dùng lớp nào do chính cửa sổ này quyết định — xem <see cref="ApplyAppearance"/>.
    /// </summary>
    public Brush? PanelBackgroundBrush
    {
        get => (Brush?)GetValue(PanelBackgroundBrushProperty);
        private set => SetValue(PanelBackgroundBrushProperty, value);
    }

    /// <summary>Đường viền sáng mảnh ở mép bảng điều khiển.</summary>
    public Brush? PanelBorderBrush
    {
        get => (Brush?)GetValue(PanelBorderBrushProperty);
        private set => SetValue(PanelBorderBrushProperty, value);
    }

    public Brush? ForegroundBrush
    {
        get => (Brush?)GetValue(ForegroundBrushProperty);
        private set => SetValue(ForegroundBrushProperty, value);
    }

    public Brush? DockBuiltinIconBrush
    {
        get => (Brush?)GetValue(DockBuiltinIconBrushProperty);
        private set => SetValue(DockBuiltinIconBrushProperty, value);
    }

    public bool LaunchpadIconFilled
    {
        get => (bool)GetValue(LaunchpadIconFilledProperty);
        private set => SetValue(LaunchpadIconFilledProperty, value);
    }

    public Brush? SecondaryForegroundBrush
    {
        get => (Brush?)GetValue(SecondaryForegroundBrushProperty);
        private set => SetValue(SecondaryForegroundBrushProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        private set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>Chế độ kính mà cửa sổ này cuối cùng áp dụng. Chẩn đoán và mẫu minh họa lấy giá trị này làm chuẩn.</summary>
    public GlassSurfaceMode SurfaceMode { get; private set; } = GlassSurfaceMode.SelfDrawn;

    /// <summary>The process's fixed Primary Monitor session, inherited by owned glass dialogs.</summary>
    public PrimaryDisplaySession? PrimaryDisplaySession =>
        _primaryDisplaySession ?? (Owner as GlassWindow)?.PrimaryDisplaySession;

    /// <summary>Work area of this window's bound display in DIPs.</summary>
    public Rect WorkAreaDip => PrimaryDisplaySession?.Display is { } disp
        ? new Rect(disp.ToDips(disp.WorkArea.X), disp.ToDips(disp.WorkArea.Y), disp.ToDips(disp.WorkArea.Width), disp.ToDips(disp.WorkArea.Height))
        : SystemParameters.WorkArea;

    /// <summary>Bind this production window to the Primary Monitor captured at process startup.</summary>
    public void BindPrimaryDisplay(PrimaryDisplaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (IsLoaded || PresentationSource.FromVisual(this) is not null)
            throw new InvalidOperationException("Bind the Primary Monitor before creating the window handle.");
        if (_primaryDisplaySession is not null && !ReferenceEquals(_primaryDisplaySession, session))
            throw new InvalidOperationException("A Glassquid window cannot be rebound to another display session.");
        _primaryDisplaySession = session;
    }

    /// <summary>Clamp a physical window rectangle to the process Primary Monitor's work area.</summary>
    protected PixelRect PlaceWindowWithinPrimaryWorkArea(PixelRect requested)
    {
        var session = PrimaryDisplaySession;
        if (session is null || !session.IsAvailable) return new PixelRect(0, 0, 0, 0);
        var display = session.Display;
        var rect = session.ClampWindowToWorkArea(requested);
        var dipScale = 96.0 / Math.Max(1, display.Dpi);
        Width = rect.Width * dipScale;
        Height = rect.Height * dipScale;
        if (Handle != 0) _ = DisplayService.MoveWindowToPixelRect(Handle, rect);
        return rect;
    }

    /// <summary>Center a sized, not-yet-shown window inside the fixed Primary Monitor work area.</summary>
    protected PixelRect CenterOnPrimaryWorkArea()
    {
        var session = PrimaryDisplaySession;
        if (session is null || !session.IsAvailable) return new PixelRect(0, 0, 0, 0);
        var display = session.Display;
        var widthDip = double.IsFinite(Width) ? Width : ActualWidth;
        var heightDip = double.IsFinite(Height) ? Height : ActualHeight;
        if (widthDip <= 0 || heightDip <= 0) return new PixelRect(0, 0, 0, 0);

        var width = Math.Max(1, display.ToPixels(widthDip));
        var height = Math.Max(1, display.ToPixels(heightDip));
        var work = display.WorkArea;
        var requested = new PixelRect(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height);
        var placed = session.ClampWindowToWorkArea(requested);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = display.ToDips(placed.Width);
        Height = display.ToDips(placed.Height);
        var handle = Handle != 0 ? Handle : new WindowInteropHelper(this).EnsureHandle();
        _ = DisplayService.MoveWindowToPixelRect(handle, placed);
        return placed;
    }

    /// <summary>Keep an owned Glassquid dialog inside the current work area of its fixed Primary.</summary>
    public void ConstrainCurrentBoundsToPrimary()
    {
        var session = PrimaryDisplaySession;
        if (session is null || !session.IsAvailable || Handle == 0
            || DisplayService.GetWindowPixelBounds(Handle) is not { Width: > 0, Height: > 0 } current)
            return;
        var placed = session.ClampWindowToWorkArea(current);
        if (placed == current) return;
        var dipScale = 96.0 / Math.Max(1, session.Display.Dpi);
        Width = placed.Width * dipScale;
        Height = placed.Height * dipScale;
        _ = DisplayService.MoveWindowToPixelRect(Handle, placed);
    }

    /// <summary>
    /// Handle của cửa sổ; bằng <see cref="IntPtr.Zero"/> trước khi nguồn được khởi tạo.
    /// </summary>
    protected nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// Bề mặt này dùng bán kính nào trong cấu hình. Bản thiết kế quy định bán kính khác nhau cho Dock và Launchpad,
    /// nên lựa chọn này không thể đặt trong lớp cơ sở.
    /// </summary>
    protected virtual double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(40, appearance.DockCornerRadius);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PrimaryDisplaySession is { } session)
        {
            if (!session.IsAvailable)
            {
                Hide();
                return;
            }

            if (DisplayService.GetWindowPixelBounds(Handle) is { Width: > 0, Height: > 0 } initialBounds)
            {
                var primaryBounds = session.ClampWindowToWorkArea(initialBounds);
                if (primaryBounds != initialBounds)
                    _ = DisplayService.MoveWindowToPixelRect(Handle, primaryBounds);
            }
        }

        // Independent candidate only: the compositor source excludes our process.
        ApplyAppearance();

        _shadow = new GlassShadow(this);

        InstallHitTestHook();

        if (ThemeService.Current is { } theme)
        {
            theme.Changed += OnThemeChanged;
        }
    }

    /// <summary>
    /// Cho phép các điểm nằm ngoài góc bo tròn của cửa sổ đi xuyên qua **giữa các cửa sổ của chính tiến trình này**.
    /// </summary>
    /// <remarks>
    /// <b>Phạm vi áp dụng hẹp hơn nhiều so với trực giác, điểm này đã được đo thực tế để đính chính lại.</b>
    ///
    /// Khi <c>WM_NCHITTEST</c> trả về <c>HTTRANSPARENT</c>, theo tài liệu của Microsoft, ngữ nghĩa chuyển tiếp của nó
    /// **chỉ giới hạn trong các cửa sổ cùng luồng** — thông điệp đi theo thứ tự z giữa các cửa sổ cùng luồng cho tới khi có một cửa sổ trả về
    /// giá trị khác HTTRANSPARENT, và **sẽ không** truyền sang cửa sổ của tiến trình khác.
    ///
    /// Vì vậy tác dụng của bộ xử lý này chỉ giới hạn ở trường hợp các cửa sổ của chính sản phẩm chồng lên nhau (Launchpad che Dock,
    /// Trung tâm cài đặt che Launchpad, v.v., tất cả đều chạy trên luồng giao diện). Việc xuyên chuột giữa các tiến trình chỉ có thể dựa vào kiểm tra trúng đích
    /// theo alpha từng pixel của cửa sổ phân lớp, mà cơ chế đó chỉ có hiệu lực ở những nơi **alpha thực sự bằng 0**.
    ///
    /// <para>
    /// <b>Thứ gánh vác việc đó thay cho nó là <see cref="UpdateGlassClip"/>.</b> Phần khuyết góc bo tròn hiện đã được
    /// khoét bỏ khỏi vùng có thể vẽ, nên ở đó là alpha 0 thực sự, vì thế xuyên chuột sang tiến trình khác cũng hoạt động — đã kiểm chứng lại thực tế
    /// (cửa sổ của tiến trình khác nằm bên dưới tại phần khuyết góc bo tròn nhận được cú nhấp chuột; bên trong bảng điều khiển vẫn bắt chuột bình thường).
    /// Bộ xử lý này được giữ lại như một lớp bảo hiểm: nếu sau này vì bóng đổ mà đưa khoảng lề khác 0 trở lại,
    /// ít nhất nó vẫn đảm bảo các cửa sổ của chính sản phẩm không nuốt nhầm cú nhấp chuột của nhau.
    /// </para>
    /// </remarks>
    private void InstallHitTestHook()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        source.AddHook(OnWindowMessage);
    }

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WmNcHitTest = 0x0084;
        const int HtTransparent = -1;

        if (message != WmNcHitTest)
        {
            return 0;
        }

        // Trong lParam là tọa độ **pixel vật lý trên màn hình**, word thấp là x, word cao là y (mỗi giá trị đều có dấu).
        var screenX = unchecked((short)(long)lParam);
        var screenY = unchecked((short)((long)lParam >> 16));

        if (!IsInsidePanel(hwnd, screenX, screenY))
        {
            handled = true;
            return HtTransparent;
        }

        return 0;
    }

    /// <summary>Quy đổi tọa độ màn hình thành DIP trong cửa sổ, rồi chuyển cho bước kiểm tra góc bo tròn.</summary>
    private bool IsInsidePanel(nint hwnd, int screenX, int screenY)
    {
        if (PrimaryDisplaySession is { IsAvailable: false }) return false;
        var bounds = DisplayService.GetWindowPixelBounds(hwnd);
        if (bounds is not { } rect)
        {
            // Khi không lấy được hình chữ nhật cửa sổ thì đừng chặn: thà phản hồi như cửa sổ bình thường còn hơn làm cửa sổ không bấm được.
            return true;
        }

        var dpi = PrimaryDisplaySession is { IsAvailable: true } primary
            ? primary.Display.Dpi
            : DisplayService.DpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;

        // Hình chữ nhật cửa sổ tính theo pixel vật lý, còn bán kính bo góc và lề trong của bảng điều khiển đều tính theo DIP.
        var panelWidth = (rect.Width / scale) - ChromeInset.Left - ChromeInset.Right;
        var panelHeight = (rect.Height / scale) - ChromeInset.Top - ChromeInset.Bottom;

        var localX = ((screenX - rect.X) / scale) - ChromeInset.Left;
        var localY = ((screenY - rect.Y) / scale) - ChromeInset.Top;

        // Lấy bán kính lớn nhất trong bốn góc: kiểm tra theo hướng rộng rãi nhất để tránh nhận nhầm vùng đáng lẽ bấm được thành bên ngoài.
        var radius = Math.Max(
            Math.Max(PanelCornerRadius.TopLeft, PanelCornerRadius.TopRight),
            Math.Max(PanelCornerRadius.BottomLeft, PanelCornerRadius.BottomRight));

        return ContainsPanelGeometryCore(new Rect(0, 0, Math.Max(0, panelWidth), Math.Max(0, panelHeight)),
            radius, new Point(localX, localY));
    }

    protected override void OnClosed(EventArgs e)
    {
        _shadow?.Dispose();
        _shadow = null;
        _captureExclusion?.Dispose();
        CaptureExclusionSession.Forget(Handle);
        _captureExclusion = null;
        if (ThemeService.Current is { } theme)
        {
            theme.Changed -= OnThemeChanged;
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// Đưa giao diện hiện tại vào cửa sổ và quyết định sử dụng chế độ kính nào.
    /// </summary>
    protected void ApplyAppearance()
    {
        var theme = ThemeService.Current;
        var appearance = theme?.Appearance;

        var palette = theme?.Palette ?? Palette ?? GlassPalette.Create(true, GlassPalette.DefaultAccent, false);
        if (UseOpaquePanel)
            palette = GlassPalette.Create(true, palette.Accent, true);
        Palette = palette;

        if (appearance is not null)
        {
            PanelCornerRadius = new CornerRadius(ResolveCornerRadius(appearance));
        }

        SurfaceMode = UseOpaquePanel ? GlassSurfaceMode.SelfDrawn : ResolveSurface(theme, appearance);
        ApplySurface(palette);
        ApplyPaletteBrushes(palette);
        UpdateGlassClip();
        _shadow?.Update();
    }

    /// <summary>
    /// Khoét bỏ bốn phần khuyết góc bo tròn khỏi vùng có thể vẽ của cửa sổ để chúng trở thành **alpha 0 thực sự**.
    /// </summary>
    /// <remarks>
    /// Đây là **cách giải đúng** cho lỗi nuốt cú nhấp chuột ở góc bo tròn, thay thế cho cách trước đây là «dùng <c>WM_NCHITTEST</c> để cho qua».
    ///
    /// Nguyên nhân: trong phần khuyết góc bo tròn vốn sẽ kết xuất các pixel bán trong suốt của bóng đổ, alpha khác 0, nên các điểm đó bị tính là
    /// một phần của cửa sổ và nuốt mất cú nhấp chuột. Trong khi đó việc chuyển tiếp <c>HTTRANSPARENT</c> chỉ giới hạn trong cùng luồng, sang tiến trình khác là vô hiệu —
    /// đã kiểm chứng thực tế (điểm đối chứng cùng tiến trình xuyên qua được, còn góc bo tròn sang tiến trình khác thì không).
    ///
    /// Kiểm tra trúng đích của cửa sổ phân lớp hoạt động **theo alpha từng pixel**: chỉ cần điểm đó thực sự có alpha 0, hệ thống sẽ
    /// cho cú nhấp chuột đi qua tới bất kỳ cửa sổ nào bên dưới, bất kể thuộc tiến trình nào. Vì vậy cách làm đúng không phải là «bảo hệ thống cho qua»,
    /// mà là «làm cho chỗ đó vốn dĩ trong suốt».
    ///
    /// Cấu tạo hình học: `(Toàn cửa sổ − Khung ngoài bảng điều khiển) ∪ Bảng điều khiển bo góc`. Hiện <see cref="ChromeInset"/> bằng 0,
    /// nên «khung ngoài bảng điều khiển» chính là toàn cửa sổ, hình học suy biến thành «bảng điều khiển bo góc» — ngoài bảng điều khiển tất cả đều là alpha 0.
    /// Nếu sau này khôi phục khoảng lề, biểu thức này sẽ tự động giữ lại luôn phần viền ngoài mà không cần sửa ở đây.
    /// </remarks>
    private void UpdateGlassClip()
    {
        if (_sharedPanelClip is not null) { Clip = _sharedPanelClip; return; }
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            // Bố cục chưa hoàn tất, đợi OnRenderSizeChanged rồi xử lý lại.
            Clip = null;
            return;
        }

        var panelWidth = Math.Max(0, width - ChromeInset.Left - ChromeInset.Right);
        var panelHeight = Math.Max(0, height - ChromeInset.Top - ChromeInset.Bottom);

        if (panelWidth <= 0 || panelHeight <= 0)
        {
            Clip = null;
            return;
        }

        var panelBox = new Rect(ChromeInset.Left, ChromeInset.Top, panelWidth, panelHeight);

        // Lấy bán kính lớn nhất trong bốn góc để thống nhất với logic kiểm tra trúng đích.
        var radius = Math.Max(
            Math.Max(PanelCornerRadius.TopLeft, PanelCornerRadius.TopRight),
            Math.Max(PanelCornerRadius.BottomLeft, PanelCornerRadius.BottomRight));

        radius = Math.Min(radius, Math.Min(panelWidth, panelHeight) / 2);

        var frame = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, width, height)),
            new RectangleGeometry(panelBox));

        var panel = CreatePanelGeometryCore(panelBox, radius);

        var clip = new CombinedGeometry(GeometryCombineMode.Union, frame, panel);
        clip.Freeze();

        Clip = clip;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // Kích thước đã đổi thì hình học khoét vùng cũng phải đổi theo, nếu không vị trí phần khuyết sẽ bị lệch.
        UpdateGlassClip();
    }

    /// <summary>
    /// Quyết định lần này dùng kính quang học thời gian thực hay hạ cấp sang hiển thị trong gọn nhẹ.
    /// </summary>
    /// <remarks>
    /// Khi người dùng yêu cầu giảm độ trong suốt hoặc hệ thống tắt hiệu ứng trong suốt, đi thẳng sang tự vẽ: trong trường hợp đó không nên làm mờ nữa,
    /// bản thân hiệu ứng bán trong suốt chính là thứ người dùng muốn bỏ đi.
    /// </remarks>
    private static GlassSurfaceMode ResolveSurface(ThemeService? theme, AppearanceSettings? appearance)
    {
        if (appearance?.UseSystemBackdrop == false || theme?.UseOpaquePanels == true)
        {
            return GlassSurfaceMode.SelfDrawn;
        }

        return GlassSurfaceMode.OpticalGlass;
    }

    /// <summary>Áp dụng chế độ đã chọn lên cửa sổ.</summary>
    private void ApplySurface(GlassPalette palette)
    {
        var handle = Handle;
        if (handle == 0)
        {
            return;
        }

        if (SurfaceMode == GlassSurfaceMode.Acrylic)
        {
            var applied = WindowAcrylic.TryApply(
                handle,
                ResolveAcrylicTintAlpha(palette),
                palette.AcrylicTint.R,
                palette.AcrylicTint.G,
                palette.AcrylicTint.B);

            // Giao diện chưa công khai nên gọi thất bại nằm trong dự kiến: âm thầm hạ cấp, không để lại trạng thái dở dang.
            if (!applied)
            {
                SurfaceMode = GlassSurfaceMode.SelfDrawn;
            }
        }
        else
        {
            WindowAcrylic.TryRemove(handle);
        }
    }

    /// <summary>Allows a window-specific material to control its native transmission density.</summary>
    protected virtual byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.AcrylicTintAlpha;

    /// <summary>
    /// Chuyển đổi bảng màu thành các cọ vẽ thực tế mà cửa sổ này cần dùng.
    /// </summary>
    /// <remarks>
    /// Việc chọn lớp tô được đặt ở đây vì đây là nơi duy nhất biết đồng thời cả «bảng màu» lẫn «chế độ cuối cùng được áp dụng».
    /// Khi dùng Acrylic thì lớp tô phải gần như trong suốt — sắc thái màu đã do lớp Acrylic cung cấp, chồng thêm một lớp nữa sẽ che mất hiệu ứng làm mờ.
    /// </remarks>
    private void ApplyPaletteBrushes(GlassPalette palette)
    {
        if (UseOpaquePanel)
        {
            var color = palette.FallbackPanelBackground;
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            PanelBackgroundBrush = brush;
        }
        else
            PanelBackgroundBrush = palette.ResolvePanelBrush(SurfaceMode == GlassSurfaceMode.Acrylic);
        PanelBorderBrush = palette.PanelBorderBrush;
        var appearance = ThemeService.Current?.Appearance;
        DockBuiltinIconBrush = MonochromeBrush(appearance?.DockBuiltinIconBrightness ?? 1);
        LaunchpadIconFilled = appearance?.LaunchpadIconFilled ?? true;
        var foreground = UseLauncherTextBrightness
            ? MonochromeBrush(appearance?.LauncherTextBrightness ?? 1) : palette.ForegroundBrush;
        ForegroundBrush = foreground;
        SecondaryForegroundBrush = UseLauncherTextBrightness ? foreground : palette.SecondaryForegroundBrush;
        AccentBrush = palette.AccentBrush;
        Foreground = foreground;
    }

    private static SolidColorBrush MonochromeBrush(double brightness)
    {
        var value = (byte)Math.Round(255 * (double.IsFinite(brightness) ? Math.Clamp(brightness, 0, 1) : 1));
        var brush = new SolidColorBrush(Color.FromRgb(value, value, value));
        brush.Freeze(); return brush;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        ApplyAppearance();
    }
}
