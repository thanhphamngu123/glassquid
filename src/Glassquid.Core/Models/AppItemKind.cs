namespace Glassquid.Core.Models;

/// <summary>
/// Mục trong thư viện ứng dụng trỏ tới đối tượng nào.
/// </summary>
public enum AppItemKind
{
    /// <summary>Tệp thực thi.</summary>
    Program = 0,

    /// <summary>Lối tắt Windows Shell (.lnk).</summary>
    Shortcut = 1,

    /// <summary>Thư mục.</summary>
    Folder = 2,

    /// <summary>Tài liệu hoặc tệp khác được mở thông qua liên kết phần mở rộng của hệ thống.</summary>
    File = 3,

    /// <summary>Địa chỉ http/https được mở trong trình duyệt mặc định.</summary>
    Url = 4,
}