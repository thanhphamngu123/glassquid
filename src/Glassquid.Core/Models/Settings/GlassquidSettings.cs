namespace Glassquid.Core.Models.Settings;

/// <summary>
/// Gốc chứa nội dung người dùng có thể cấu hình, được lưu bền vững thành một tài liệu JSON duy nhất.
/// </summary>
/// <remarks>
/// Đây là mô hình cài đặt riêng của chúng ta. Nó không bao giờ đọc hoặc ghi cấu hình của sản phẩm tham chiếu
/// (<c>%APPDATA%\MacDock\settings.json</c> của MacDock hoặc localStorage của Launchpad),
/// giữ nguyên các cấu hình đó để chương trình cũ vẫn tiếp tục hoạt động bình thường.
/// </remarks>
public sealed class GlassquidSettings
{
    /// <summary>
    /// Phiên bản schema của tài liệu trên đĩa. Mỗi khi có thay đổi không thể tự xử lý
    /// chỉ bằng giá trị mặc định thì tăng số này lên; tệp cũ sẽ được di chuyển dữ liệu khi tải.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AppearanceSettings Appearance { get; set; } = new();

    public DockSettings Dock { get; set; } = new();

    public LauncherSettings Launcher { get; set; } = new();

    public ShortcutSettings Shortcuts { get; set; } = new();

    public DisplaySettings Display { get; set; } = new();

    public PerformanceSettings Performance { get; set; } = new();

    public DesktopTaskbarSettings DesktopTaskbar { get; set; } = new();

    /// <summary>
    /// Thay thế các phần bị null bằng giá trị mặc định. Khi giải tuần tự hóa một tệp bị sửa thủ công
    /// hoặc ghi dở dang, một phần nào đó có thể là null dù khai báo kiểu không cho phép;
    /// nếu không làm vậy thì mọi nơi sử dụng đều phải tự kiểm tra null.
    /// </summary>
    public void Normalize()
    {
        Appearance ??= new AppearanceSettings();
        Dock ??= new DockSettings();
        Launcher ??= new LauncherSettings();
        Shortcuts ??= new ShortcutSettings();
        Display ??= new DisplaySettings();
        Performance ??= new PerformanceSettings();
        DesktopTaskbar ??= new DesktopTaskbarSettings();
        Dock.EdgeRevealEnabled = false;
        Launcher.FullscreenMode = false;
        Shortcuts.HotCornersEnabled = false;
        Shortcuts.MouseSideButtonsEnabled = false;

        // Keep legacy enum values readable, but product settings have one appearance.
        Appearance.Theme = ThemeMode.Dark;
        if (Appearance.GlassMaterial != GlassMaterialMode.FrostedGlass)
            Appearance.GlassMaterial = GlassMaterialMode.AndroidLiquidGlass;
        Dock.SizeScale = double.IsFinite(Dock.SizeScale)
            ? Math.Clamp(Dock.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale) : 1;
        // Settings now owns its opaque surface instead of changing every window.
        Appearance.ReduceTransparency = false;
        Appearance.DockGlassTransparency = double.IsFinite(Appearance.DockGlassTransparency)
            ? Math.Clamp(Appearance.DockGlassTransparency, 0, 1) : AppearanceSettings.DefaultDockGlassTransparency;
        Appearance.LauncherGlassTransparency = double.IsFinite(Appearance.LauncherGlassTransparency)
            ? Math.Clamp(Appearance.LauncherGlassTransparency, 0, 1) : AppearanceSettings.DefaultLauncherGlassTransparency;
        Appearance.DockBuiltinIconBrightness = double.IsFinite(Appearance.DockBuiltinIconBrightness)
            ? Math.Clamp(Appearance.DockBuiltinIconBrightness, 0, 1) : 1;
        Appearance.LauncherTextBrightness = double.IsFinite(Appearance.LauncherTextBrightness)
            ? Math.Clamp(Appearance.LauncherTextBrightness, 0, 1) : 1;
        Appearance.GlassTintOpacity = double.IsFinite(Appearance.GlassTintOpacity)
            ? Math.Clamp(Appearance.GlassTintOpacity, 0, 1) : AppearanceSettings.DefaultGlassTintOpacity;
        Appearance.GlassBackgroundClarity = double.IsFinite(Appearance.GlassBackgroundClarity)
            ? Math.Clamp(Appearance.GlassBackgroundClarity, 0, 1) : AppearanceSettings.DefaultGlassBackgroundClarity;

        Shortcuts.Normalize();
        DesktopTaskbar.Normalize();
    }
}
