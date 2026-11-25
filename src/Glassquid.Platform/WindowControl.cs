using Glassquid.Platform.Interop;

namespace Glassquid.Platform;

/// <summary>
/// Thực hiện các thao tác điều khiển nhẹ nhàng trên từng cửa sổ: hiện, ẩn, thu nhỏ, khôi phục và yêu cầu đóng.
/// </summary>
/// <remarks>
/// Bọc một lớp thay vì để tầng trên gọi thẳng <c>User32</c> (tương tự <see cref="WindowActivation"/>).
/// Toàn bộ thao tác đều đi qua thông điệp của chính cửa sổ và <c>ShowWindow</c>, không kết thúc bất kỳ tiến trình nào——
/// các thao tác hàng loạt như “Hiện màn hình nền” hay dọn dẹp probe dựa vào đây để giữ sự nhẹ nhàng: cửa sổ không đóng được
/// sẽ trả về trung thực để bên gọi quyết định bước tiếp theo, tuyệt đối không cưỡng bức kết thúc.
///
/// <para>
/// Các lệnh gọi này có thể thất bại một cách hợp lệ (cửa sổ đã biến mất, bị từ chối), trả về <c>false</c> mà không ném ngoại lệ.
/// </para>
/// </remarks>
public static class WindowControl
{
    /// <summary>Trả về true khi handle cửa sổ vẫn còn hợp lệ.</summary>
    public static bool IsAlive(nint window)
    {
        try
        {
            return window != 0 && User32.IsWindow(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Cửa sổ hiện có đang ở trạng thái thu nhỏ hay không.</summary>
    public static bool IsMinimized(nint window)
    {
        try
        {
            return window != 0 && User32.IsIconic(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Cửa sổ tiền cảnh hiện tại; trả về 0 nếu không có cửa sổ tiền cảnh.</summary>
    public static nint GetForegroundWindow()
    {
        try
        {
            return User32.GetForegroundWindow();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Yêu cầu thu nhỏ qua <c>WM_SYSCOMMAND/SC_MINIMIZE</c>——là hai con đường tương đương với <see cref="Minimize"/>,
    /// thử nghiệm thực tế bằng probe “Hiện màn hình nền” cho thấy cả hai đều hiệu quả như nhau với cửa sổ đích.
    /// </summary>
    public static bool MinimizeViaSysCommand(nint window)
    {
        const uint WmSysCommand = 0x0112;
        const nint ScMinimize = 0xF020;

        try
        {
            return window != 0 && User32.PostMessage(window, WmSysCommand, (nuint)ScMinimize, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }


    /// <summary>Đọc tiêu đề cửa sổ; trả về chuỗi rỗng nếu không đọc được.</summary>
    public static string GetTitle(nint window)
    {
        try
        {
            return window == 0 ? string.Empty : User32.ReadWindowText(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>Gửi một thông điệp <c>WM_CLOSE</c>, đi theo quy trình đóng riêng của cửa sổ.</summary>
    public static bool Close(nint window)
    {
        try
        {
            return window != 0 && User32.PostMessage(window, User32.WmClose, 0, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Thu nhỏ cửa sổ.</summary>
    public static bool Minimize(nint window) => ShowWith(window, User32.SwMinimize);

    /// <summary>Khôi phục cửa sổ.</summary>
    public static bool Restore(nint window) => ShowWith(window, User32.SwRestore);

    /// <summary>Hiển thị cửa sổ (không kích hoạt).</summary>
    public static bool Show(nint window) => ShowWith(window, User32.SwShow);

    /// <summary>Ẩn cửa sổ. Ẩn khác với thu nhỏ: cửa sổ biến mất khỏi cả màn hình lẫn thanh tác vụ.</summary>
    public static bool Hide(nint window) => ShowWith(window, User32.SwHide);

    private static bool ShowWith(nint window, int command)
    {
        try
        {
            return window != 0 && User32.ShowWindow(window, command);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
