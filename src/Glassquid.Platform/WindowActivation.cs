using Glassquid.Platform.Interop;

namespace Glassquid.Platform;

/// <summary>
/// Đưa cửa sổ lên tiền cảnh.
/// </summary>
/// <remarks>
/// Bọc một lớp mỏng thay vì để lộ lời gọi interop thô ra ngoài, để phần còn lại của sản phẩm
/// không cần biết API nào ở phía sau. Bên gọi chính là menu khay hệ thống: menu vùng thông báo
/// sẽ không tự thu gọn khi nhấp ra ngoài, trừ khi cửa sổ chủ quản của nó trở thành
/// cửa sổ tiền cảnh trước.
///
/// Lần gọi này có thể thất bại một cách hợp lý — Windows sẽ từ chối việc chuyển lên tiền cảnh từ tiến trình mà người dùng không đang tương tác,
/// và trả về false thay vì ném ngoại lệ. Bên gọi coi kết quả false là một giới hạn về mặt hiển thị,
/// tuyệt đối không coi là lỗi.
/// </remarks>
public static class WindowActivation
{
    public static bool TryBringToForeground(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        try
        {
            return User32.SetForegroundWindow(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}