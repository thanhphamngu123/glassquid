using System.Text;

namespace Glassquid.AppServices;

/// <summary>Đọc tệp <c>.url</c> (lối tắt Internet).</summary>
/// <remarks>
/// Đây là định dạng INI văn bản thuần túy, có nội dung dạng:
/// <code>
/// [InternetShortcut]
/// URL=https://example.com/
/// </code>
///
/// <para>
/// Ý nghĩa tồn tại của nó là cung cấp dạng tệp cho "lối tắt trang web" — khi người dùng kéo địa chỉ từ trình duyệt ra màn hình nền,
/// thứ nhận được chính là tệp này chứ không phải <c>.lnk</c>. Nếu không nhận diện nó,
/// đối tượng được kéo từ trình duyệt sẽ bị coi là một tệp thông thường.
/// </para>
///
/// <para>
/// Tự phân tích thay vì gọi <c>GetPrivateProfileString</c>: hàm đó hỗ trợ UTF-8 không rõ ràng,
/// trong khi bảng mã của các tệp này không đồng nhất (thực tế có cả ANSI lẫn UTF-8).
/// </para>
/// </remarks>
public static class InternetShortcutReader
{
    private const string UrlKey = "URL=";

    /// <summary>Đọc địa chỉ bên trong tệp <c>.url</c>; trả về null nếu không đọc được địa chỉ hợp lệ.</summary>
    public static string? ReadUrl(string path)
    {
        string[] lines;

        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (!line.StartsWith(UrlKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[UrlKey.Length..].Trim();
            return IsSupportedUrl(value) ? value : null;
        }

        return null;
    }

    /// <summary>Kiểm tra địa chỉ này có phải là "trang web" được hỗ trợ hay không.</summary>
    /// <remarks>
    /// Chỉ chấp nhận http và https. Những giao thức như <c>file:</c>, <c>mailto:</c>, <c>steam:</c> tuy cũng là URI hợp lệ,
    /// nhưng không phải trang web; nếu trộn vào sẽ làm mất ý nghĩa của nhóm "trang web" —
    /// hơn nữa chúng cần được xử lý bởi ứng dụng liên kết chứ không nên gắn nhãn là mục trình duyệt.
    /// </remarks>
    public static bool IsSupportedUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>Tạo nội dung tệp <c>.url</c> từ một URL để ghi xuống đĩa khi nhập trang web.</summary>
    public static string Compose(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var builder = new StringBuilder();
        builder.AppendLine("[InternetShortcut]");
        builder.Append("URL=").AppendLine(url.Trim());
        return builder.ToString();
    }
}