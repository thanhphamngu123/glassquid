using System.Globalization;
using Glassquid.Core.Models;

namespace Glassquid.Core.Storage;

/// <summary>
/// Suy ra định danh dùng để nhận diện các mục trùng lặp khi nhập.
/// </summary>
/// <remarks>
/// Tách biệt có chủ đích với <see cref="AppEntry.Id"/>. Id mang tính mờ đục và vĩnh viễn;
/// còn khóa được tính lại từ đích, nhờ vậy cùng một chương trình được nhập hai lần
/// vẫn có thể được nhận ra, ngay cả khi hai bản ghi được tạo ở các phiên khác nhau
/// và tên hiển thị đã bị đổi ở giữa.
///
/// <para>
/// Phép so sánh đường dẫn trên Windows không phân biệt chữ hoa chữ thường, nên khóa sẽ gộp kiểu chữ.
/// Tham số cũng tham gia cấu thành khóa, vì hai lối tắt trỏ đến cùng một tệp thực thi nhưng có tham số khác nhau
/// thực sự là hai thao tác khởi chạy khác nhau.
/// </para>
///
/// <para>
/// <b>Khóa chỉ được suy ra trong tiến trình hiện tại, không bao giờ ghi ra tệp.</b> Mỗi lần tải đều tính lại
/// bằng thuật toán hiện hành (xem <see cref="AppLibraryStore.RebuildIndexes"/>),
/// vì vậy việc thay đổi thuật toán không cần di trú dữ liệu hiện có.
/// </para>
/// </remarks>
public static class AppEntryKey
{
    public static string Compute(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var target = NormalizeTarget(entry.Target, entry.Kind);
        var arguments = (entry.Arguments ?? string.Empty).Trim();
        return arguments.Length == 0 ? target : $"{target}\u0000{arguments}";
    }

    private static string NormalizeTarget(string target, AppItemKind kind)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        var trimmed = target.Trim();

        if (kind == AppItemKind.Url)
        {
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                ? NormalizeUrl(uri)
                : trimmed.ToLowerInvariant();
        }

        try
        {
            // Gộp "..", dấu gạch chéo thuận và tên ngắn thành một dạng chuẩn.
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Không phải đường dẫn hợp lệ. Quay về so sánh theo dạng đã cắt khoảng trắng đầu cuối,
            // để việc nhập vẫn trả về kết quả hợp lý thay vì ném ngoại lệ.
            return trimmed.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Gộp một địa chỉ tuyệt đối thành dạng có thể so sánh được.
    /// </summary>
    /// <remarks>
    /// Giao thức và máy chủ được gộp chữ hoa/thường; đường dẫn giữ nguyên vì đường dẫn URL phân biệt hoa thường.
    ///
    /// <para>
    /// <b>Cổng bắt buộc phải giữ lại.</b> Từng dùng <see cref="Uri.Host"/> để gộp địa chỉ, nhưng nó không chứa cổng,
    /// dẫn đến <c>http://localhost:8001/app</c> và <c>http://localhost:8002/app</c>
    /// bị coi là cùng một mục — đó là hai dịch vụ khác nhau, và mục thứ hai sẽ bị coi là trùng lặp rồi bị từ chối nhập.
    /// </para>
    ///
    /// <para>
    /// Chỉ "cổng mặc định được ghi rõ" và "lược bỏ cổng" mới được coi là giống nhau:
    /// <c>https://example.com/</c> và <c>https://example.com:443/</c> là hai cách viết
    /// của cùng một địa chỉ, không nên bị tính thành hai mục.
    /// </para>
    ///
    /// <para>
    /// Cố ý <strong>không</strong> đưa userinfo (<c>user@host</c>) vào khóa: trong Launchpad hầu như không xuất hiện
    /// địa chỉ có kèm thông tin xác thực, và việc gộp nó vào chỉ là giả định không có nhu cầu thực tế. Cùng một địa chỉ
    /// viết userinfo khác hoa thường sẽ bị coi là hai mục — dư một bản ghi an toàn hơn là âm thầm gộp mất một mục.
    /// </para>
    /// </remarks>
    private static string NormalizeUrl(Uri uri)
    {
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port.ToString(CultureInfo.InvariantCulture)}";

        return $"{scheme}://{host}{port}{uri.PathAndQuery}{uri.Fragment}";
    }
}