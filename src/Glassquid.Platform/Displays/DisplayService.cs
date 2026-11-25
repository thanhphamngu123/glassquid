using System.Runtime.InteropServices;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Displays;

/// <summary>
/// Liệt kê màn hình, thăm dò nhận thức DPI (DPI awareness), và chuyển đổi giữa pixel vật lý với DIP.
/// </summary>
/// <remarks>
/// Báo cáo cơ sở từng ghi nhận một lỗi thực tế: trình khởi chạy tham chiếu đem **pixel vật lý**
/// do hook chuột cấp thấp báo cáo so sánh trực tiếp với ranh giới màn hình **DIP** của Electron,
/// gây lệch (1024, 246) trên màn hình phụ, khiến góc nóng (hot corner) trên màn hình phụ không bao giờ kích hoạt,
/// hoặc kích hoạt nhầm trong một vùng rộng.
///
/// Sản phẩm này không phụ thuộc vào bộ hook đó, nhưng cùng một cái bẫy vẫn tồn tại trong phần triển khai gốc (native).
/// Do đó nguyên tắc ở đây là: **mọi tọa độ đều phải hỏi rõ thuộc không gian nào trước**. Các phương thức chuyển đổi
/// trên <see cref="DisplayInfo"/> tồn tại để việc này chỉ có một điểm xử lý duy nhất, thay vì tự tính nhẩm ở từng chỗ gọi.
/// </remarks>
public static partial class DisplayService
{
    // Handle giả của ngữ cảnh nhận thức DPI. Chúng không phải con trỏ thật, chỉ là các giá trị quy ước.
    private static readonly nint ContextUnaware = -1;
    private static readonly nint ContextSystemAware = -2;
    private static readonly nint ContextPerMonitorAware = -3;
    private static readonly nint ContextPerMonitorAwareV2 = -4;
    private static readonly nint ContextUnawareGdiScaled = -5;

    /// <summary>
    /// Liệt kê tất cả màn hình. Ranh giới và vùng làm việc đều tính bằng **pixel vật lý**.
    /// </summary>
    /// <remarks>
    /// Nếu tiến trình này đã khai báo nhận thức DPI theo từng màn hình (Per-Monitor DPI Aware), giá trị lấy được
    /// ở đây là pixel thật chưa qua co giãn; nếu chưa, hệ thống sẽ kéo giãn kết quả theo tỷ lệ co giãn hiện tại,
    /// các con số trông "na ná" nhưng thực chất bị sai — đó là lý do <see cref="ProbeDpiAwareness"/> cần được xem cùng kết quả liệt kê.
    /// </remarks>
    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var displays = new List<DisplayInfo>();

        // Dùng danh sách thay vì bắt giữ closure: callback liệt kê không được ném ngoại lệ xuyên qua stack native.
        // Phải ghi rõ kiểu tham số — delegate có tham số ref, lambda không thể suy luận kiểu để khớp được.
        Display32.MonitorEnumProc callback = (nint monitor, nint deviceContext, ref Rect monitorRect, nint data) =>
        {
            var info = new MonitorInfoEx
            {
                Size = Marshal.SizeOf<MonitorInfoEx>(),
                DeviceName = string.Empty,
            };

            if (Display32.GetMonitorInfo(monitor, ref info))
            {
                displays.Add(new DisplayInfo(
                    DeviceName: info.DeviceName,
                    IsPrimary: (info.Flags & Display32.MonitorInfoPrimary) != 0,
                    Bounds: ToPixelRect(info.Monitor),
                    WorkArea: ToPixelRect(info.Work),
                    Dpi: DpiForMonitor(monitor)));
            }

            return true;
        };

        Display32.EnumDisplayMonitors(0, 0, callback, 0);

        // Xếp màn hình chính lên đầu tiên, các màn hình còn lại sắp xếp theo tên thiết bị để bảo đảm thứ tự liệt kê nhất quán trên cùng một máy.
        return displays
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Lấy màn hình chính; trả về null nếu liệt kê thất bại.</summary>
    public static DisplayInfo? Primary() => Enumerate().FirstOrDefault(static d => d.IsPrimary);

    /// <summary>
    /// Lấy vị trí hiện tại của con trỏ chuột theo **pixel vật lý**.
    /// </summary>
    /// <remarks>
    /// Tính năng phát hiện vùng nóng tự động ẩn dùng phương thức này: vùng nóng nằm trên mép vùng làm việc
    /// của màn hình đích, nên việc phán đoán phải nằm cùng không gian tọa độ với <see cref="Enumerate"/>.
    /// Trả về null khi không đọc được (ví dụ con trỏ không khả dụng trong phiên điều khiển từ xa) —
    /// bên gọi xử lý như "con trỏ không nằm trong bất kỳ vùng nóng nào", tuyệt đối không coi là lỗi.
    /// </remarks>
    public static (int X, int Y)? GetCursorPixelPoint()
    {
        try
        {
            return Display32.GetCursorPos(out var point) ? (point.X, point.Y) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Tìm màn hình theo tên thiết bị, ví dụ <c>\\.\DISPLAY2</c>.</summary>
    /// <remarks>
    /// Dùng tên thiết bị thay vì chỉ số để lưu trữ lâu dài vị trí "Dock nằm trên màn hình nào": chỉ số có thể thay đổi giữa các phiên, tên thiết bị thì không.
    /// </remarks>
    public static DisplayInfo? FindByDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        return Enumerate()
            .FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tìm màn hình theo tên thiết bị, nếu không thấy thì lùi về màn hình chính.</summary>
    /// <remarks>
    /// Sau khi rút màn hình ra, Dock phải quay về màn hình chính thay vì nằm ngoài màn hình — vì vậy ở đây cố tình không trả về null.
    /// </remarks>
    public static DisplayInfo? ResolveOrPrimary(string? deviceName) => FindByDeviceName(deviceName) ?? Primary();

    /// <summary>Cho tọa độ pixel vật lý, trả về màn hình mà điểm đó rơi vào.</summary>
    public static DisplayInfo? FromPixelPoint(int x, int y)
    {
        var handle = Display32.MonitorFromPoint(new Point { X = x, Y = y }, Display32.MonitorDefaultToNearest);
        if (handle == 0)
        {
            return null;
        }

        var info = new MonitorInfoEx
        {
            Size = Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty,
        };

        if (!Display32.GetMonitorInfo(handle, ref info))
        {
            return null;
        }

        return new DisplayInfo(
            info.DeviceName,
            (info.Flags & Display32.MonitorInfoPrimary) != 0,
            ToPixelRect(info.Monitor),
            ToPixelRect(info.Work),
            DpiForMonitor(handle));
    }

    /// <summary>Thăm dò mức độ nhận thức DPI của tiến trình hiện tại.</summary>
    public static DpiAwarenessInfo ProbeDpiAwareness()
    {
        var context = Dpi32.GetThreadDpiAwarenessContext();
        var coarse = Dpi32.GetAwarenessFromDpiAwarenessContext(context);

        var level = coarse switch
        {
            0 => DpiAwarenessLevel.Unaware,
            1 => DpiAwarenessLevel.SystemAware,
            2 => DpiAwarenessLevel.PerMonitorAware,
            _ => DpiAwarenessLevel.Unknown,
        };

        return new DpiAwarenessInfo(
            Level: level,
            IsPerMonitorV2: IsContext(context, ContextPerMonitorAwareV2),
            RawContext: DescribeContext(context),
            SystemDpi: Dpi32.GetDpiForSystem());
    }

    /// <summary>
    /// So sánh hai ngữ cảnh nhận thức DPI xem có tương đương nhau không.
    /// </summary>
    /// <remarks>
    /// Phải dùng API này, không được so sánh trực tiếp giá trị số của handle. Khi mức nhận thức đến từ khai báo manifest,
    /// <c>GetThreadDpiAwarenessContext</c> trả về một handle thật (đo thực tế có dạng 0x9011),
    /// chứ không phải mấy giá trị quy ước <c>DPI_AWARENESS_CONTEXT_*</c>; chỉ khi chủ động thiết lập qua
    /// <c>SetThreadDpiAwarenessContext</c> mới nhận được giá trị quy ước.
    /// So sánh số trực tiếp sẽ nhận nhầm PerMonitorV2 khai báo trong manifest thành "Không xác định".
    /// </remarks>
    private static bool IsContext(nint context, nint reference)
    {
        try
        {
            return Dpi32.AreDpiAwarenessContextsEqual(context, reference);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Hệ thống cũ không có API này, đành lùi về không phân biệt V1/V2.
            return false;
        }
    }

    /// <summary>Lấy DPI của màn hình chứa một cửa sổ nhất định.</summary>
    public static uint DpiForWindow(nint window)
    {
        if (window == 0)
        {
            return 96;
        }

        var dpi = Dpi32.GetDpiForWindow(window);
        return dpi == 0 ? 96 : dpi;
    }

    /// <summary>
    /// Đặt cửa sổ theo hình chữ nhật **pixel vật lý**.
    /// </summary>
    /// <remarks>
    /// Đây là con đường đáng tin cậy duy nhất để định vị xuyên màn hình. Đã đo thực tế <c>Left</c>/<c>Top</c> của WPF:
    /// phép chuyển đổi DIP→vật lý của chúng dùng DPI của **màn hình mà cửa sổ đang đứng hiện tại**, chứ không phải màn hình đích.
    /// Do đó trong môi trường DPI hỗn hợp, gán giá trị một lần xuyên màn hình chắc chắn bị lệch — đo thực tế thấy X trên màn hình phụ
    /// bị lệch 640 pixel (hiện tại ở màn hình chính 150%, đích là màn hình phụ 125%). Còn hình chữ nhật cửa sổ của Win32 vốn đã là pixel vật lý,
    /// nên không tồn tại sự nhập nhằng này.
    /// </remarks>
    public static bool MoveWindowToPixelRect(nint window, PixelRect rect)
    {
        if (window == 0)
        {
            return false;
        }

        // Đặt vị trí hai lần, ở giữa cho các thông điệp cửa sổ đang xếp hàng chạy hết.
        //
        // Đây không phải là thừa thãi. Đặt vị trí vật lý một lần cho xong sẽ bị **đúng vị trí, sai kích thước**:
        // sau khi cửa sổ rơi sang màn hình đích, framework giao diện (đặc biệt rõ ở WPF) sẽ nhận thông báo đổi DPI
        // và co giãn lại cửa sổ theo **kích thước DIP đã lỗi thời** của nó. Đo thực tế trên màn hình phụ 125% cho thấy
        // 320×80 bị đổi thành 267×67, đúng bằng tỷ lệ 120/144.
        //
        // Đóng gói cả hai việc vào đây để bên gọi không có cách nào giẫm phải cái bẫy này: trước kia chỉ có chương trình dò tìm
        // tự gọi hai lần mới vượt qua được, còn phương thức công khai chỉ đặt một lần — kiểu đóng gói mà "cách dùng đúng chỉ viết trong bài kiểm thử"
        // còn nguy hiểm hơn cả việc không đóng gói.
        if (!TrySetPositionOnce(window, rect))
        {
            return false;
        }

        DrainPendingWindowMessages(window);

        return TrySetPositionOnce(window, rect);
    }

    private static bool TrySetPositionOnce(nint window, PixelRect rect)
    {
        // Không đổi thứ tự Z, không kích hoạt: định vị không được tiện tay đưa cửa sổ lên trên cùng hoặc cướp tiêu điểm (focus).
        const uint SwpNoZOrder = 0x0004;
        const uint SwpNoActivate = 0x0010;

        return Dpi32.SetWindowPos(
            window,
            0,
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>
    /// Xử lý hết các thông điệp đang xếp hàng của cửa sổ này, nhưng không đụng tới thông điệp của cửa sổ khác.
    /// </summary>
    /// <remarks>
    /// Chỉ lấy thông điệp của cửa sổ này (<c>PeekMessage</c> chỉ định hwnd), nên sẽ không tiện tay phân phát luôn
    /// thông điệp của các cửa sổ khác — điều đó sẽ gây ra tác dụng phụ khó truy vết cho bên gọi.
    ///
    /// Có giới hạn trên: nhỡ một thông điệp nào đó tự gửi lại chính nó liên tục thì ở đây cũng không thành vòng lặp vô tận.
    /// Chạm ngưỡng trên là quay về ngay, lần đặt vị trí thứ hai phía sau vẫn sẽ được thực thi.
    ///
    /// <b>Bắt buộc phải gọi trên luồng sở hữu cửa sổ đó.</b> Đây là ràng buộc cơ bản của bơm thông điệp (message pump):
    /// phân phát thông điệp của cửa sổ trên luồng khác đồng nghĩa với chạy thủ tục cửa sổ (window procedure) của nó trên sai luồng.
    /// </remarks>
    private static void DrainPendingWindowMessages(nint window)
    {
        const int MaxMessages = 64;

        for (var i = 0; i < MaxMessages; i++)
        {
            // Nhìn trước một lượt mà không rút khỏi hàng đợi: thông điệp thoát phải để lại cho vòng lặp thông điệp thực sự,
            // nếu ta lấy mất thì chương trình sẽ không nhận được thông báo thoát và không kết thúc bình thường được.
            if (!User32.PeekMessage(out var message, window, 0, 0, User32.PmNoRemove))
            {
                return;
            }

            if (message.Message == User32.WmQuit)
            {
                return;
            }

            if (!User32.PeekMessage(out message, window, 0, 0, User32.PmRemove))
            {
                return;
            }

            _ = User32.TranslateMessage(in message);
            _ = User32.DispatchMessage(in message);
        }
    }

    /// <summary>Lấy hình chữ nhật của cửa sổ trên màn hình theo **pixel vật lý**.</summary>
    /// <remarks>
    /// Bọc ở đây thay vì để tầng giao diện gọi thẳng interop: một mặt khai báo nội bộ không nên lọt ra ngoài,
    /// mặt khác việc "giá trị này là pixel vật lý" cần một kiểu tường minh (<see cref="PixelRect"/>) để mang theo,
    /// nếu không rất dễ dùng lẫn với DIP của WPF.
    /// </remarks>
    public static PixelRect? GetWindowPixelBounds(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        return Display32.GetWindowRect(window, out var rect)
            ? ToPixelRect(rect)
            : null;
    }

    private static string DescribeContext(nint context)
    {
        if (context == ContextUnaware) return "UNAWARE";
        if (context == ContextSystemAware) return "SYSTEM_AWARE";
        if (context == ContextPerMonitorAware) return "PER_MONITOR_AWARE";
        if (context == ContextPerMonitorAwareV2) return "PER_MONITOR_AWARE_V2";
        if (context == ContextUnawareGdiScaled) return "UNAWARE_GDISCALED";
        return $"Không xác định (0x{context:X})";
    }

    /// <summary>
    /// Lấy DPI của màn hình.
    /// </summary>
    /// <remarks>
    /// Ưu tiên dùng <c>GetDpiForMonitor</c> (Shcore), nó truy vấn theo handle màn hình nên trực tiếp nhất cho mục đích này;
    /// khi thất bại thì lùi về DPI hệ thống thay vì ném ngoại lệ — việc liệt kê màn hình không nên đứt gãy toàn bộ chỉ vì một truy vấn lỗi.
    /// </remarks>
    private static uint DpiForMonitor(nint monitor)
    {
        try
        {
            var result = Shcore.GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var dpiX, out _);
            if (result == 0 && dpiX > 0)
            {
                return dpiX;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Không có Shcore cũng không sao, bên dưới vẫn còn đường lùi.
        }

        var systemDpi = Dpi32.GetDpiForSystem();
        return systemDpi == 0 ? 96 : systemDpi;
    }

    private static PixelRect ToPixelRect(Rect rect) =>
        new(rect.Left, rect.Top, rect.Width, rect.Height);
}

internal enum MonitorDpiType
{
    /// <summary>DPI thực tế đang có hiệu lực. Tuyệt đại đa số trường hợp cần chính giá trị này.</summary>
    Effective = 0,

    Angular = 1,
    Raw = 2,
}

internal static partial class Shcore
{
    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);
}