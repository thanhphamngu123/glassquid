using Microsoft.Win32;

namespace Glassquid.Platform;

/// <summary>
/// Đọc lựa chọn giao diện của người dùng ở cấp độ hệ điều hành.
/// </summary>
/// <remarks>
/// Chủ đề "Theo hệ thống" cần các giá trị này, còn cờ trong suốt mang ý nghĩa độc lập: nếu
/// người dùng đã tắt hiệu ứng trong suốt trong Windows, thì dù cài đặt của ứng dụng có thiết lập thế nào
/// cũng không nên vẽ bảng bán trong suốt.
///
/// Mỗi lần đọc đều theo hướng phòng vệ. Việc thiếu giá trị là bình thường — một số phiên bản
/// và hồ sơ người dùng mới tạo không có các khóa này — không được ném ngoại lệ khi khởi động.
/// </remarks>
public static class SystemThemeReader
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Trả về true khi hệ thống đặt chủ đề ứng dụng sáng. Mặc định là true.</summary>
    public static bool IsLightTheme()
    {
        var value = ReadPersonalizeDword("AppsUseLightTheme");
        return value is null or 1;
    }

    /// <summary>
    /// Trả về true khi hiệu ứng trong suốt của Windows được bật. Mặc định là true, giống với
    /// cài đặt mới.
    /// </summary>
    public static bool IsTransparencyEnabled()
    {
        var value = ReadPersonalizeDword("EnableTransparency");
        return value is null or 1;
    }

    private static int? ReadPersonalizeDword(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            var raw = key?.GetValue(name);
            return raw switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}