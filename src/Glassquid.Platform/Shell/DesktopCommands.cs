using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>
/// Thao tác cửa sổ hàng loạt cấp màn hình nền: "Hiển thị màn hình nền" (D-13).
/// </summary>
/// <remarks>
/// Cài đặt dựa trên kết luận đo kiểm thực tế của đầu dò D giai đoạn 4 (tài liệu kết quả đo kiểm đầu dò giai đoạn 4):
/// gọi <c>SW_MINIMIZE</c> cho từng cửa sổ là đường đi đáng tin cậy duy nhất —
/// <c>WM_SYSCOMMAND/SC_MINIMIZE</c> qua thử nghiệm gửi thông điệp không thu nhỏ được cửa sổ đích,
/// còn lệnh "Hiển thị màn hình nền" của shell ảnh hưởng toàn bộ màn hình nền của người dùng và không kiểm soát được, nên đều không dùng.
///
/// <para>
/// Danh sách cửa sổ ứng viên dùng lại quy tắc của <see cref="WindowLocator"/> (hiển thị, có tiêu đề, không phải cửa sổ công cụ),
/// nhờ vậy màn hình nền (<c>Progman</c>) và các cửa sổ phụ trợ được loại trừ tự nhiên; các cửa sổ của chính tiến trình này
/// (Dock, Launcher, cửa sổ bật lên) cũng phải loại trừ — "Hiển thị màn hình nền" không nên tự thu nhỏ chính Dock.
/// Các cửa sổ đã thu nhỏ sẵn được bỏ qua: chúng vốn đã không còn hiển thị trên màn hình nền.
/// </para>
/// </remarks>
public static class DesktopCommands
{
    /// <summary>
    /// Thu nhỏ toàn bộ cửa sổ ứng dụng đang hiển thị hiện tại, trả về số lượng cửa sổ đã được thu nhỏ.
    /// </summary>
    /// <remarks>
    /// Tiêu điểm sau lệnh <c>ShowWindow</c> cuối cùng sẽ được hệ thống chuyển sang cửa sổ phù hợp tiếp theo;
    /// kết quả đo kiểm của đầu dò cho thấy hành vi này bình thường, không cần can thiệp thêm.
    /// </remarks>
    public static int ShowDesktop()
    {
        var minimized = 0;
        var ownProcessId = (uint)Environment.ProcessId;

        foreach (var window in WindowLocator.EnumerateTopLevelWindows())
        {
            if (window.ProcessId == ownProcessId || window.IsMinimized)
            {
                continue;
            }

            if (WindowControl.Minimize(window.Handle))
            {
                minimized++;
            }
        }

        return minimized;
    }
}
