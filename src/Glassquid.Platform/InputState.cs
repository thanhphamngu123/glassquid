using Glassquid.Platform.Interop;

namespace Glassquid.Platform;

/// <summary>
/// Trạng thái tức thời của bàn phím và chuột.
/// </summary>
/// <remarks>
/// Dùng <c>GetAsyncKeyState</c> để đọc trạng thái **vật lý**, thay vì <c>Mouse.LeftButton</c> của WPF:
/// thuộc tính sau được điều khiển bởi thông điệp cửa sổ, nhưng trong vòng lặp hộp thoại kéo thả (<c>DoDragDrop</c>), luồng giao diện không phân phối thông điệp bình thường,
/// nên giá trị đọc được lúc đó đã bị cũ. Việc xác định kết thúc kéo thả lại cần đúng sự thật tức thời "ngay lúc này chuột có còn đang được giữ hay không".
/// </remarks>
public static class InputState
{
    /// <summary>Nút chuột chính (chuột trái) ngay lúc này có đang được nhấn hay không.</summary>
    /// <remarks>
    /// Mục đích: phân biệt giữa "thả tay để kết thúc" và "nhấn Esc để hủy" — khi hủy, tay người dùng vẫn còn đang giữ nút chuột.
    /// Khi không đọc được thì trả về false (xử lý như "đã thả tay"), điều này sẽ đi theo nhánh "kết thúc bình thường",
    /// và bên gọi vẫn còn tín hiệu độc lập là sự kiện Esc để dự phòng.
    /// </remarks>
    public static bool IsPrimaryMouseButtonDown()
    {
        try
        {
            // Bit cao nhất biểu thị "ngay lúc này đang nhấn". Giá trị trả về là short, chỉ cần lấy bit dấu là được.
            return (User32.GetAsyncKeyState(User32.VirtualKeyLeftButton) & 0x8000) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}