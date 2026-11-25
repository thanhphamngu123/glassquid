namespace Glassquid.Core.Models;

/// <summary>
/// Tài liệu thư viện ứng dụng: toàn bộ mục do người dùng nhập, dùng chung giữa Dock và
/// Trình khởi chạy.
/// </summary>
/// <remarks>
/// Bọc trong đối tượng thay vì lưu trực tiếp thành mảng trần, để sau này thêm trường
/// (chỉ mục, bộ đếm sửa đổi) không làm hỏng các tệp hiện có.
/// </remarks>
public sealed class AppLibraryData
{
    public int SchemaVersion { get; set; } = 1;

    public List<AppEntry> Items { get; set; } = [];
}

/// <summary>
/// Mô tả gói sao lưu do Trung tâm cài đặt xuất ra, để khi khôi phục có thể xác nhận nó
/// đến từ sản phẩm này và biết được phiên bản lược đồ đang dùng.
/// </summary>
public sealed class BackupManifest
{
    public string Product { get; set; } = "Glassquid";

    public int SchemaVersion { get; set; } = Glassquid.Core.Models.Settings.GlassquidSettings.CurrentSchemaVersion;

    /// <summary>Phiên bản sản phẩm đã tạo gói sao lưu này.</summary>
    public string? ProductVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Tên các tệp bên trong gói sao lưu, tính tương đối theo thư mục gốc của gói.</summary>
    public List<string> Files { get; set; } = [];
}