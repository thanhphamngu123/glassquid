namespace Glassquid.Dock;

/// <summary>
/// Hợp nhất kết quả nhập vào danh sách ghim của Dock.
/// </summary>
/// <remarks>
/// <strong>Các mục trùng lặp cũng phải tham gia vào việc ghim.</strong> Khi kéo một tệp đã có sẵn trong thư viện ứng dụng từ Explorer vào,
/// dịch vụ nhập báo "trùng lặp 0, thêm mới 1" — nếu chỉ nhìn vào mục thêm mới sẽ khiến thao tác không phản hồi gì:
/// người dùng kéo một ứng dụng vừa gỡ khỏi Dock trở lại thì trên giao diện sẽ không có chuyện gì xảy ra
/// (mục vốn đã nằm trong thư viện nên khi nhập đương nhiên không tính là thêm mới).
/// </remarks>
public static class DockPinMerge
{
    /// <summary>
    /// Tính toán danh sách mới sau khi hợp nhất ghim <paramref name="added"/> và <paramref name="duplicates"/>.
    /// </summary>
    /// <param name="pinned">Danh sách ghim hiện tại, giữ nguyên thứ tự.</param>
    /// <param name="added">Id các mục mới được nhập trong lần này.</param>
    /// <param name="duplicates">Id các mục lần này được xác định là "đã có trong thư viện".</param>
    /// <returns>Danh sách mới; các id đã được ghim sẽ không bị thêm trùng lặp.</returns>
    public static List<string> Merge(
        IReadOnlyList<string> pinned,
        IEnumerable<string> added,
        IEnumerable<string> duplicates)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(duplicates);

        var merged = new List<string>(pinned);
        var seen = new HashSet<string>(merged, StringComparer.Ordinal);

        foreach (var id in added.Concat(duplicates))
        {
            if (!string.IsNullOrEmpty(id) && seen.Add(id))
            {
                merged.Add(id);
            }
        }

        return merged;
    }

    /// <summary>Số lượng mục ghim thực sự mới được thêm trong lần hợp nhất này (dùng cho chẩn đoán và văn bản trạng thái).</summary>
    public static int CountNewlyPinned(
        IReadOnlyList<string> pinned,
        IEnumerable<string> added,
        IEnumerable<string> duplicates)
        => Merge(pinned, added, duplicates).Count - pinned.Count;
}