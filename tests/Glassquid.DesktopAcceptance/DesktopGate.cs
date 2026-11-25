using System.Diagnostics;

namespace Glassquid.DesktopAcceptance;

/// <summary>
/// Cổng kiểm tra môi trường trước khi chạy.
/// </summary>
/// <remarks>
/// Trình điều khiển này sẽ thật sự di chuyển chuột và nhấn phím. Chạy nhầm môi trường thì nhẹ là không kiểm thử được gì,
/// nặng là làm phiền người đang sử dụng máy. Vì vậy cần trả lời một câu hỏi trước:
/// <strong>Hiện tại có một màn hình tương tác chỉ dành riêng cho kiểm thử hay không?</strong>
/// Nếu không xác nhận được thì ghi nhận “bị chặn” và thoát ngay, không gửi bất kỳ sự kiện nhập liệu nào.
///
/// Ba tiêu chí độc lập, thiếu một tiêu chí đều coi là bị chặn:
/// <list type="number">
/// <item>Bên gọi khẳng định rõ “tôi đang ở trên màn hình cách ly” (nếu không khẳng định thì tuyệt đối không chèn đầu vào);</item>
/// <item>Tiến trình gắn với một window station hiển thị được, không phải phiên dịch vụ;</item>
/// <item>Màn hình mà tiến trình đang chạy chính là màn hình nhập liệu hiện tại và không phải màn hình khóa.</item>
/// </list>
/// </remarks>
internal static class DesktopGate
{
    internal const string OptInFlag = "--i-am-on-isolated-desktop";

    internal const string OptInEnvironmentVariable = "GLASSQUID_ISOLATED_DESKTOP";

    /// <summary>Trả về null nghĩa là môi trường đạt yêu cầu; ngược lại là lý do cần ghi vào báo cáo.</summary>
    internal static string? BlockedReason(bool optedIn)
    {
        if (!optedIn)
        {
            return "Chưa xác nhận đang chạy trên màn hình cách ly. Trình điều khiển này sẽ di chuyển chuột và nhấn phím thật, "
                + $"nên phải chạy trong tài khoản kiểm thử riêng hoặc máy ảo, và truyền tường minh {OptInFlag}"
                + $" (hoặc đặt biến môi trường {OptInEnvironmentVariable}=1)."
                + " Nếu chưa xác nhận, nó sẽ không gửi bất kỳ sự kiện nhập liệu nào.";
        }

        if (IntPtr.Size != 8)
        {
            return $"Trình điều khiển đang chạy trong tiến trình {IntPtr.Size * 8} bit; bố cục cấu trúc nhập liệu và tọa độ chèn chỉ được kiểm chứng trên 64 bit.";
        }

        if (Win32.Monitors().Count == 0)
        {
            return "Không liệt kê được màn hình nào, không có desktop để định vị điều khiển.";
        }

        if (!Win32.WindowStationIsVisible())
        {
            return "Window station của tiến trình này không hiển thị: đây là phiên dịch vụ, không phải màn hình tương tác người dùng đang xem. "
                + "Cửa sổ không thể vẽ ra và ngữ nghĩa chuột, bàn phím cũng không còn hiệu lực.";
        }

        var current = Win32.CurrentThreadDesktopName();
        if (string.IsNullOrEmpty(current))
        {
            return "Không đọc được tên desktop mà tiến trình này đang chạy.";
        }

        var input = Win32.InputDesktopName();
        if (string.IsNullOrEmpty(input))
        {
            return $"Không mở được desktop nhập liệu (tiến trình đang ở “{current}”). Phiên rất có thể đang bị khóa hoặc đã ngắt kết nối.";
        }

        if (!string.Equals(current, input, StringComparison.OrdinalIgnoreCase))
        {
            return $"Tiến trình đang ở desktop “{current}”, trong khi desktop nhập liệu là “{input}”. "
                + (input.Equals("Winlogon", StringComparison.OrdinalIgnoreCase)
                    ? "Desktop nhập liệu là Winlogon, chứng tỏ phiên đang ở màn hình khóa; đầu vào được chèn lúc này sẽ không tới bất kỳ cửa sổ ứng dụng nào."
                    : "Hai desktop không trùng nhau; đầu vào được chèn sẽ không tới được cửa sổ cần kiểm thử.");
        }

        if (!current.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return $"Tên desktop nhập liệu là “{current}” chứ không phải Default, không thể xác nhận đây là màn hình tương tác khả dụng.";
        }

        return null;
    }

    /// <summary>Phiên hiện tại đã có Glassquid nào đang chạy chưa. Nếu có thì không thể quy thuộc cửa sổ thuộc về tiến trình nào nên buộc phải chặn.</summary>
    internal static string? ExistingInstanceReason()
    {
        var running = Process.GetProcessesByName("Glassquid");

        try
        {
            var others = running.Where(process => process.Id != Environment.ProcessId).ToList();
            if (others.Count == 0)
            {
                return null;
            }

            var described = string.Join(
                "; ",
                others.Select(process => $"pid {process.Id}"));

            return $"Phiên hiện tại đã có Glassquid đang chạy ({described}). "
                + "Màn hình độc lập yêu cầu một phiên sạch: nếu không, không thể xác định cửa sổ mới tạo thuộc về tiến trình nào, "
                + "và cơ chế chuyển tiếp đơn thể sẽ khiến tiến trình mới thoát ngay. Hãy đóng tiến trình đó trước hoặc chạy trong tài khoản kiểm thử mới.";
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }
}