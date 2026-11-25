namespace Glassquid.Core.Models.Settings;

/// <summary>Hành vi lưới, cửa sổ và tìm kiếm của Trình khởi chạy.</summary>
public sealed class LauncherSettings
{
    /// <summary>Số hàng lưới trên mỗi trang.</summary>
    public int Rows { get; set; } = 5;

    /// <summary>Số cột lưới trên mỗi trang.</summary>
    public int Columns { get; set; } = 7;

    /// <summary>
    /// Tỷ lệ chiếm vùng làm việc ở chế độ cửa sổ. Đặc tả yêu cầu khoảng
    /// hai phần ba, căn giữa.
    /// </summary>
    public double WindowWorkAreaRatio { get; set; } = 0.66;

    /// <summary>Phủ kín toàn bộ vùng làm việc thay vì hiển thị bảng căn giữa.</summary>
    public bool FullscreenMode { get; set; }

    /// <summary>
    /// Khi bật, đặt con trỏ vào ô tìm kiếm mỗi khi Trình khởi chạy mở. Mặc định tắt.
    /// </summary>
    public bool FocusSearchOnOpen { get; set; }

    /// <summary>
    /// Khôi phục trang đã dừng lại lần trước khi mở lại; nếu tắt thì luôn bắt đầu từ trang đầu tiên.
    /// </summary>
    public bool RememberLastPage { get; set; } = true;
}
