using Glassquid.Platform.Interop;

namespace Glassquid.Platform;

/// <summary>
/// Đọc và sửa đổi các bit kiểu mở rộng của cửa sổ.
/// </summary>
/// <remarks>
/// Bọc một lớp mỏng thay vì để lộ trực tiếp interop gốc (tương tự <see cref="WindowActivation"/>),
/// giúp phần còn lại của sản phẩm không cần biết API bên dưới là gì hay tự ghép bit kiểu.
/// </remarks>
public static class WindowStyles
{
    private static readonly nint HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>Raise an existing topmost window without changing focus, position, or size.</summary>
    public static bool BringToTopWithoutActivation(nint window)
    {
        if (window == 0) return false;

        try
        {
            return Dpi32.SetWindowPos(
                window,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Thêm <c>WS_EX_NOACTIVATE</c> cho cửa sổ: nhấp vào cửa sổ sẽ không đưa nó lên tiền cảnh.
    /// </summary>
    /// <remarks>
    /// Thanh Dock chính dựa vào cờ này để đạt được hành vi "nhấp không cướp tiêu điểm" — khi người dùng đang gõ ở ứng dụng khác
    /// mà nhấp vào Dock, tiêu điểm phải giữ nguyên tại chỗ. Đổi lại, các thành phần do Dock tự bật lên
    /// (menu chuột phải, bảng thư mục, danh sách chọn cửa sổ) cũng không tự nhận tiêu điểm khi nhấp,
    /// nên khi cần tương tác bàn phím phải gọi tường minh <see cref="WindowActivation.TryBringToForeground"/>.
    ///
    /// Giá trị trả về cho biết kiểu đã thực sự có hiệu lực để phục vụ chẩn đoán và kiểm thử; nếu đã có kiểu này rồi
    /// thì trả về true và không ghi lại.
    /// </remarks>
    public static bool MakeNoActivate(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        try
        {
            var style = User32.GetWindowLong(window, User32.GwlExStyle);
            if ((style & User32.WsExNoactivate) != 0)
            {
                return true;
            }

            // SetWindowLong trả về giá trị trước khi đổi chứ không phải trạng thái thành công/thất bại, nên bỏ qua giá trị trả về ở đây (CA1806):
            // việc ghi thành công hay chưa được xác định bằng lần đọc lại kiểu bên dưới, không dựa vào giá trị trả về để bắt lỗi —
            // khi thất bại nó trả về 0, mà kiểu cũ của cửa sổ mới tình cờ cũng có thể là 0.
            _ = User32.SetWindowLong(window, User32.GwlExStyle, style | User32.WsExNoactivate);

            return IsNoActivate(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Kiểm tra cửa sổ đã có kiểu <c>WS_EX_NOACTIVATE</c> hay chưa.</summary>
    public static bool IsNoActivate(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        try
        {
            return (User32.GetWindowLong(window, User32.GwlExStyle) & User32.WsExNoactivate) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
