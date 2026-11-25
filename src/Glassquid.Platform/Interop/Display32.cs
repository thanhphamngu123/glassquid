using System.Runtime.InteropServices;

namespace Glassquid.Platform.Interop;

/// <summary>
/// Liệt kê màn hình và truy vấn DPI.
/// </summary>
/// <remarks>
/// Các khai báo này chủ đích trả về **pixel vật lý**: tiến trình này cần được khai báo nhận thức PerMonitorV2, khi đó
/// <c>EnumDisplayMonitors</c> trả về biên pixel thực chưa qua tỷ lệ thu phóng. Bố cục của WPF dùng DIP,
/// hai loại này thuộc hai không gian tọa độ khác nhau, trộn lẫn sẽ làm lệch vị trí trên màn hình phụ — xem <c>CoordinateSpace</c>.
/// </remarks>
internal static partial class Display32
{
    internal const int MonitorInfoPrimary = 0x00000001;

    /// <summary><c>MONITOR_DEFAULTTONEAREST</c>: trả về màn hình gần nhất khi không tìm thấy thay vì báo lỗi.</summary>
    internal const int MonitorDefaultToNearest = 0x00000002;

    internal const int MonitorDefaultToPrimary = 0x00000001;

    internal delegate bool MonitorEnumProc(nint monitor, nint deviceContext, ref Rect monitorRect, nint data);

    // Dùng DllImport thay vì LibraryImport ở đây: delegate callback không thể marshal bằng source generator,
    // cố chuyển đổi sẽ khiến các điểm gọi khó đọc hơn so với lợi ích mang lại.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(nint deviceContext, nint clipRect, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, int flags);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint window, int flags);

    /// <summary>Lấy hình chữ nhật cửa sổ, theo **pixel vật lý** (là pixel thực trong tiến trình nhận thức theo từng màn hình).</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect rect);

    /// <summary>Lấy hình chữ nhật vùng khách của cửa sổ, theo **pixel vật lý**.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint window, out Rect rect);

    /// <summary>Vị trí hiện tại của con trỏ chuột, theo pixel vật lý.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out Point point);
}

internal static partial class Dpi32
{
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    /// <summary>Ngữ cảnh nhận thức DPI của luồng hiện tại. Giá trị trả về là giả handle, không được dùng như con trỏ thật.</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindowDpiAwarenessContext(nint window);

    [LibraryImport("user32.dll")]
    internal static partial int GetAwarenessFromDpiAwarenessContext(nint awarenessContext);

    /// <summary>
    /// So sánh hai ngữ cảnh nhận thức DPI xem có tương đương nhau không. Việc kiểm tra PerMonitorV2 khai báo trong manifest chỉ có thể dựa vào hàm này.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AreDpiAwarenessContextsEqual(nint awarenessContext1, nint awarenessContext2);

    /// <summary>Di chuyển cửa sổ sang màn hình chỉ định và trả về kết quả thành công hay không. Dùng để kiểm chứng định vị đa màn hình.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}

/// <summary>Win32 <c>RECT</c>, theo pixel vật lý.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;

    internal readonly int Width => Right - Left;

    internal readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    internal int X;
    internal int Y;
}

/// <summary><c>MONITORINFOEX</c>. <c>szDevice</c> có dạng như <c>\\.\DISPLAY1</c>.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfoEx
{
    internal int Size;
    internal Rect Monitor;
    internal Rect Work;
    internal int Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string DeviceName;
}