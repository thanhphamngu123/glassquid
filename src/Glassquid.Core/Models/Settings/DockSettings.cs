namespace Glassquid.Core.Models.Settings;

/// <summary>Vị trí neo của Dock và cách nó hiển thị.</summary>
public enum DockPlacement
{
    BottomCenter = 0,
    BottomLeft = 1,
    BottomRight = 2,
    TopCenter = 3,
}

public sealed class DockSettings
{
    public const double FixedIconSize = 36;
    public const double FixedIconSpacing = 10;
    public const double FixedMagnificationPeak = 1.15;
    public const double MinimumFrameLengthDip = 180;
    public const double MaximumFrameLengthDip = 10000;
    public const double MinimumSizeScale = 2d / 3;
    public const double MaximumSizeScale = 1.5;

    /// <summary>Hệ số kích thước tổng thể, điều chỉnh đồng thời biểu tượng, khoảng cách và độ dày thanh. Cấu hình cũ mặc định giữ nguyên kích thước gốc.</summary>
    public double SizeScale { get; set; } = 1;

    /// <summary>Các mục tích hợp cố định ở bên trái ứng dụng; cấu hình cũ mặc định hiển thị toàn bộ các mục.</summary>
    public bool ShowLauncherEntry { get; set; } = true;
    public bool ShowThisPcEntry { get; set; } = true;
    public bool ShowDesktopIconsEntry { get; set; } = true;

    /// <summary>
    /// Chiều dài theo phương ngang của khung kính ngoài Dock, đơn vị là DIP. Giá trị 0 nghĩa là tiếp tục tự điều chỉnh theo nội dung,
    /// số dương nghĩa là cố định chiều dài khung ngoài; kích thước biểu tượng và độ dày Dock không bị ảnh hưởng bởi giá trị này.
    /// </summary>
    public double FrameLengthDip { get; set; }

    /// <summary>Kích thước cạnh biểu tượng, đơn vị là DIP.</summary>
    public double IconSize { get; set; } = FixedIconSize;

    /// <summary>Khoảng cách giữa các biểu tượng, đơn vị là DIP.</summary>
    public double IconSpacing { get; set; } = FixedIconSpacing;

    /// <summary>Mức phóng đại cực đại tại vị trí con trỏ, tính theo bội số của <see cref="IconSize"/>.</summary>
    public double MagnificationPeak { get; set; } = 1.35;

    public DockPlacement Placement { get; set; } = DockPlacement.BottomCenter;

    /// <summary>Vị trí thanh Dock khay hệ thống: false = Bên phải (mặc định), true = Bên trái.</summary>
    public bool SystemTrayOnLeft { get; set; }

    /// <summary>Độ tinh chỉnh theo chiều ngang so với điểm neo, đơn vị là DIP.</summary>
    public double OffsetX { get; set; }

    /// <summary>Độ tinh chỉnh theo chiều dọc so với điểm neo, đơn vị là DIP.</summary>
    public double OffsetY { get; set; }

    /// <summary>Tự động thu gọn khi bình thường; hiện ra khi di chuột đến vị trí gốc của Dock và thu gọn trễ sau khi rời khỏi.</summary>
    public bool AutoHide { get; set; }

    /// <summary>
    /// Thời gian đệm trước khi Dock tự động ẩn thu gọn lại. Nếu không có khoảng này, Dock
    /// sẽ nhấp nháy qua lại khi con trỏ lướt qua cạnh; phương án yêu cầu khoảng 300 ms.
    /// </summary>
    public int AutoHideLeaveDelayMs { get; set; } = 300;

    /// <summary>Đẩy con trỏ chạm cạnh màn hình để gọi Dock hiện ra.</summary>
    public bool EdgeRevealEnabled { get; set; }

    /// <summary>Độ dày của dải kích hoạt cạnh màn hình, đơn vị là DIP.</summary>
    public int EdgeHotzoneSize { get; set; } = 14;

    /// <summary>
    /// Khi cửa sổ khác che khuất vùng Dock, việc di chuột vào vị trí gốc của Dock là thao tác di chuột chủ đích nên vẫn gọi Dock hiện ra.
    /// Trường thiết lập gốc này được giữ lại để tương thích với cấu hình hiện có; khóa gọi cạnh tường minh vẫn có mức ưu tiên cao nhất.
    /// </summary>
    public bool BlockRevealWhenCovered { get; set; } = true;

    /// <summary>
    /// Trường tương thích của bộ chọn màn hình Dock phiên bản cũ. Dock thực tế luôn sử dụng Primary Monitor tại thời điểm tiến trình khởi chạy,
    /// và không đọc trường này để định vị.
    /// </summary>
    public string? TargetDisplayId { get; set; }

    /// <summary>Hiển thị chấm tròn bên dưới biểu tượng có cửa sổ đang chạy.</summary>
    public bool ShowRunningIndicator { get; set; } = true;
}
