using System.Runtime.InteropServices;

namespace Glassquid.Platform.Interop;

/// <summary>
/// Các điểm vào cho cửa sổ, thông điệp và biểu tượng trong user32.
/// </summary>
/// <remarks>
/// Lớp được đánh dấu <c>unsafe</c> để vùng đệm cố định dùng con trỏ thay vì marshaling được quản lý:
/// marshaling được sinh từ mã nguồn chỉ chấp nhận tham số blittable, nhờ vậy mỗi lần lấy tên lớp/tiêu đề
/// sẽ không tạo ra mảng trung gian và cũng không cần quay lại dùng <c>DllImport</c>.
/// </remarks>
internal static unsafe partial class User32
{
    /// <summary>Chỉ mục kiểu của <c>GetWindowLong</c>.</summary>
    internal const int GwlStyle = -16;

    internal const int GwlExStyle = -20;

    /// <summary>Cửa sổ hiện đang ở trạng thái phóng to tối đa.</summary>
    internal const int WsMaximize = 0x01000000;

    /// <summary>
    /// Nhấp vào cửa sổ sẽ không đưa nó lên tiền cảnh. Thanh chính của dock dựa vào cờ này để đạt được
    /// hành vi "nhấp vào không chiếm tiêu điểm"; các cửa sổ bật lên cần tự lấy tiêu điểm phải chủ động
    /// đưa lên tiền cảnh qua <c>WindowActivation</c>.
    /// </summary>
    internal const int WsExNoactivate = 0x08000000;

    internal const int WsExLayered = 0x00080000;

    internal const uint LwaAlpha = 0x00000002;

    internal const uint SwpNoSize = 0x0001;

    internal const uint SwpNoMove = 0x0002;

    internal const uint SwpNoZOrder = 0x0004;

    internal const uint SwpNoActivate = 0x0010;

    internal const uint SwpFrameChanged = 0x0020;

    /// <summary>
    /// Dung lượng vùng đệm tên lớp. Quy ước Win32 giới hạn tên lớp không quá 256 ký tự.
    /// </summary>
    private const int ClassNameCapacity = 256;

    /// <summary>Dung lượng vùng đệm tiêu đề. Đủ dùng cho báo cáo chẩn đoán, nếu quá dài sẽ bị hệ thống cắt bớt.</summary>
    private const int WindowTextCapacity = 256;

    // ---- Hook sự kiện ----

    /// <summary>Phát sinh khi cửa sổ tiền cảnh **đổi sang cửa sổ khác**.</summary>
    /// <remarks>
    /// Nó chỉ phát sinh khi đổi cửa sổ: cùng một cửa sổ tự thay đổi kích thước (như trình duyệt nhấn F11 toàn màn hình,
    /// trình phát video chuyển toàn màn hình) sẽ **không** kích hoạt sự kiện này. Vì vậy phải đồng thời lắng nghe thay đổi vị trí.
    /// </remarks>
    internal const uint EventSystemForeground = 0x0003;

    /// <summary>Cửa sổ được tạo (<c>OBJID_WINDOW</c>).</summary>
    internal const uint EventObjectCreate = 0x8000;

    /// <summary>Cửa sổ bị hủy.</summary>
    internal const uint EventObjectDestroy = 0x8001;

    /// <summary>Cửa sổ được hiển thị (gồm cả thay đổi hiển thị khi khôi phục từ trạng thái ẩn hoặc thu nhỏ).</summary>
    internal const uint EventObjectShow = 0x8002;

    /// <summary>Cửa sổ bị ẩn.</summary>
    internal const uint EventObjectHide = 0x8003;

    /// <summary>Cửa sổ bắt đầu thu nhỏ.</summary>
    internal const uint EventSystemMinimizeStart = 0x0016;

    /// <summary>Cửa sổ kết thúc thu nhỏ.</summary>
    internal const uint EventSystemMinimizeEnd = 0x0017;

    /// <summary>Phát sinh khi vị trí hoặc kích thước cửa sổ thay đổi.</summary>
    internal const uint EventObjectLocationChange = 0x800B;

    /// <summary><c>OBJID_WINDOW</c>: chỉ quan tâm đến bản thân cửa sổ cấp cao nhất, bỏ qua các control con.</summary>
    internal const int ObjIdWindow = 0;

    /// <summary>Gọi lại trên hàng đợi thông điệp của luồng cài đặt hook, không cần tiêm callback vào tiến trình đích.</summary>
    internal const uint WineventOutOfContext = 0x0000;

    /// <summary>
    /// Bỏ qua các sự kiện cửa sổ do chính tiến trình này phát sinh.
    /// </summary>
    /// <remarks>
    /// Cố tình không sử dụng cờ này: khi cửa sổ của chính sản phẩm này trở thành cửa sổ tiền cảnh, kết quả phán đoán cũng cần được cập nhật
    /// (kết luận chuyển từ "toàn màn hình" về "không toàn màn hình"); bỏ qua sự kiện của chính mình sẽ khiến phán đoán bị kẹt ở giá trị cũ.
    /// Xem giải thích ở <c>FullscreenWatcher</c>.
    /// </remarks>
    internal const uint WineventSkipOwnProcess = 0x0002;

    // ---- Vòng lặp thông điệp ----

    internal const uint WmQuit = 0x0012;

    internal const uint PmRemove = 0x0001;

    internal const uint PmNoRemove = 0x0000;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);

    /// <summary>Cửa sổ tiền cảnh hiện tại; trả về 0 nếu không có cửa sổ tiền cảnh.</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    /// <summary>Cửa sổ màn hình nền của shell (<c>Progman</c>).</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();

    /// <summary>Cửa sổ màn hình nền ở tầng thấp nhất.</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint window);

    /// <summary>Cửa sổ đã thu nhỏ hay chưa. Cửa sổ đang thu nhỏ không thể phủ kín màn hình.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLong(nint window, int index);

    /// <summary>
    /// Ghi lại giá trị kiểu của cửa sổ, trả về giá trị trước khi thay đổi.
    /// </summary>
    /// <remarks>
    /// Chỉ dùng cho các giá trị kiểu **32-bit** như <see cref="GwlStyle"/>, <see cref="GwlExStyle"/>.
    /// Các chỉ mục <c>GWLP_*</c> có độ dài bằng con trỏ trên tiến trình 64-bit phải đi qua
    /// <c>SetWindowLongPtrW</c>, không thể dùng chung điểm vào này — đó là một loại giá trị khác.
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static partial int SetWindowLong(nint window, int index, int value);

    /// <summary>Lấy ID tiến trình và luồng sở hữu cửa sổ.</summary>
    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static partial int GetClassNameCore(nint window, char* buffer, int capacity);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static partial int GetWindowTextCore(nint window, char* buffer, int capacity);

    /// <summary>
    /// Ghi tên lớp cửa sổ vào vùng đệm do bên gọi cung cấp, trả về số ký tự đã ghi; thất bại trả về 0.
    /// </summary>
    /// <remarks>
    /// Cung cấp phiên bản span thay vì chỉ có phiên bản string để những kiểm tra như "cửa sổ này có phải cửa sổ shell không"
    /// không phải cấp phát một chuỗi chỉ để so sánh vài hằng chuỗi — nó sẽ nằm trên đường dẫn của mỗi lần thăm dò.
    /// </remarks>
    internal static int GetClassName(nint window, Span<char> destination)
    {
        if (window == 0 || destination.IsEmpty)
        {
            return 0;
        }

        fixed (char* buffer = destination)
        {
            return GetClassNameCore(window, buffer, destination.Length);
        }
    }

    /// <summary>Lấy tên lớp cửa sổ; trả về chuỗi rỗng khi thất bại.</summary>
    internal static string ReadClassName(nint window)
    {
        if (window == 0)
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[ClassNameCapacity];
        var length = GetClassName(window, buffer);
        return length <= 0 ? string.Empty : new string(buffer[..length]);
    }

    /// <summary>
    /// Lấy tiêu đề cửa sổ; trả về chuỗi rỗng khi thất bại hoặc không có tiêu đề.
    /// </summary>
    /// <remarks>
    /// Dùng <c>GetWindowText</c> thay vì <c>WM_GETTEXT</c>: với cửa sổ của tiến trình khác, nó đọc
    /// tiêu đề được hệ thống lưu đệm, tránh việc tiến trình đích bị treo làm lệnh gọi của chúng ta cũng bị treo theo.
    /// </remarks>
    internal static string ReadWindowText(nint window)
    {
        if (window == 0)
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[WindowTextCapacity];
        int length;

        fixed (char* pointer = buffer)
        {
            length = GetWindowTextCore(window, pointer, buffer.Length);
        }

        return length <= 0 ? string.Empty : new string(buffer[..length]);
    }

    // ---- Hook sự kiện và vòng lặp thông điệp ----

    internal delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time);

    /// <summary>
    /// Cài đặt hook sự kiện. Trả về 0 nghĩa là thất bại (ví dụ đã đạt giới hạn số lượng hook mà hệ thống cho phép).
    /// </summary>
    /// <remarks>
    /// Ở đây dùng <c>DllImport</c> thay vì marshaling sinh từ mã nguồn, tương tự lý do ở <c>Display32.EnumDisplayMonitors</c>:
    /// delegate callback không thể được marshal bởi trình sinh mã nguồn.
    /// Việc gỡ bỏ phải thực hiện trên chính luồng đã cài đặt hook, nếu không các sự kiện đã vào hàng đợi nhưng chưa điều phối sẽ mất nơi tiếp nhận.
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hookModule, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    /// <summary>Lấy một thông điệp; trả về 0 khi nhận được <c>WM_QUIT</c>, trả về -1 khi có lỗi.</summary>
    /// <remarks>
    /// Điểm vào phải ghi là <c>GetMessageW</c>: <c>GetMessage</c> trong tệp tiêu đề là một macro,
    /// trong DLL không có tên này. Các hàm cùng nhóm như <c>PeekMessage</c> / <c>DispatchMessage</c> /
    /// <c>PostThreadMessage</c> cũng tương tự.
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    internal static partial int GetMessage(out Msg message, nint window, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessage(out Msg message, nint window, uint min, uint max, uint remove);

    /// <summary><c>TranslateMessage</c> là hàm xuất thực sự, không có biến thể A/W.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(in Msg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in Msg message);

    /// <summary>Gửi thông điệp đến luồng chỉ định. Luồng đích phải đã có hàng đợi thông điệp.</summary>
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    // ---- Liệt kê và kích hoạt cửa sổ (Giai đoạn 3) ----

    /// <summary>Lấy cửa sổ chủ sở hữu của một cửa sổ; trả về 0 nếu không có chủ sở hữu.</summary>
    internal const int GwOwner = 4;

    /// <summary>Thu nhỏ cửa sổ.</summary>
    internal const int SwMinimize = 6;

    /// <summary>Hiển thị cửa sổ.</summary>
    internal const int SwShow = 5;

    /// <summary>Ẩn cửa sổ (không phải thu nhỏ: cửa sổ bị ẩn sẽ biến mất khỏi cả màn hình lẫn thanh tác vụ).</summary>
    internal const int SwHide = 0;

    /// <summary>Yêu cầu đóng cửa sổ (đi theo quy trình đóng của nó, nhẹ nhàng hơn ép buộc kết thúc tiến trình).</summary>
    internal const uint WmClose = 0x0010;

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    /// <summary>
    /// Đọc một giá trị atom trong bảng thuộc tính của cửa sổ. Khi OLE đăng ký đích kéo-thả sẽ để lại dấu hiệu với tên
    /// <c>OleDropTargetInterface</c>; việc dò tìm quyền sở hữu đăng ký kéo-thả dựa vào nó.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetPropW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetProp(nint window, string atom);

    /// <summary>Mã phím ảo của nút chuột chính.</summary>
    internal const int VirtualKeyLeftButton = 0x01;

    /// <summary>
    /// Trạng thái vật lý tức thời của phím (bit cao nhất = đang nhấn tại thời điểm này).
    /// </summary>
    /// <remarks>
    /// Khác biệt với <c>GetKeyState</c>: hàm kia đọc trạng thái phím trong hàng đợi thông điệp của luồng gọi,
    /// còn trong vòng lặp modal kéo-thả thì luồng giao diện không điều phối thông điệp nên khi đó nó trả về giá trị cũ.
    /// </remarks>
    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int virtualKey);

    /// <summary>Khôi phục một cửa sổ đang thu nhỏ và kích hoạt nó.</summary>
    internal const int SwRestore = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint window, uint command);

    /// <summary>Đưa cửa sổ lên đỉnh thứ tự z.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(nint window);

    /// <summary>
    /// Gắn tạm thời hàng đợi đầu vào của luồng hiện tại vào một luồng khác.
    /// </summary>
    /// <remarks>
    /// Dùng để vượt qua khóa tiền cảnh: <c>SetForegroundWindow</c> chỉ cho phép tiến trình "hiện đang giữ quyền tiền cảnh" gọi,
    /// mà quyền tiền cảnh thuộc về toàn bộ hàng đợi đầu vào. Sau khi tự gắn mình vào luồng sở hữu cửa sổ tiền cảnh,
    /// lần gọi này nằm trong cùng một hàng đợi nên hệ thống không từ chối nữa.
    /// Phải gọi theo cặp (<c>true</c> rồi sau đó <c>false</c>), nếu không đầu vào của hai luồng sẽ bị buộc chặt vào nhau mãi.
    /// </remarks>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attachFlag);

    internal delegate int EnumWindowsProc(nint window, nint parameter);

    /// <summary>Lấy cửa sổ thanh tác vụ chính của Windows để dùng cho thông điệp trạng thái thanh tác vụ của Shell.</summary>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindow(string? className, string? windowName);

    /// <summary>
    /// Liệt kê các cửa sổ cấp cao nhất từ trên xuống dưới (theo thứ tự z).
    /// </summary>
    /// <remarks>
    /// Delegate callback không thể được marshal bởi trình sinh mã nguồn, vì vậy ở đây dùng <c>DllImport</c>,
    /// tương tự như <see cref="SetWinEventHook"/>.
    /// </remarks>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(nint window, EnumWindowsProc callback, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLayeredWindowAttributes(nint window, out uint colorKey, out byte alpha, out uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

    // ---- Đọc pixel của biểu tượng (Giai đoạn 3) ----

    /// <summary>Tách một <c>HICON</c> thành các handle bitmap để đọc pixel.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetIconInfo(nint icon, out IconInfo iconInfo);
}

/// <summary>
/// Win32 <c>MSG</c>. Chỉ dùng <see cref="Message"/>, các trường còn lại để giữ bố cục khớp với cấu trúc gốc.
/// </summary>
/// <remarks>
/// Bố cục trường trên x64 là 48 byte, giống với <c>MSG</c> gốc; <c>GetMessage</c> sẽ ghi vào toàn bộ khối cấu trúc,
/// nên struct phía quản lý không được nhỏ hơn cấu trúc gốc.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    internal nint Window;
    internal uint Message;
    internal nuint WParam;
    internal nint LParam;
    internal uint Time;
    internal int PointX;
    internal int PointY;
}
