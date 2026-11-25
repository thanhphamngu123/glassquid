using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Glassquid.DesktopAcceptance;

/// <summary>Kết quả của một lần nhấp chuột thật, mang theo cả lý do «tại sao không nhấp được».</summary>
internal sealed record ClickOutcome(bool Clicked, string Detail);

/// <summary>
/// Định vị cửa sổ và điều khiển bằng Windows UI Automation.
/// </summary>
/// <remarks>
/// Việc định vị <strong>chỉ dựa theo danh tính</strong>: tiến trình, tên lớp cửa sổ, loại điều khiển và mã định danh tự động hóa.
/// Trong trình điều khiển không có bất kỳ đoạn mã nào «ép tọa độ vào một hình chữ nhật nào đó» — nguyên nhân gốc rễ của thất bại vòng trước chính là
/// dùng cửa sổ nhỏ tìm được để áp tọa độ của hộp thoại gốc, dẫn đến nhấp ra ngoài cửa sổ
/// (chi tiết xem <c>docs/Giai đoạn 4 - Báo cáo nghiệm thu tương tác tự động.md</c>).
///
/// Client UIA được quản lý không có tham số quá thời gian, cuộc gọi liên tiến trình bị kẹt sẽ làm treo toàn bộ trình điều khiển,
/// nên mỗi lần tìm kiếm có khả năng bị chặn đều được bọc trong luồng canh gác của <see cref="Guarded{T}"/>.
/// Quá thời gian chỉ là từ bỏ cuộc gọi lần đó, bản thân luồng canh gác không thể hủy — đây là hạn chế đã biết của client được quản lý,
/// do đó trình điều khiển chỉ tìm trong phạm vi nhỏ (cửa sổ cấp cao nhất và cây con của một hộp thoại đơn lẻ), không duyệt toàn bộ màn hình nền.
/// </remarks>
internal static class Uia
{
    /// <summary>Chờ một cửa sổ cấp cao nhất xuất hiện bên ngoài tiến trình này.</summary>
    internal static AutomationElement? WaitForTopWindow(
        int processId,
        string? className,
        string? nameContains,
        int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var found = FindTopWindow(processId, className, nameContains);
            if (found is not null)
            {
                return found;
            }

            Thread.Sleep(200);
        }

        return null;
    }

    /// <summary>Tìm một lần, không chờ.</summary>
    internal static AutomationElement? FindTopWindow(int processId, string? className, string? nameContains)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);

        var matches = Guarded(
            () => AutomationElement.RootElement
                .FindAll(TreeScope.Children, condition)
                .Cast<AutomationElement>()
                .ToList(),
            8000);

        if (matches.Value is null)
        {
            return null;
        }

        foreach (var window in matches.Value)
        {
            if (className is not null && !string.Equals(Class(window), className, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (nameContains is not null && !Name(window).Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return window;
        }

        return null;
    }

    /// <summary>
    /// Chờ một cửa sổ đạt điều kiện trong toàn bộ các cửa sổ cấp cao nhất (không giới hạn tiến trình).
    /// </summary>
    /// <remarks>
    /// File Explorer không phải tiến trình do chúng ta khởi chạy, không biết trước pid, nên chỉ có thể nhận diện bằng tên lớp cửa sổ
    /// (<c>CabinetWClass</c>) kết hợp với «tiêu đề có chứa tên thư mục duy nhất của lần này».
    /// Tên thư mục duy nhất do vòng này tự tạo ra, do đó sẽ không nhận nhầm sang cửa sổ mà người dùng đã mở sẵn.
    /// </remarks>
    internal static AutomationElement? WaitForWindow(
        string? className,
        string? nameContains,
        int timeoutMs,
        Func<AutomationElement, bool>? accept = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var windows = Guarded(
                () => AutomationElement.RootElement
                    .FindAll(TreeScope.Children, Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .ToList(),
                8000);

            foreach (var window in windows.Value ?? [])
            {
                if (className is not null && !string.Equals(Class(window), className, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (nameContains is not null && !Name(window).Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (accept is not null && !accept(window))
                {
                    continue;
                }

                return window;
            }

            Thread.Sleep(250);
        }

        return null;
    }

    /// <summary>Phần tử cha trong dạng xem điều khiển. Dùng để đọc «ô nhập liệu này thuộc về nhãn nào».</summary>
    internal static AutomationElement? Parent(AutomationElement element)
    {
        if (element is null)
        {
            return null;
        }

        var parent = Guarded(() => TreeWalker.ControlViewWalker.GetParent(element), 4000);
        return parent.Value;
    }

    /// <summary>Toàn bộ các phần tử cửa sổ cấp cao nhất của một tiến trình nào đó.</summary>
    internal static IReadOnlyList<AutomationElement> TopWindowsOf(int processId)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);

        var found = Guarded(
            () => AutomationElement.RootElement
                .FindAll(TreeScope.Children, condition)
                .Cast<AutomationElement>()
                .ToList(),
            8000);

        return found.Value ?? [];
    }

    /// <summary>
    /// Chờ một hộp thoại gốc thuộc tiến trình đó (lớp cửa sổ <c>#32770</c>).
    /// </summary>
    /// <remarks>
    /// <b>Không được chỉ tìm các cửa sổ con trực tiếp của màn hình nền.</b> Thực nghiệm cho thấy (xem
    /// <c>docs/evidence/Giai đoạn 4 - Nhật ký hiệu chuẩn trình điều khiển máy tính để bàn.md</c>): trình chọn tệp là cửa sổ cấp cao nhất được sở hữu bởi cửa sổ máy chủ,
    /// trong dạng xem điều khiển của UIA nó <strong>nằm dưới cửa sổ máy chủ</strong>, chứ không phải nút con trực tiếp của màn hình nền.
    /// Chỉ tìm ở nút con trực tiếp sẽ dẫn tới kết luận sai là «sản phẩm không mở trình chọn».
    /// Vì vậy đi cả hai đường cùng lúc: tìm ở nút con trực tiếp trước, sau đó tìm trong cây con của cửa sổ máy chủ.
    /// </remarks>
    internal static AutomationElement? WaitForDialog(int processId, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var dialog = FindDialog(processId);
            if (dialog is not null)
            {
                return dialog;
            }

            Thread.Sleep(200);
        }

        return FindDialog(processId);
    }

    internal static AutomationElement? FindDialog(int processId)
    {
        var direct = FindTopWindow(processId, "#32770", null);
        if (direct is not null)
        {
            return direct;
        }

        foreach (var window in TopWindowsOf(processId))
        {
            var nested = Descendants(window, ControlType.Window)
                .FirstOrDefault(candidate => string.Equals(Class(candidate), "#32770", StringComparison.OrdinalIgnoreCase));

            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Lấy các hậu duệ theo loại điều khiển.</summary>
    internal static IReadOnlyList<AutomationElement> Descendants(AutomationElement root, ControlType type)
    {
        var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, type);

        var found = Guarded(
            () => root.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().ToList(),
            8000);

        return found.Value ?? [];
    }

    internal static IReadOnlyList<AutomationElement> Descendants(AutomationElement root)
    {
        var found = Guarded(
            () => root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToList(),
            12000);

        return found.Value ?? [];
    }

    /// <summary>Các phần tử con trực tiếp. Dùng nó để đọc cấu trúc cửa sổ rẻ hơn nhiều so với descendants.</summary>
    internal static IReadOnlyList<AutomationElement> Children(AutomationElement root)
    {
        var found = Guarded(
            () => root.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().ToList(),
            6000);

        return found.Value ?? [];
    }

    internal static string Name(AutomationElement element) => SafeOr(() => element.Current.Name, string.Empty);

    internal static string Class(AutomationElement element) => SafeOr(() => element.Current.ClassName, string.Empty);

    internal static string AutomationId(AutomationElement element) => SafeOr(() => element.Current.AutomationId, string.Empty);

    internal static string ControlKind(AutomationElement element) =>
        SafeOr(() => element.Current.ControlType?.ProgrammaticName ?? "?", "?");

    internal static bool IsEnabled(AutomationElement element) => SafeOr(() => element.Current.IsEnabled, false);

    /// <remarks>Khi không đọc được thì xử lý là «không nhìn thấy»: điều khiển không nhìn thấy không thể coi là ứng viên có thể nhấp.</remarks>
    internal static bool IsOffscreen(AutomationElement element) => SafeOr(() => element.Current.IsOffscreen, true);

    internal static nint Handle(AutomationElement element) => SafeOr(() => element.Current.NativeWindowHandle, 0);

    internal static int ProcessIdOf(AutomationElement element) => SafeOr(() => element.Current.ProcessId, 0);

    internal static ScreenRect RectOf(AutomationElement element)
    {
        var rect = SafeOr<System.Windows.Rect?>(() => element.Current.BoundingRectangle, null);

        if (rect is not { } value || value.IsEmpty || double.IsNaN(value.Width) || double.IsNaN(value.Height))
        {
            return default;
        }

        return new ScreenRect(
            (int)Math.Round(value.Left),
            (int)Math.Round(value.Top),
            (int)Math.Round(value.Right),
            (int)Math.Round(value.Bottom));
    }

    internal static string Describe(AutomationElement element) =>
        $"Loại {ControlKind(element)} | Tên «{Name(element)}» | Mã tự động hóa «{AutomationId(element)}»"
        + $" | Lớp {Class(element)} | Hình chữ nhật {RectOf(element).Describe()} | Hiển thị {!IsOffscreen(element)} | Khả dụng {IsEnabled(element)}";

    /// <summary>Đọc văn bản của điều khiển Edit. Dùng để đối chiếu tại chỗ «sản phẩm thực sự đã nhận được gì».</summary>
    internal static string? ValueOf(AutomationElement element)
    {
        return SafeOr<string?>(
            () =>
            {
                if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                {
                    return null;
                }

                return ((ValuePattern)pattern).Current.Value;
            },
            null);
    }

    /// <summary>
    /// Nhấp chuột thật vào tâm điều khiển.
    /// </summary>
    /// <remarks>
    /// Bốn bước kiểm tra tiên quyết không thể thiếu bước nào, chúng tách biệt «có nhấp trúng không» và «sản phẩm có phản hồi không»:
    /// hình chữ nhật khác rỗng, điểm nằm trong màn hình nền ảo, sau khi di chuyển điểm vẫn nằm trong hình chữ nhật của điều khiển, cửa sổ cấp cao nhất tại điểm đó thuộc về tiến trình chứa điều khiển.
    /// Bất kỳ điều kiện nào không thỏa đều trả về chưa nhấp được, bên gọi ghi nhận «bị chặn» chứ không ghi nhận sản phẩm thất bại.
    /// </remarks>
    internal static ClickOutcome Click(AutomationElement element, string label, int settleMs = 150)
    {
        ArgumentNullException.ThrowIfNull(element);

        var rect = RectOf(element);
        if (rect.IsEmpty)
        {
            return new ClickOutcome(false, $"{label}: điều khiển không có hình chữ nhật để nhấp (có thể chưa được kết xuất). {Describe(element)}");
        }

        var target = new ScreenPoint(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));
        var elementProcess = ProcessIdOf(element);
        var area = Win32.VirtualScreen();

        if (!area.Contains(target))
        {
            return new ClickOutcome(false, $"{label}: tâm điều khiển {target.X},{target.Y} không nằm trong màn hình nền ảo {area.Describe()}.");
        }

        // Các cửa sổ bật lên nhất thời như biểu ngữ thông báo của Windows sẽ che khuất điều khiển. Thử nghiệm thực tế trên máy khách từng gặp: một «thông báo mới»
        // đè đúng lên ô tên tệp và nút mở của hộp thoại tệp. Trong tình huống đó nhấp mù một phát đồng nghĩa với nhấp trúng thứ khác,
        // nên ở đây thử lại vài lần đối với việc «cửa sổ tại điểm nhấp có thuộc điều khiển này không», đợi cửa sổ bật lên tự biến mất;
        // đợi không được mới ghi nhận bị chặn, và ghi rõ cửa sổ nào đang chắn phía trước.
        ClickOutcome? lastRefusal = null;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            NativeInput.MoveTo(target);
            Thread.Sleep(settleMs);

            var moved = RectOf(element);
            if (!moved.IsEmpty && !moved.Contains(target))
            {
                return new ClickOutcome(false, $"{label}: sau khi đưa con trỏ vào, hình chữ nhật điều khiển đổi thành {moved.Describe()}, điểm {target.X},{target.Y} không còn nằm trên điều khiển.");
            }

            var top = Win32.WindowAt(target);
            var root = top == 0 ? 0 : Win32.RootAncestor(top);

            if (root != 0 && Win32.ProcessOf(root) == elementProcess)
            {
                NativeInput.LeftDown();
                Thread.Sleep(40);
                NativeInput.LeftUp();

                var note = attempt == 0 ? string.Empty : $" (đã đợi {attempt * 1500} ms mới tới lượt nó)";
                return new ClickOutcome(true, $"{label}: đã hoàn tất nhấn và thả chuột thật tại {target.X},{target.Y}{note}.");
            }

            var owner = Win32.Describe(root);
            lastRefusal = new ClickOutcome(
                false,
                $"{label}: cửa sổ cấp cao nhất phía trên điểm {target.X},{target.Y} không thuộc tiến trình của điều khiển (pid điều khiển {elementProcess}), "
                + $"thực tế là {owner?.Describe() ?? "(không có cửa sổ)"}. Có cửa sổ chắn phía trên, lần nhấp này không thể dùng làm bằng chứng.");

            Thread.Sleep(1500);
        }

        return lastRefusal ?? new ClickOutcome(false, $"{label}: không thể hoàn tất thao tác nhấp.");
    }

    /// <summary>
    /// Dùng tiêu điểm bàn phím thay vì nhấp chuột để đặt tiêu điểm vào điều khiển.
    /// </summary>
    /// <remarks>
    /// Đường lùi dành cho trường hợp «điểm bị cửa sổ bật lên dai dẳng che khuất»: biểu ngữ thông báo không tự biến mất, trong khi điểm chuột phải rơi vào
    /// nơi điều khiển thực sự hiển thị. SetFocus của UIA đi theo tiêu điểm bàn phím, không phụ thuộc vào việc có cửa sổ nào tại điểm đó không,
    /// sau đó nhập bằng phím bấm thật vẫn là đầu vào thật, không phải đi vòng qua sản phẩm.
    /// </remarks>
    internal static bool TryFocus(AutomationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var focused = SafeOr(
            () =>
            {
                element.SetFocus();
                return element.Current.HasKeyboardFocus;
            },
            false);

        return focused;
    }

    /// <summary>Thăm dò chờ đến khi điều kiện thỏa mãn.</summary>
    internal static bool Until(Func<bool> predicate, int timeoutMs, int pollMs = 150)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var deadline = Environment.TickCount64 + timeoutMs;

        do
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(pollMs);
        }
        while (Environment.TickCount64 < deadline);

        return predicate();
    }

    /// <summary>In một cây con ra ngoài để đối chiếu danh tính điều khiển trong lần «chạy lần đầu trên máy đích».</summary>
    internal static string Dump(AutomationElement root, int maxElements = 240, int maxDepth = 12)
    {
        var text = new StringBuilder();
        var visited = 0;

        Walk(root, 0);
        return text.ToString();

        void Walk(AutomationElement element, int depth)
        {
            if (visited >= maxElements || depth > maxDepth)
            {
                return;
            }

            visited++;
            text.Append(new string(' ', depth * 2))
                .Append("- ")
                .AppendLine(Describe(element));

            foreach (var child in Children(element))
            {
                Walk(child, depth + 1);
            }
        }
    }

    /// <summary>
    /// Thực thi một cuộc gọi UIA trên luồng canh gác có quá thời gian. Quá thời gian hoặc ngoại lệ đều trả về null,
    /// để bên gọi tự quyết định đây là «không tìm thấy» hay «không thể tra cứu».
    /// </summary>
    private static (T? Value, string? Error) Guarded<T>(Func<T> work, int timeoutMs)
    {
        T? value = default;
        string? error = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                value = work();
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or TimeoutException)
            {
                error = ex.Message;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "uia-guard",
        };

        thread.Start();

        if (!done.Wait(timeoutMs))
        {
            return (default, $"Cuộc gọi UIA quá {timeoutMs} ms chưa trả về.");
        }

        return (value, error);
    }

    /// <summary>
    /// Đọc một thuộc tính; khi phần tử đã biến mất thì trả về giá trị dự phòng đã cho thay vì ném ngoại lệ.
    /// </summary>
    /// <remarks>
    /// Cố ý không làm kiểu «trả về T có thể null»: <c>T?</c> không ràng buộc đối với kiểu giá trị chính là bản thân <c>T</c>,
    /// khi không đọc được sẽ âm thầm biến thành 0 hoặc false, bên gọi hoàn toàn không phân biệt được. Vì vậy giá trị dự phòng phải do bên gọi chỉ định rõ.
    /// </remarks>
    private static T SafeOr<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return fallback;
        }
    }
}