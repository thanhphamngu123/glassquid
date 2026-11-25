namespace Glassquid.Dock;

/// <summary>
/// Bên quyết định duy nhất về “những gì được ghi và không được ghi” trong bố cục Dock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Các lối vào tích hợp sẵn không phải mục của thư viện ứng dụng, tuyệt đối không được đưa vào tệp bố cục.</b>
/// Ba ô Launcher, Hiện màn hình nền và Thùng rác thuộc về bản thân Dock, luôn tồn tại mọi lúc và vị trí cũng do Dock quyết định;
/// còn <c>PinnedItemIds</c> của <c>dock-layout.json</c> ghi lại “người dùng đã ghim những ứng dụng nào và theo thứ tự nào”.
/// Cách sắp xếp trước đây ghi nguyên cả hàng ô (bao gồm ba lối vào) trở lại tệp bố cục, khiến tệp bị lẫn vào
/// các id giả như <c>entry:launcher</c>——lần tải sau chúng không tìm thấy mục tương ứng nên bị bỏ qua,
/// nhìn qua tưởng “không sao”, nhưng tệp bố cục không còn biểu đạt đúng thứ nó cần biểu đạt nữa, ngữ nghĩa khử trùng lặp và thứ tự đều bị nhiễu.
/// </para>
/// <para>
/// Ba lối vào tích hợp sẵn được đánh dấu bằng tiền tố cố định (<see cref="EntryTilePrefix"/>), tự nhiên không xung đột với id mục thư viện.
/// </para>
/// </remarks>
public static class DockLayoutPolicy
{
    /// <summary>Tiền tố id của các ô lối vào tích hợp sẵn. Chúng không phải mục của thư viện ứng dụng.</summary>
    public const string EntryTilePrefix = "entry:";

    /// <summary>Id này có phải là ô lối vào tích hợp sẵn hay không.</summary>
    public static bool IsEntryTile(string? itemId)
        => itemId is not null && itemId.StartsWith(EntryTilePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Làm sạch danh sách ghim thành dạng có thể ghi vào bố cục: **chỉ giữ lại các mục thật trong thư viện ứng dụng**,
    /// loại bỏ lối vào tích hợp sẵn và các id không tìm thấy, khử trùng lặp và giữ nguyên thứ tự ban đầu.
    /// </summary>
    /// <param name="pinned">Danh sách ghim cần làm sạch.</param>
    /// <param name="isLibraryEntry">
    /// Kiểm tra xem một id có tồn tại trong thư viện ứng dụng hay không. Bên gọi nên bỏ qua bước làm sạch khi thư viện **chưa được tải**
    /// (truyền hàm luôn trả về true hoặc không gọi), nếu không toàn bộ bố cục sẽ bị coi là id lạ và bị xóa sạch.
    /// </param>
    /// <returns>Danh sách sau khi làm sạch; nếu tương đương với đầu vào thì nội dung danh sách trả về giống hệt (bên gọi dựa vào đó để quyết định có ghi xuống đĩa không).</returns>
    public static List<string> Sanitize(IEnumerable<string> pinned, Func<string, bool> isLibraryEntry)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(isLibraryEntry);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in pinned)
        {
            if (string.IsNullOrWhiteSpace(id) || IsEntryTile(id))
            {
                continue;
            }

            if (!isLibraryEntry(id) || !seen.Add(id))
            {
                continue;
            }

            result.Add(id);
        }

        return result;
    }

    /// <summary>Nội dung hai danh sách ghim có hoàn toàn khớp nhau hay không (phân biệt thứ tự).</summary>
    public static bool SameOrder(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}