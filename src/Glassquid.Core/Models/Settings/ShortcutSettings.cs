namespace Glassquid.Core.Models.Settings;

/// <summary>
/// Một liên kết phím nóng toàn cục đơn lẻ.
/// </summary>
/// <remarks>
/// Phím được lưu dưới dạng văn bản thay vì giá trị <c>System.Windows.Forms.Keys</c>, giúp tệp luôn dễ đọc,
/// tương thích qua các phiên bản framework và không kéo thêm phụ thuộc UI vào
/// assembly này chỉ vì một phím nóng.
/// </remarks>
public sealed class HotkeyDefinition
{
    public bool Enabled { get; set; }

    public bool Control { get; set; }

    public bool Alt { get; set; }

    public bool Shift { get; set; }

    /// <summary>Phím Windows. Đặt tên là "Win" thay vì "Windows" để tránh xung đột với tên nền tảng.</summary>
    public bool Win { get; set; }

    /// <summary>Tên phím, ví dụ <c>F2</c> hoặc <c>Space</c>. Để trống nghĩa là chưa gán.</summary>
    public string Key { get; set; } = string.Empty;

    public bool IsBound => Enabled && !string.IsNullOrWhiteSpace(Key);

    /// <summary>Dạng dễ đọc để hiển thị trên giao diện cài đặt, ví dụ <c>Ctrl+Shift+Space</c>.</summary>
    public string Describe()
    {
        if (!IsBound)
        {
            return "Chưa thiết lập";
        }

        var parts = new List<string>(5);
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }
}

/// <summary>Góc màn hình có thể kích hoạt Launchpad.</summary>
public enum HotCorner
{
    None = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomLeft = 3,
    BottomRight = 4,
}

/// <summary>Cách thức kích hoạt bằng bàn phím và con trỏ.</summary>
public sealed class ShortcutSettings
{
    public Glassquid.Core.Input.InputAction MouseBackAction { get; set; } = Glassquid.Core.Input.InputAction.ToggleLauncher;

    public Glassquid.Core.Input.InputAction MouseForwardAction { get; set; }

    public HotkeyDefinition ToggleLauncher { get; set; } = new();

    public HotkeyDefinition HideLauncher { get; set; } = new();

    public HotkeyDefinition NextPage { get; set; } = new() { Enabled = true, Key = "WheelDown" };

    public HotkeyDefinition PreviousPage { get; set; } = new() { Enabled = true, Key = "WheelUp" };

    public HotkeyDefinition ToggleDesktopIcons { get; set; } = new();

    /// <summary>Bật/tắt khóa gọi Dock bằng chuột khi Dock bị che khuất; khi bật sẽ chặn gọi hiện, khi tắt cho phép di chuột tới vị trí gốc của Dock để gọi hiện.</summary>
    public HotkeyDefinition ToggleDockRevealLock { get; set; } = new();

    /// <summary>
    /// Góc nóng mặc định tắt. Tính năng này rất dễ bị kích hoạt nhầm nên cần người dùng bật
    /// tường minh mới có hiệu lực.
    /// </summary>
    public bool HotCornersEnabled { get; set; }

    public HotCorner HotCornerTarget { get; set; } = HotCorner.None;

    /// <summary>Thời gian con trỏ cần dừng ở góc để kích hoạt.</summary>
    public int HotCornerDwellMs { get; set; } = 300;

    /// <summary>
    /// Các nút bên hông chuột mặc định tắt; chỉ khi được bật và thao tác nhấn thực sự ánh xạ tới
    /// một hành động của ứng dụng thì mới bị chặn xử lý.
    /// </summary>
    public bool MouseSideButtonsEnabled { get; set; }

    public HotkeyDefinition MouseBackButton { get; set; } = new();

    public HotkeyDefinition MouseForwardButton { get; set; } = new();

    internal void Normalize()
    {
        ToggleLauncher ??= new HotkeyDefinition();
        HideLauncher ??= new HotkeyDefinition();
        NextPage ??= new HotkeyDefinition();
        PreviousPage ??= new HotkeyDefinition();
        ToggleDesktopIcons ??= new HotkeyDefinition();
        ToggleDockRevealLock ??= new HotkeyDefinition();
        MouseBackButton ??= new HotkeyDefinition();
        MouseForwardButton ??= new HotkeyDefinition();
    }
}
