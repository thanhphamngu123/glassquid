using System.Runtime.InteropServices;

namespace Glassquid.Platform.Interop;

/// <summary>
/// Thuộc tính DWM cấp cửa sổ dùng để tạo giao diện kính.
/// </summary>
/// <remarks>
/// Chúng chỉ có ý nghĩa trên các phiên bản được liệt kê trong <see cref="WindowsCapabilities"/>;
/// trên các phiên bản cũ hơn, các lệnh gọi này sẽ trả về HRESULT thất bại và bên gọi nên coi đó là
/// tín hiệu "lùi về bảng tự vẽ" thay vì lỗi.
/// </remarks>
internal static partial class DwmApi
{
    /// <summary>Chất liệu nền như Mica / Acrylic. Windows 11 22621 trở lên.</summary>
    internal const int DwmwaSystemBackdropType = 38;

    /// <summary>Bo tròn góc viền cửa sổ. Windows 11 22000 trở lên.</summary>
    internal const int DwmwaWindowCornerPreference = 33;

    /// <summary>Chuyển đổi vùng phi khách hàng giữa chế độ sáng và tối.</summary>
    internal const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Màu viền.</summary>
    internal const int DwmwaBorderColor = 34;

    /// <summary>
    /// Đường viền **hiển thị thực tế** của cửa sổ, tính bằng pixel vật lý.
    /// </summary>
    /// <remarks>
    /// Đây là mắt xích then chốt để phát hiện toàn màn hình, không thể bỏ qua. Từ Windows 10 trở đi,
    /// <c>GetWindowRect</c> của cửa sổ phóng đại sẽ tính cả phần viền điều chỉnh vô hình bị đẩy ra ngoài màn hình (khoảng 8 pixel mỗi cạnh),
    /// khiến một cửa sổ phóng đại thông thường về mặt con số lại "che phủ toàn bộ màn hình",
    /// nếu chỉ nhìn <c>GetWindowRect</c> sẽ nhận diện nhầm là toàn màn hình.
    /// Thuộc tính này trả về vòng viền thực sự nhìn thấy trên màn hình (khi phóng đại chính xác bằng vùng làm việc),
    /// dùng nó để phán đoán mới phân biệt được "phóng đại" và "toàn màn hình".
    /// </remarks>
    internal const int DwmwaExtendedFrameBounds = 9;

    /// <summary>Cửa sổ có đang bị DWM ẩn hay không (UWP bị tạm ngưng, chuyển sang desktop ảo khác, v.v.).</summary>
    internal const int DwmwaCloaked = 14;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowIntAttribute(nint hwnd, int attribute, out int value, int size);

    /// <summary>
    /// Lấy đường viền hiển thị của cửa sổ (pixel vật lý). Trả về false khi thất bại, bên gọi cần tự lùi về <c>GetWindowRect</c>.
    /// </summary>
    /// <remarks>
    /// Dưới tỷ lệ thu phóng kết hợp, giá trị này có thể chênh vài pixel so với <c>GetWindowRect</c>, điều này bình thường;
    /// thứ thực sự cần phân biệt là sự chênh lệch hàng chục pixel của cửa sổ phóng đại.
    /// </remarks>
    internal static bool TryGetExtendedFrameBounds(nint hwnd, out Rect bounds)
    {
        bounds = default;

        if (hwnd == 0)
        {
            return false;
        }

        try
        {
            return DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<Rect>()) == 0
                && bounds.Width > 0
                && bounds.Height > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Cửa sổ có bị DWM ẩn không. Khi truy vấn thất bại trả về false (xem như cửa sổ bình thường).</summary>
    internal static bool IsCloaked(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        try
        {
            return DwmGetWindowIntAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Áp dụng một thuộc tính cửa sổ và báo cáo xem hệ thống có chấp nhận hay không.
    /// </summary>
    /// <remarks>
    /// Trả về false thay vì ném ngoại lệ. Việc máy không nhận ra một thuộc tính nào đó
    /// là tình huống bình thường và có thể dự đoán trước, phải hạ cấp âm thầm — không coi là ngoại lệ.
    /// </remarks>
    internal static bool TrySetAttribute(nint hwnd, int attribute, int value)
    {
        if (hwnd == 0)
        {
            return false;
        }

        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool TryExtendFrameIntoClientArea(nint hwnd, int left, int top, int right, int bottom)
    {
        if (hwnd == 0)
        {
            return false;
        }

        var margins = new Margins
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
        };

        try
        {
            return DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Margins
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}