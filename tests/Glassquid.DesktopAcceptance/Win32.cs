using System.Runtime.InteropServices;

namespace Glassquid.DesktopAcceptance;

/// <summary>Một điểm trên màn hình (pixel, tọa độ màn hình ảo).</summary>
internal readonly record struct ScreenPoint(int X, int Y);

/// <summary>Hình chữ nhật màn hình ảo (pixel, gốc tọa độ có thể âm — khi màn hình phụ nằm bên trái màn hình chính).</summary>
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;

    internal int Height => Bottom - Top;

    internal bool IsEmpty => Width <= 0 || Height <= 0;

    internal bool Contains(ScreenPoint point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    internal string Describe() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

/// <summary>Các thông tin thực tế có thể quan sát được của một cửa sổ cấp cao tại thời điểm hiện tại, tất cả đều lấy từ hệ điều hành chứ không từ trạng thái nội bộ của sản phẩm.</summary>
internal sealed record WindowFacts(
    nint Handle,
    string ClassName,
    string Title,
    int ProcessId,
    long ExStyle,
    ScreenRect Rect,
    bool Visible,
    nint Owner)
{
    // Ba bit của kiểu mở rộng (extended style). Sản phẩm yêu cầu dock "không chiếm tiêu điểm, không xuất hiện trên thanh tác vụ",
    // hai yêu cầu này thể hiện ở thuộc tính cửa sổ chính là NoActivate và IsOnTaskbar bên dưới.
    private const long NoActivate = 0x08000000L;
    private const long ToolWindow = 0x00000080L;
    private const long AppWindow = 0x00040000L;

    internal bool HasNoActivate => (ExStyle & NoActivate) != 0;

    internal bool HasToolWindow => (ExStyle & ToolWindow) != 0;

    internal bool HasAppWindow => (ExStyle & AppWindow) != 0;

    internal bool HasOwner => Owner != 0;

    /// <summary>
    /// Cửa sổ này hiện có xuất hiện trên thanh tác vụ hay không.
    /// </summary>
    /// <remarks>
    /// Quy tắc của thanh tác vụ là: hiển thị, không phải cửa sổ công cụ, <strong>không có cửa sổ chủ (owner)</strong>, và không có WS_EX_APPWINDOW.
    /// Ở đây cố ý không dùng "có WS_EX_TOOLWINDOW" làm tiêu chí "không nằm trên thanh tác vụ" — đã kiểm chứng thực tế: dock của sản phẩm
    /// có kiểu mở rộng là <c>0x08080008</c> (NOACTIVATE + LAYERED + TOPMOST, không có TOOLWINDOW),
    /// nhưng nó thực sự không xuất hiện trên thanh tác vụ. <c>ShowInTaskbar=False</c> của WPF đi đúng theo hướng "để một cửa sổ ẩn
    /// làm chủ sở hữu của cửa sổ". Vì vậy tiêu chí phải xét cửa sổ chủ, chứ không xét mỗi bit đó.
    /// </remarks>
    internal bool IsOnTaskbar => Visible && !HasToolWindow && !HasOwner && !HasAppWindow;

    internal string Describe() =>
        $"hwnd 0x{Handle:X} lớp {ClassName} tiêu đề \"{Title}\" pid {ProcessId} "
        + $"hình chữ nhật {Rect.Describe()} hiển thị {Visible} "
        + $"ExStyle 0x{ExStyle:X8} (NOACTIVATE={HasNoActivate} TOOLWINDOW={HasToolWindow} "
        + $"APPWINDOW={HasAppWindow} chủ sở hữu 0x{Owner:X})"
        + $"→ hiển thị trên thanh tác vụ {IsOnTaskbar}";
}

/// <summary>
/// Các điểm vào Win32 mà trình điều khiển kiểm thử sử dụng. Cố ý chỉ đưa vào những hàm thực sự được gọi,
/// tránh biến nơi này thành một bãi rác interop không ai bảo trì.
/// </summary>
internal static class Win32
{
    // ---- Thuộc tính cửa sổ ----

    internal const int GwlExStyle = -20;

    /// <summary>GW_OWNER của GetWindow: lấy cửa sổ chủ của cửa sổ (nếu không có thì là 0).</summary>
    private const uint GwOwner = 4;

    internal static nint GetForegroundWindow() => GetForegroundWindowRaw();

    internal static WindowFacts? Describe(nint handle)
    {
        if (handle == 0 || !IsWindow(handle))
        {
            return null;
        }

        var className = new char[256];
        var length = GetClassNameRaw(handle, className, className.Length);

        var title = new char[512];
        var titleLength = GetWindowTextRaw(handle, title, title.Length);

        GetWindowThreadProcessId(handle, out var processId);

        var rect = GetWindowRect(handle, out var bounds)
            ? new ScreenRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)
            : default;

        // Biến thể 32-bit của GetWindowLongPtr trong tiến trình 64-bit là GetWindowLongPtrW,
        // nhưng tiến trình 32-bit chỉ có GetWindowLongW. Trình điều khiển này chỉ chạy trên Windows 64-bit,
        // tên điểm vào được bọc bởi hai lần thử dưới đây (cũng không lỗi ở các cấu hình hiếm/màn hình hẹp).
        long exStyle;
        try
        {
            exStyle = GetWindowLongPtrWrapper(handle, GwlExStyle).ToInt64();
        }
        catch (EntryPointNotFoundException)
        {
            exStyle = GetWindowLongRaw(handle, GwlExStyle);
        }

        return new WindowFacts(
            handle,
            new string(className, 0, Math.Max(0, length)),
            new string(title, 0, Math.Max(0, titleLength)),
            (int)processId,
            exStyle,
            rect,
            IsWindowVisible(handle),
            // GW_OWNER, not GA_ROOTOWNER: the latter walks the owner chain to its root and
            // returns the window itself when there is no owner, which would make every window
            // look owned.
            GetWindowRaw(handle, GwOwner));
    }

    /// <summary>Liệt kê các cửa sổ cấp cao. Dùng cho đầu ra chẩn đoán và đối chiếu "cửa sổ tại điểm thuộc về ai".</summary>
    internal static List<nint> TopLevelWindows()
    {
        var handles = new List<nint>();

        _ = EnumWindows((handle, _) =>
        {
            handles.Add(handle);
            return true;
        }, 0);

        return handles;
    }

    /// <summary>Handle cửa sổ ngay bên dưới tọa độ. Vùng alpha 0 của cửa sổ phân lớp sẽ cho xuyên qua, giống hệt cú nhấp chuột thật.</summary>
    internal static nint WindowAt(ScreenPoint point) => WindowFromPointRaw(new Point32 { X = point.X, Y = point.Y });

    /// <summary>Cửa sổ cấp cao nhất tìm ngược lên từ một cửa sổ con bất kỳ.</summary>
    internal static nint RootAncestor(nint handle) => GetAncestorRaw(handle, 2 /* GA_ROOT */);

    /// <summary>
    /// Gửi yêu cầu đóng đến một cửa sổ.
    /// </summary>
    /// <remarks>
    /// Chỉ gửi thông điệp, không kết thúc tiến trình. Explorer là tiến trình dùng chung của người dùng, tuyệt đối không được để bài kiểm thử kết thúc nó;
    /// hơn nữa trước khi đóng còn phải đối chiếu tiêu đề cửa sổ có chứa tên thư mục duy nhất của lần chạy này hay không.
    /// </remarks>
    internal static bool RequestClose(nint handle) =>
        handle != 0 && PostMessage(handle, 0x0010 /* WM_CLOSE */, 0, 0);

    internal static int ProcessOf(nint handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        return (int)processId;
    }

    // ---- Tọa độ và hiển thị ----

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    internal static ScreenRect VirtualScreen() => new(
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmXVirtualScreen) + GetSystemMetrics(SmCxVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen) + GetSystemMetrics(SmCyVirtualScreen));

    internal static int SystemDpi()
    {
        var screen = GetDcScreen(0);
        if (screen == 0)
        {
            return 96;
        }

        try
        {
            return GetDeviceCaps(screen, 88 /* LOGPIXELSX */);
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// Mức độ nhận biết DPI của tiến trình hiện tại.
    /// </summary>
    /// <remarks>
    /// Bắt buộc phải ghi vào bằng chứng: tiến trình không nhận biết DPI sẽ nhận tọa độ đã được hệ thống phóng đại, trong khi hình chữ nhật điều khiển của UIA là pixel vật lý,
    /// trộn lẫn hai loại sẽ khiến cú nhấp rơi sang chỗ khác. Trình điều khiển dựa vào app.manifest để khai báo PerMonitorV2, ở đây đọc lại giá trị đó
    /// để chứng minh khai báo thực sự có hiệu lực, chứ không chỉ nằm trên tệp.
    /// </remarks>
    internal static string DpiAwareness()
    {
        try
        {
            var awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());

            return awareness switch
            {
                0 => "Không nhận biết (tọa độ sẽ bị hệ thống phóng đại, không cùng không gian với pixel vật lý của UIA)",
                1 => "DPI hệ thống",
                2 => "DPI theo từng màn hình v2",
                _ => $"Không xác định ({awareness})",
            };
        }
        catch (EntryPointNotFoundException)
        {
            return "(Hệ thống này không đọc được mức độ nhận biết DPI)";
        }
    }

    /// <summary>Hình chữ nhật vật lý, vùng làm việc và trạng thái màn hình chính của từng màn hình.</summary>
    internal static List<(ScreenRect Rect, ScreenRect Work, bool Primary)> Monitors()
    {
        var monitors = new List<(ScreenRect, ScreenRect, bool)>();

        _ = EnumDisplayMonitors(0, 0, (handle, _, _, _) =>
        {
            var info = new MonitorInfo32 { Size = Marshal.SizeOf<MonitorInfo32>() };

            if (GetMonitorInfoRaw(handle, ref info))
            {
                monitors.Add((
                    new ScreenRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
                    new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
                    (info.Flags & 1) != 0));
            }

            return true;
        }, 0);

        return monitors;
    }

    // ---- Màn hình nền (Desktop) và phiên ----

    /// <summary>
    /// Tên của input desktop hiện tại. Desktop tương tác đã đăng nhập có tên là "Default";
    /// sau khi khóa màn hình, input desktop sẽ đổi thành "Winlogon" — đó là "môi trường bị chặn", không phải lỗi của sản phẩm.
    /// </summary>
    internal static string? InputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (desktop == 0)
        {
            return null;
        }

        try
        {
            var buffer = new byte[512];
            if (!GetUserObjectInformation(desktop, 2 /* UOI_NAME */, buffer, buffer.Length, out var needed))
            {
                return null;
            }

            // Tên ở định dạng UTF-16, độ dài tính theo byte.
            var text = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2));
            return text.TrimEnd('\0');
        }
        finally
        {
            _ = CloseDesktop(desktop);
        }
    }

    /// <summary>Tên desktop của chính luồng thuộc tiến trình này. Nếu không khớp với input desktop thì nghĩa là chúng ta không ở trên desktop mà người dùng đang nhìn thấy.</summary>
    internal static string? CurrentThreadDesktopName()
    {
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == 0)
        {
            return null;
        }

        var buffer = new byte[512];
        if (!GetUserObjectInformation(desktop, 2 /* UOI_NAME */, buffer, buffer.Length, out var needed))
        {
            return null;
        }

        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2));
        return text.TrimEnd('\0');
    }

    internal static uint ActiveConsoleSessionId() => WTSGetActiveConsoleSessionId();

    /// <summary>
    /// Trạm cửa sổ (window station) của tiến trình này có "hiển thị" hay không — tức là có đang gắn với một desktop tương tác có thể hiển thị cửa sổ hay không.
    /// </summary>
    /// <remarks>
    /// Đây là căn cứ chính thức để phân biệt "phiên dịch vụ (service session) với desktop thật" (<c>WSF_VISIBLE</c>).
    /// Trạm cửa sổ trong phiên dịch vụ không hiển thị được, không vẽ được cửa sổ, nên không thể nói đến ngữ nghĩa chuột hay bàn phím.
    /// Cố ý <strong>không</strong> so sánh theo "mã phiên bảng điều khiển (console session ID)": khi kết nối qua Remote Desktop hoặc phiên nâng cao của máy ảo,
    /// phiên bảng điều khiển là một mã khác, so sánh như vậy sẽ nhận nhầm phiên tương tác hợp lệ là bị chặn.
    /// </remarks>
    internal static bool WindowStationIsVisible()
    {
        var station = GetProcessWindowStation();
        if (station == 0)
        {
            return false;
        }

        var flags = default(UserObjectFlags);
        return GetUserObjectInformationFlags(station, 1 /* UOI_FLAGS */, ref flags, Marshal.SizeOf<UserObjectFlags>(), out _)
            && (flags.Flags & 1 /* WSF_VISIBLE */) != 0;
    }

    // ---- GDI dùng để chụp ảnh màn hình ----

    internal const int DibRgbColors = 0;
    internal const uint BiRgb = 0;
    internal const uint Srccopy = 0x00CC0020;
    internal const uint Captureblt = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader32
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo32
    {
        internal BitmapInfoHeader32 Header;
        internal uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint dc, int index);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint dc, nint handle);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(nint handle);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll")]
    internal static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfo32 info, uint usage);

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern nint GetDcScreen(nint window);

    // ---- Liệt kê màn hình ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo32
    {
        internal int Size;
        internal Rect32 Monitor;
        internal Rect32 Work;
        internal uint Flags;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfoRaw(nint monitor, ref MonitorInfo32 info);

    // ---- Cửa sổ ----

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint data);

    private delegate bool EnumWindowsProc(nint handle, nint data);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameRaw(nint handle, [Out] char[] buffer, int length);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowTextRaw(nint handle, [Out] char[] buffer, int length);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out Rect32 rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint handle, int index);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint handle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern nint GetWindowRaw(nint handle, uint command);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindowRaw();

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint handle, uint message, nint wparam, nint lparam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint WindowFromPointRaw(Point32 point);

    // ---- Màn hình nền (Desktop) ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    private static extern bool GetUserObjectInformation(nint handle, int index, [Out] byte[] info, int length, out int needed);

    /// <remarks>
    /// Cấu trúc có ba trường (<c>fInherit</c>, <c>fReserved</c>, <c>dwFlags</c>), không phải hai.
    /// Nếu chỉ khai báo hai trường sẽ đọc sai trường: <c>dwFlags</c> rơi ra ngoài cấu trúc, đọc về luôn bằng 0,
    /// khi đó mọi phiên tương tác đều bị nhận nhầm thành phiên dịch vụ. Đã kiểm chứng thực tế một lần, thứ tự trường ở đây là bắt buộc.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct UserObjectFlags
    {
        internal int Inherit;
        internal int Reserved;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    private static extern bool GetUserObjectInformationFlags(nint handle, int index, ref UserObjectFlags info, int length, out int needed);

    // ---- Hàm bọc nhỏ: gom việc phân biệt "điểm vào 32-bit hay 64-bit" vào một chỗ ----

    private static nint GetWindowLongPtrWrapper(nint handle, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : GetWindowLong32(handle, index);

    private static long GetWindowLongRaw(nint handle, int index) => GetWindowLong32(handle, index);

    private static nint GetAncestorRaw(nint handle, uint flags) => GetAncestor(handle, flags);

    /// <summary>Pixel toàn màn hình (BGRA, từ trên xuống dưới). Bằng chứng ảnh chụp màn hình dùng hàm này, không phụ thuộc bất kỳ thư viện ảnh ngoài nào.</summary>
    internal static byte[] CaptureBgra(ScreenRect area, out int stride)
    {
        stride = area.Width * 4;

        var screen = GetDcScreen(0);
        if (screen == 0)
        {
            throw new InvalidOperationException("Không lấy được ngữ cảnh thiết bị màn hình, không thể chụp ảnh màn hình.");
        }

        var memory = nint.Zero;
        var bitmap = nint.Zero;

        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, area.Width, area.Height);

            var previous = SelectObject(memory, bitmap);
            try
            {
                if (!BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, Srccopy | Captureblt))
                {
                    throw new InvalidOperationException("BitBlt thất bại, không thể chụp ảnh màn hình.");
                }
            }
            finally
            {
                _ = SelectObject(memory, previous);
            }

            var info = new BitmapInfo32
            {
                Header = new BitmapInfoHeader32
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader32>(),
                    // Chiều cao âm = thứ tự dòng từ trên xuống dưới, đỡ mất một lần lật ảnh.
                    Width = area.Width,
                    Height = -area.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = (uint)(stride * area.Height),
                },
            };

            var pixels = new byte[stride * area.Height];
            var lines = GetDIBits(memory, bitmap, 0, (uint)area.Height, pixels, ref info, DibRgbColors);

            if (lines == 0)
            {
                throw new InvalidOperationException("GetDIBits thất bại, không thể chụp ảnh màn hình.");
            }

            return pixels;
        }
        finally
        {
            if (bitmap != 0)
            {
                _ = DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                _ = DeleteDC(memory);
            }

            _ = ReleaseDC(0, screen);
        }
    }
}
