using System.IO;
using System.Text.Json.Nodes;

namespace Glassquid.DesktopAcceptance;

/// <summary>Góc nhìn chỉ đọc cho một bản ghi trong thư viện.</summary>
internal sealed record LibraryEntry(string Id, string DisplayName, string Target, bool IsTargetMissing);

/// <summary>
/// Đọc trực tiếp tệp dữ liệu do sản phẩm ghi ra.
/// </summary>
/// <remarks>
/// Đây là nơi duy nhất mà trình điều khiển “nhìn vào bên trong sản phẩm”, và thứ nó xem là tài liệu công khai đã ghi xuống đĩa, không phải trạng thái bộ nhớ:
/// tên trường của <c>apps.json</c> và <c>dock-layout.json</c> do chính sản phẩm khai báo
/// (<c>AppLibraryData</c> / <c>DockLayout</c>, camelCase).
/// Nhờ vậy trình điều khiển không cần tham chiếu mã nguồn sản phẩm mà vẫn đối chiếu độc lập được “sản phẩm thực sự đã ghi những gì”.
/// </remarks>
internal static class ProductLibrary
{
    /// <summary>Thư mục dữ liệu thật của sản phẩm.</summary>
    internal static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Glassquid");

    internal static string AppsFile(string root) => Path.Combine(root, "apps.json");

    internal static string DockLayoutFile(string root) => Path.Combine(root, "dock-layout.json");

    /// <summary>
    /// Thư mục dữ liệu này đã có sẵn thư viện ứng dụng thật hay chưa.
    /// </summary>
    /// <remarks>
    /// Kiểm thử kéo thả <strong>sẽ ghi dữ liệu vào thư viện ứng dụng</strong>. Khi thư mục dữ liệu của người dùng thật đã có mục,
    /// thà chặn cả lượt kiểm thử còn hơn thêm một bản ghi kiểm thử vào thư viện của người khác.
    /// </remarks>

    internal static IReadOnlyList<LibraryEntry> ReadEntries(string root)
    {
        var file = AppsFile(root);
        if (!File.Exists(file))
        {
            return [];
        }

        var document = JsonNode.Parse(File.ReadAllText(file));
        var items = document?["items"] as JsonArray;
        if (items is null)
        {
            return [];
        }

        var entries = new List<LibraryEntry>();

        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            entries.Add(new LibraryEntry(
                item["id"]?.GetValue<string>() ?? string.Empty,
                item["displayName"]?.GetValue<string>() ?? string.Empty,
                item["target"]?.GetValue<string>() ?? string.Empty,
                item["isTargetMissing"]?.GetValue<bool>() ?? false));
        }

        return entries;
    }

    internal static IReadOnlyList<string> ReadPinned(string root)
    {
        var file = DockLayoutFile(root);
        if (!File.Exists(file))
        {
            return [];
        }

        var document = JsonNode.Parse(File.ReadAllText(file));
        var pinned = document?["pinnedItemIds"] as JsonArray;

        return pinned is null
            ? []
            : [.. pinned.Select(node => node?.GetValue<string>() ?? string.Empty)];
    }

    /// <summary>
    /// Những mục trong thư viện <strong>không phải do trình điều khiển này tạo ra</strong>.
    /// </summary>
    /// <remarks>
    /// Hàng rào bảo vệ ban đầu chỉ kiểm tra “thư viện có trống không”, qua thử nghiệm thấy quá thô: lượt thứ hai của cùng trình điều khiển sẽ bị chặn bởi các mục
    /// do lượt thứ nhất để lại. Mục đích thực sự của hàng rào là “tuyệt đối không ghi vào dữ liệu thật của người dùng”, nên tiêu chí đổi thành kiểm tra đường dẫn đích của mục
    /// nằm ở đâu——trình điều khiển này chỉ tạo tệp trong thư mục làm việc tạm của chính nó (<c>%TEMP%\Glassquid-DesktopDriver-*</c>),
    /// mục có đích ở nơi khác đều được coi là dữ liệu thật của người dùng.
    /// </remarks>
    internal static IReadOnlyList<LibraryEntry> ForeignEntries(string root, string allowedTargetPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedTargetPrefix);

        return
        [
            .. ReadEntries(root).Where(entry =>
                !entry.Target.StartsWith(allowedTargetPrefix, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>Trạng thái thư mục và tệp dữ liệu của sản phẩm tại thời điểm này, dùng để ghi vào bằng chứng.</summary>
    internal static string Describe(string root)
    {
        var entries = ReadEntries(root);
        var pinned = ReadPinned(root);

        return $"Thư mục dữ liệu {root}; thư viện {entries.Count} mục; ghim {pinned.Count} mục"
            + $"; apps.json {(File.Exists(AppsFile(root)) ? "tồn tại" : "không tồn tại")}";
    }
}