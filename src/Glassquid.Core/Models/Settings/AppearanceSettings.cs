namespace Glassquid.Core.Models.Settings;

/// <summary>
/// Thiết lập giao diện. Áp dụng đồng thời cho Dock, Launchpad và trung tâm cài đặt —
/// <c>ApplyAppearance</c> phải làm mới tất cả các cửa sổ đang mở từ cùng một nguồn.
/// </summary>
public sealed class AppearanceSettings
{
    public const double DefaultGlassTintOpacity = .16;
    public const double DefaultGlassBackgroundClarity = .5;
    public const double DefaultDockGlassTransparency = .9;
    public const double DefaultLauncherGlassTransparency = .2;

    public double DockGlassTransparency { get; set; } = DefaultDockGlassTransparency;
    public double LauncherGlassTransparency { get; set; } = DefaultLauncherGlassTransparency;

    /// <summary>Độ sáng tối của ba biểu tượng chức năng cố định trên Dock, 0 là màu đen, 1 là màu trắng.</summary>
    public double DockBuiltinIconBrightness { get; set; } = 1;

    /// <summary>Lưới 9 ô của Launchpad dùng khối đặc; false khôi phục viền rỗng ban đầu.</summary>
    public bool LaunchpadIconFilled { get; set; } = true;

    /// <summary>Độ sáng tối của chữ trên Launchpad, 0 là màu đen, 1 là màu trắng.</summary>
    public double LauncherTextBrightness { get; set; } = 1;

    /// <summary>Legacy shared value retained for older settings files.</summary>
    public double GlassBackgroundClarity { get; set; } = DefaultGlassBackgroundClarity;

    /// <summary>Only the glass body veil; never multiplies refraction, lighting or layer alpha.</summary>
    public double GlassTintOpacity { get; set; } = DefaultGlassTintOpacity;

    /// <summary>Shared liquid glass material; legacy selections normalize on load and save.</summary>
    public GlassMaterialMode GlassMaterial { get; set; } = GlassMaterialMode.AndroidLiquidGlass;

    /// <summary>
    /// Cố định là kính lỏng tối màu; giá trị chủ đề phiên bản cũ được chuẩn hóa về tối màu khi tải và lưu.
    /// </summary>
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    /// <summary>Màu nhấn, định dạng <c>#RRGGBB</c>. Mặc định dùng màu xanh dương theo yêu cầu thiết kế.</summary>
    public string AccentColor { get; set; } = "#0A84FF";

    /// <summary>
    /// Ưu tiên sử dụng chất liệu nền DWM của Windows 11 thay vì bảng bán trong suốt tự vẽ.
    /// Bị bỏ qua trên các phiên bản hệ thống không cung cấp API này; xem <c>WindowsCapabilities</c>.
    /// </summary>
    public bool UseSystemBackdrop { get; set; } = true;

    /// <summary>Giảm hoặc tắt hoạt ảnh chuyển tiếp. Yêu cầu cơ bản về trợ năng.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>
    /// Giảm độ trong suốt của bảng nền cho người dùng thấy bán trong suốt khó đọc, đồng thời xử lý trường hợp hệ thống
    /// đã tắt hiệu ứng trong suốt.
    /// </summary>
    public bool ReduceTransparency { get; set; }

    /// <summary>Bán kính bo góc của Dock, đơn vị là DIP.</summary>
    public double DockCornerRadius { get; set; } = 40;

    /// <summary>Bán kính bo góc của bảng Launchpad, đơn vị là DIP.</summary>
    public double LauncherCornerRadius { get; set; } = 80;
}
