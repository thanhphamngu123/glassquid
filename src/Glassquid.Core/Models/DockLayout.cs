namespace Glassquid.Core.Models;

/// <summary>
/// Bố cục sắp xếp trên Dock. Được lưu tách biệt với thư viện ứng dụng, nhờ đó việc sắp xếp lại
/// Dock sẽ không bao giờ làm hỏng <see cref="AppEntry"/>.
/// </summary>
public sealed class DockLayout
{
    /// <summary>
    /// Danh sách id mục thư viện ứng dụng hiển thị trên Dock, sắp xếp theo thứ tự. Các id không còn
    /// tồn tại trong thư viện là mục lỗi thời, cần được bỏ qua thay vì coi là lỗi — người dùng có thể đã
    /// xóa ứng dụng đó và Dock chỉ cần thu gọn tương ứng.
    /// </summary>
    public List<string> PinnedItemIds { get; set; } = [];
}