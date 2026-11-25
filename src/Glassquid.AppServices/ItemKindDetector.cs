using Glassquid.Core.Models;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices;

/// <summary>Nhận diện một mục là loại gì và nên hiển thị tên như thế nào.</summary>
/// <remarks>
/// Tách riêng ra vì cả quá trình nhập và quét menu Start đều phải dùng cùng một bộ quy tắc phán đoán,
/// nếu hai nơi tự viết riêng thì cùng một tệp đi từ menu vào và đi từ kéo thả vào
/// sẽ trở thành hai bản ghi khác nhau.
/// </remarks>
public static class ItemKindDetector
{
    /// <summary>Các loại phần mở rộng được xử lý như chương trình thực thi.</summary>
    /// <remarks>
    /// Chỉ nhận các phần mở rộng thực sự “khởi chạy một chương trình”. Cố ý không tính <c>.msi</c>:
    /// nó khởi chạy trình cài đặt, gắn nhãn là chương trình sẽ khiến tính năng “kích hoạt nếu đang chạy” khớp nhầm tiến trình cài đặt.
    /// </remarks>
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1",
    };

    /// <summary>Lối tắt (shortcut) của Windows.</summary>
    public const string ShortcutExtension = ".lnk";

    /// <summary>Lối tắt Internet (tệp văn bản thuần <c>.url</c>).</summary>
    public const string InternetShortcutExtension = ".url";

    /// <summary>
    /// Xác định một đường dẫn thuộc loại nào.
    /// </summary>
    /// <remarks>
    /// Thư mục được ưu tiên hơn phần mở rộng——một thư mục tên <c>CongCu.exe</c> vẫn là thư mục.
    /// </remarks>
    public static AppItemKind Detect(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return AppItemKind.Folder;
        }

        var extension = Path.GetExtension(path);

        if (string.Equals(extension, ShortcutExtension, StringComparison.OrdinalIgnoreCase))
        {
            return AppItemKind.Shortcut;
        }

        if (string.Equals(extension, InternetShortcutExtension, StringComparison.OrdinalIgnoreCase))
        {
            return AppItemKind.Url;
        }

        return ProgramExtensions.Contains(extension) ? AppItemKind.Program : AppItemKind.File;
    }

    /// <summary>
    /// Suy ra tên hiển thị mà người dùng nhìn thấy.
    /// </summary>
    /// <remarks>
    /// Chương trình và lối tắt bỏ phần mở rộng: người dùng nhận diện “Notepad”, không phải “notepad.exe”.
    /// Tài liệu và thư mục giữ nguyên tên đầy đủ: phần mở rộng trong <c>bao-cao-quy.xlsx</c> tự thân đã là thông tin hữu ích,
    /// bỏ đi ngược lại sẽ khó nhận ra.
    /// </remarks>
    public static string DeriveDisplayName(string path, AppItemKind kind)
    {
        if (kind is AppItemKind.Program or AppItemKind.Shortcut or AppItemKind.Url or AppItemKind.Folder)
            return ShellDisplayNames.GetFileName(path, removeExtension: kind != AppItemKind.Folder);

        // Tài liệu luôn giữ nguyên phần mở rộng, không bị ảnh hưởng bởi thiết lập ẩn phần mở rộng của Explorer.
        var name = Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(name))
        {
            // Thư mục gốc (như C:\) không có phần tên tệp, lùi về dùng đường dẫn đầy đủ vẫn tốt hơn là tên rỗng.
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return name;
    }
}
