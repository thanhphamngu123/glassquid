namespace Glassquid.Core.Models.Settings;

/// <summary>Hành vi liên quan đến màn hình và DPI.</summary>
public sealed class DisplaySettings
{
    /// <summary>
    /// Trường tương thích cho bộ chọn màn hình Dock phiên bản cũ. Từ Chapter 11 trở đi, Dock bản chính thức luôn dùng
    /// Primary Monitor do Windows chọn khi khởi động tiến trình, trường này không còn tham gia vào việc đặt vị trí cửa sổ.
    /// </summary>
    public string? DockDisplayId { get; set; }

    /// <summary>
    /// Trường tương thích cho thiết lập di chuyển theo con trỏ giữa các màn hình phiên bản cũ. Launcher bản chính thức luôn dùng Primary Monitor được gắn khi khởi động tiến trình.
    /// </summary>
    public bool LauncherFollowsPointer { get; set; } = true;

    /// <summary>
    /// Khi có ứng dụng toàn màn hình chạy ở tiền cảnh, tạm tắt kích hoạt góc nóng, cạnh màn hình và nút bên chuột,
    /// để chúng không làm gián đoạn trò chơi hoặc video.
    /// </summary>
    public bool SuppressTriggersWhenFullscreen { get; set; } = true;
}

/// <summary>
/// Biểu tượng màn hình nền và thanh tác vụ.
/// </summary>
/// <remarks>
/// Mọi thứ ở đây mặc định đều theo nguyên tắc "không làm phiền Windows". Tài liệu kế hoạch yêu cầu rõ:
/// trừ khi người dùng chủ động yêu cầu, tuyệt đối không chỉnh sửa thanh tác vụ; tính năng chặn kích hoạt ở cạnh thuộc diện
/// thử nghiệm, mặc định giữ tắt.
/// </remarks>
public sealed class DesktopTaskbarSettings
{
    public HotkeyDefinition ToggleDesktopIcons { get; set; } = new();

    /// <summary>Temporarily hide Windows taskbar windows while Glassquid is running.</summary>
    public bool HideWindowsTaskbar { get; set; }

    /// <summary>
    /// Tính năng thử nghiệm. Trước khi cung cấp trên một phiên bản Windows nào đó, cần kiểm chứng theo từng phiên bản;
    /// phiên bản chưa biết phải báo là không hỗ trợ, thay vì phỏng đoán.
    /// </summary>
    public bool BlockTaskbarEdgeReveal { get; set; }

    internal void Normalize()
    {
        ToggleDesktopIcons ??= new HotkeyDefinition();
    }
}
