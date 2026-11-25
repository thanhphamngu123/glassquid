using System.Runtime.InteropServices;

namespace Glassquid.DesktopAcceptance;

/// <summary>
/// Hệ thống đã từ chối đầu vào được chèn (inject).
/// </summary>
/// <remarks>
/// Tách riêng một loại ngoại lệ là vì nó biểu thị <strong>môi trường không đáp ứng</strong> chứ không phải sản phẩm bị lỗi:
/// khi <c>SendInput</c> bị UIPI (cách ly mức độ toàn vẹn) chặn thì sẽ trả về 0, và tài liệu chính thức nêu rõ
/// cả giá trị trả về lẫn <c>GetLastError</c> đều không cho biết nguyên nhân là do UIPI.
/// Trường hợp này phải được ghi nhận là "bị chặn", không được ghi nhận là "thất bại".
/// </remarks>
internal sealed class InputBlockedException : Exception
{
    internal InputBlockedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Chèn đầu vào chuột và bàn phím thực thông qua <c>SendInput</c>.
/// </summary>
/// <remarks>
/// Phần này là gốc của chuỗi bằng chứng "đầu vào desktop thực": nó đi qua cùng một hàng đợi
/// đầu vào hệ thống với thao tác thủ công của người dùng, do đó vòng lặp kéo-thả dạng modal của <c>DoDragDrop</c> và bộ bơm thông điệp của hộp thoại tệp gốc
/// đều sẽ xử lý các sự kiện này như đối với người dùng thật. Các sự kiện định tuyến (routed events) của WPF không làm được điều này.
///
/// Việc chèn diễn ra bất đồng bộ: khi <c>SendInput</c> trả về thì sự kiện mới chỉ vào hàng đợi, nên sau mỗi hành động
/// đều cần chừa thời gian quan sát, còn tiêu chí "đạt" vẫn là kết quả có thể quan sát được chứ không phải việc gọi hàm thành công.
/// </remarks>
internal static class NativeInput
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseWheel = 0x0800;

    private const uint KeyboardKeyUp = 0x0002;
    private const uint KeyboardUnicode = 0x0004;

    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyA = 0x41;
    private const ushort VirtualKeyEscape = 0x1B;
    private const ushort VirtualKeyReturn = 0x0D;
    private const ushort VirtualKeyHome = 0x24;
    private const ushort VirtualKeyDown = 0x28;

    private static readonly object Gate = new();

    /// <summary>Di chuyển con trỏ đến pixel tại tọa độ desktop ảo.</summary>
    internal static void MoveTo(ScreenPoint point)
    {
        var area = Win32.VirtualScreen();

        // Tọa độ tuyệt đối là giá trị chuẩn hóa từ 0..65535: mẫu số dùng "chiều dài cạnh - 1",
        // nếu không thì hàng pixel ngoài cùng bên phải / dưới cùng sẽ không bao giờ chạm tới được.
        var normalX = Normalize(point.X - area.Left, area.Width);
        var normalY = Normalize(point.Y - area.Top, area.Height);

        Send([new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    X = normalX,
                    Y = normalY,
                    Flags = MouseMove | MouseAbsolute | MouseVirtualDesk,
                },
            },
        }], "Di chuyển con trỏ");
    }

    internal static void LeftDown() => Send([Mouse(MouseLeftDown)], "Nhấn giữ chuột trái");

    internal static void LeftUp() => Send([Mouse(MouseLeftUp)], "Nhả chuột trái");

    internal static void RightClick(ScreenPoint point)
    {
        MoveTo(point); Thread.Sleep(150);
        Send([Mouse(0x0008), Mouse(0x0010)], "Nhấp chuột phải");
    }

    /// <summary>
    /// Một thao tác kéo hoàn chỉnh: nhấn giữ, di chuyển từng bước dọc theo đường dẫn, dừng di chuột nhẹ rồi nhả.
    /// </summary>
    /// <remarks>
    /// Giữa lúc nhấn giữ và nhả ra <strong>không chèn thêm bất kỳ thao tác nào khác</strong>, toàn bộ chuỗi được gửi xong trong một khóa nguyên tử —
    /// kéo-thả OLE yêu cầu trạng thái nhấn giữ và di chuyển phải liên tục, nếu chen đầu vào của tiến trình khác vào giữa thì có thể bị coi là hai thao tác riêng biệt.
    /// Việc dừng di chuột là để cửa sổ đích nhận được <c>DragOver</c> trước (việc xác định chấp nhận thả của WPF diễn ra tại đó).
    /// </remarks>
    internal static void DragPath(IReadOnlyList<ScreenPoint> path, int stepDelayMs = 25, int hoverBeforeReleaseMs = 250)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(path.Count, 2);

        lock (Gate)
        {
            MoveTo(path[0]);
            Thread.Sleep(150);

            LeftDown();
            Thread.Sleep(120);

            for (var index = 1; index < path.Count; index++)
            {
                MoveTo(path[index]);
                Thread.Sleep(stepDelayMs);
            }

            Thread.Sleep(hoverBeforeReleaseMs);
            LeftUp();
        }
    }

    /// <summary>Nhấn Ctrl+A (xóa sạch nội dung đã điền sẵn trong hộp thoại).</summary>
    internal static void SelectAll()
    {
        lock (Gate)
        {
            TypeKey(VirtualKeyControl, up: false);
            TypeKey(VirtualKeyA, up: false);
            TypeKey(VirtualKeyA, up: true);
            TypeKey(VirtualKeyControl, up: true);
        }
    }

    internal static void PressEscape() => Send([Key(VirtualKeyEscape, up: false), Key(VirtualKeyEscape, up: true)], "Nhấn Esc");

    /// <summary>Nhấn Enter. Trong hộp thoại, nhấn Enter tại ô tên tệp tương đương với "Mở".</summary>
    internal static void PressEnter() => Send([Key(VirtualKeyReturn, up: false), Key(VirtualKeyReturn, up: true)], "Nhấn Enter");
    internal static void PressHome() => Send([Key(VirtualKeyHome, up: false), Key(VirtualKeyHome, up: true)], "Nhấn Home");
    internal static void PressDown() => Send([Key(VirtualKeyDown, up: false), Key(VirtualKeyDown, up: true)], "Nhấn phím mũi tên xuống");

    internal static void WheelDown(int steps = 1)
    {
        for (var i = 0; i < steps; i++)
            Send([new Input { Type = InputMouse, Data = new InputUnion {
                Mouse = new MouseInput { MouseData = unchecked((uint)-120), Flags = MouseWheel } } }], "Cuộn chuột xuống");
    }

    /// <summary>
    /// Nhập văn bản từng ký tự một. <c>KEYEVENTF_UNICODE</c> gửi trực tiếp đơn vị mã UTF-16,
    /// nên đường dẫn có chữ tiếng Trung và dấu cách không đi qua bất kỳ bộ gõ hay chuyển đổi bố cục bàn phím nào.
    /// </summary>
    internal static void TypeText(string text, int characterDelayMs = 12)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (Gate)
        {
            foreach (var character in text)
            {
                Send(
                [
                    new Input
                    {
                        Type = InputKeyboard,
                        Data = new InputUnion
                        {
                            Keyboard = new KeyboardInput { VirtualKey = 0, ScanCode = character, Flags = KeyboardUnicode },
                        },
                    },
                    new Input
                    {
                        Type = InputKeyboard,
                        Data = new InputUnion
                        {
                            Keyboard = new KeyboardInput { VirtualKey = 0, ScanCode = character, Flags = KeyboardUnicode | KeyboardKeyUp },
                        },
                    },
                ], "Nhập ký tự");

                Thread.Sleep(characterDelayMs);
            }
        }
    }

    /// <summary>Nội suy một đoạn thẳng thành nhiều bước để phục vụ thao tác kéo.</summary>
    internal static IReadOnlyList<ScreenPoint> Interpolate(ScreenPoint from, ScreenPoint to, int steps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);

        var points = new List<ScreenPoint>(steps + 1) { from };

        for (var index = 1; index <= steps; index++)
        {
            var ratio = index / (double)steps;
            points.Add(new ScreenPoint(
                (int)Math.Round(from.X + ((to.X - from.X) * ratio)),
                (int)Math.Round(from.Y + ((to.Y - from.Y) * ratio))));
        }

        return points;
    }

    private static int Normalize(int offset, int extent)
    {
        if (extent <= 1)
        {
            return 0;
        }

        var clamped = Math.Clamp(offset, 0, extent - 1);
        return (int)Math.Round(clamped * 65535.0 / (extent - 1));
    }

    private static Input Mouse(uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Flags = flags } },
    };

    private static Input Key(ushort virtualKey, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = up ? KeyboardKeyUp : 0 },
        },
    };

    private static void TypeKey(ushort virtualKey, bool up)
    {
        Send([Key(virtualKey, up)], "Nhấn phím");
    }

    /// <summary>
    /// Gửi một loạt sự kiện. Nếu số lượng trả về không khớp với số lượng yêu cầu thì coi như bị từ chối (nhiều khả năng nhất là UIPI),
    /// ngoại lệ được ném ra là "bị chặn" chứ không phải "thất bại".
    /// </summary>
    private static void Send(Input[] inputs, string action)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

        if (sent == inputs.Length)
        {
            return;
        }

        throw new InputBlockedException(
            $"{action}: Đã yêu cầu chèn {inputs.Length} sự kiện, hệ thống chỉ chấp nhận {sent} sự kiện. "
            + "Điều này thường có nghĩa là mức độ toàn vẹn của tiến trình đích cao hơn trình điều khiển này (cách ly quyền UIPI). "
            + "Sản phẩm chưa được kiểm chứng nên không thể tính là thất bại.");
    }

    // ---- Bố cục cấu trúc ----

    /// <remarks>
    /// Bố cục phải khớp từng byte với <c>INPUT</c> của Win32. Trên x64 là 40 byte
    /// (4 byte loại + 4 byte căn chỉnh + 32 byte union); nếu không khớp sẽ âm thầm gửi sai sự kiện,
    /// vì vậy cần đối chiếu rõ ràng một lần khi khởi tạo.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    internal static void VerifyLayout()
    {
        var size = Marshal.SizeOf<Input>();
        var expected = IntPtr.Size == 8 ? 40 : 28;

        if (size != expected)
        {
            throw new InvalidOperationException(
                $"Kích thước cấu trúc INPUT là {size} byte, trên tiến trình {IntPtr.Size * 8}-bit lẽ ra phải là {expected} byte; "
                + "việc gửi đầu vào được chèn sẽ tạo ra sự kiện bị lệch, do đó từ chối tiếp tục.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
