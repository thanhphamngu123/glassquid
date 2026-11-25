using System.Runtime.InteropServices;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>Một cửa sổ cấp cao nhất ứng viên.</summary>
/// <param name="Handle">Handle của cửa sổ.</param>
/// <param name="Title">Tiêu đề, có thể rỗng.</param>
/// <param name="ProcessId">Id tiến trình sở hữu.</param>
/// <param name="IsMinimized">Hiện đang ở trạng thái thu nhỏ.</param>
public sealed record WindowCandidateInfo(nint Handle, string Title, uint ProcessId, bool IsMinimized);

/// <summary>Một cửa sổ cấp cao nhất ứng viên kèm theo thông tin danh tính ở cả hai phía của nó.</summary>
/// <param name="ImagePath">Đường dẫn đầy đủ của tệp ảnh tiến trình sở hữu cửa sổ; là null nếu không đọc được.</param>
/// <param name="WindowAumid">AppUserModelID được thiết lập tường minh trên cửa sổ; là null nếu chưa thiết lập.</param>
/// <param name="ProcessAumid">AppUserModelID của tiến trình sở hữu; là null đối với tiến trình Win32 thông thường.</param>
public sealed record WindowIdentityInfo(
    nint Handle,
    string Title,
    uint ProcessId,
    bool IsMinimized,
    string? ImagePath,
    string? WindowAumid,
    string? ProcessAumid)
{
    /// <summary>
    /// «Danh tính hiệu lực» của cửa sổ này.
    /// </summary>
    /// <remarks>
    /// Danh tính được thiết lập tường minh trên cửa sổ được ưu tiên — nó cụ thể hơn danh tính của tiến trình. Ví dụ điển hình nhất là
    /// cửa sổ UWP được lưu trữ bởi ApplicationFrameHost: bản thân tiến trình máy chủ không có danh tính ứng dụng,
    /// trong khi thứ gắn trên cửa sổ mới là danh tính thực sự của ứng dụng được lưu trữ. Trường hợp cả hai bên đều có nhưng không khớp nhau
    /// được xử lý là «không đáng tin cậy», bên gọi không được ép khớp lại bằng đường dẫn tiến trình máy chủ,
    /// xem quy tắc tại <c>RunningAppTracker</c>.
    /// </remarks>
    public string? EffectiveAumid => WindowAumid ?? ProcessAumid;
}

/// <summary>
/// Tìm ra cửa sổ cấp cao nhất thuộc về một chương trình nào đó và đưa nó lên tiền cảnh.
/// </summary>
/// <remarks>
/// Việc xác định quyền sở hữu dùng «đường dẫn đầy đủ của tệp ảnh tiến trình», không dùng tên tiến trình. Độ chi tiết của tên tiến trình quá thô —
/// người dùng có thể cài nhiều phiên bản của cùng một chương trình, hoặc có thể có chương trình khác vô tình trùng tên.
/// Đường dẫn là duy nhất.
///
/// <para>
/// Lấy đường dẫn tệp ảnh bằng <c>PROCESS_QUERY_LIMITED_INFORMATION</c>,
/// nó cũng có hiệu lực với tiến trình <strong>đã nâng quyền</strong>; đổi sang <c>PROCESS_QUERY_INFORMATION</c>
/// sẽ bị từ chối truy cập ngay lập tức, dẫn đến việc «chương trình chạy với quyền quản trị viên» không bao giờ khớp được cửa sổ.
/// </para>
/// </remarks>
public static class WindowLocator
{
    /// <summary>
    /// Liệt kê tất cả các cửa sổ cấp cao nhất ứng viên có «danh tính hiệu lực» bằng với AppUserModelID đã cho.
    /// </summary>
    /// <remarks>
    /// Danh tính hiệu lực = danh tính được thiết lập tường minh trên cửa sổ, khi không đọc được thì lùi về danh tính gói của tiến trình sở hữu
    /// (<see cref="WindowIdentityInfo.EffectiveAumid"/>). Kết quả đo thực tế từ đầu dò Giai đoạn 4:
    /// cửa sổ khung UWP (được lưu trữ bởi ApplicationFrameHost) đọc từ phía cửa sổ, ứng dụng đóng gói
    /// đọc từ phía tiến trình, kết hợp cả hai đường mới bao phủ hết. Trả về danh sách rỗng khi không tìm thấy khớp —
    /// bên gọi xử lý theo hướng «không có cửa sổ đang chạy», tuyệt đối không lùi về đường dẫn tệp ảnh của tiến trình máy chủ:
    /// hai ứng dụng UWP dùng chung một tiến trình máy chủ, khớp theo máy chủ sẽ nhận nhầm cửa sổ của ứng dụng A
    /// thành của ứng dụng B.
    /// </remarks>
    public static IReadOnlyList<WindowCandidateInfo> FindWindowsForAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return [];
        }

        var matches = new List<WindowCandidateInfo>();

        foreach (var window in EnumerateTopLevelWindows())
        {
            if (string.Equals(window.EffectiveAumid, aumid, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new WindowCandidateInfo(window.Handle, window.Title, window.ProcessId, window.IsMinimized));
            }
        }

        return matches;
    }

    /// <summary>
    /// Liệt kê tất cả các cửa sổ cấp cao nhất hiển thị và có tiêu đề khớp với đường dẫn tệp ảnh đã cho.
    /// </summary>
    /// <param name="imagePath">Đường dẫn đầy đủ của đích. Trả về danh sách rỗng nếu là null hoặc rỗng.</param>
    public static IReadOnlyList<WindowCandidateInfo> FindWindowsForImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return [];
        }

        var normalized = NormalizePath(imagePath);
        var matches = new List<WindowCandidateInfo>();

        // Cùng một tiến trình có thể mở nhiều cửa sổ, hỏi đường dẫn tệp ảnh một lần là đủ.
        var processPaths = new Dictionary<uint, string?>();

        try
        {
            _ = User32.EnumWindows(
                (window, parameter) =>
                {
                    if (!IsCandidateWindow(window))
                    {
                        return 1;
                    }

                    _ = User32.GetWindowThreadProcessId(window, out var processId);
                    if (processId == 0)
                    {
                        return 1;
                    }

                    if (!processPaths.TryGetValue(processId, out var path))
                    {
                        path = TryReadImagePath(processId);
                        processPaths[processId] = path;
                    }

                    if (path is not null && string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(new WindowCandidateInfo(
                            window,
                            User32.ReadWindowText(window),
                            processId,
                            User32.IsIconic(window)));
                    }

                    return 1;
                },
                0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }

        return matches;
    }

    /// <summary>
    /// Liệt kê tất cả các cửa sổ cấp cao nhất đáng được coi là «cửa sổ của một ứng dụng nào đó», kèm theo danh tính hai phía.
    /// </summary>
    /// <remarks>
    /// Nguồn đối soát cho việc xác định trạng thái chạy (D-07): sự kiện chỉ đóng vai trò tín hiệu kích hoạt,
    /// tập cửa sổ thực sự lấy kết quả liệt kê tại đây làm chuẩn. Việc đọc danh tính có chi phí
    /// (mỗi cửa sổ một lần truy vấn kho thuộc tính, mỗi tiến trình một lần mở handle),
    /// nhưng số lượng cửa sổ ứng viên thường chỉ ở mức vài chục, và việc đối soát chỉ do sự kiện kích hoạt, nên có thể chấp nhận được.
    /// </remarks>
    public static IReadOnlyList<WindowIdentityInfo> EnumerateTopLevelWindows()
    {
        var windows = new List<WindowIdentityInfo>();
        var imagePaths = new Dictionary<uint, string?>();
        var processAumids = new Dictionary<uint, string?>();

        try
        {
            _ = User32.EnumWindows(
                (window, parameter) =>
                {
                    if (!IsCandidateWindow(window))
                    {
                        return 1;
                    }

                    _ = User32.GetWindowThreadProcessId(window, out var processId);
                    if (processId == 0)
                    {
                        return 1;
                    }

                    if (!imagePaths.TryGetValue(processId, out var imagePath))
                    {
                        imagePath = TryReadImagePath(processId);
                        imagePaths[processId] = imagePath;
                    }

                    if (!processAumids.TryGetValue(processId, out var processAumid))
                    {
                        processAumid = WindowIdentity.TryGetProcessAumid(processId);
                        processAumids[processId] = processAumid;
                    }

                    windows.Add(new WindowIdentityInfo(
                        window,
                        User32.ReadWindowText(window),
                        processId,
                        User32.IsIconic(window),
                        imagePath,
                        WindowIdentity.TryGetWindowAumid(window),
                        processAumid));

                    return 1;
                },
                0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }

        return windows;
    }

    /// <summary>
    /// Đưa cửa sổ lên tiền cảnh.
    /// </summary>
    /// <remarks>
    /// Cơ chế <strong>khóa tiền cảnh</strong> của Windows sẽ từ chối yêu cầu chuyển lên tiền cảnh từ tiến trình «lúc này không được người dùng thao tác»,
    /// trả về <c>false</c> thay vì báo lỗi. Khi người dùng nhấn vào biểu tượng trên thanh dock, tiến trình của chúng ta vốn đã nhận được quyền tiền cảnh,
    /// nên luồng bình thường sẽ thành công ngay; khi thất bại sẽ hạ cấp theo thứ tự dưới đây:
    /// <list type="number">
    /// <item>Cửa sổ đang thu nhỏ thì khôi phục trước — gọi chuyển lên tiền cảnh với cửa sổ đang thu nhỏ vốn không có tác dụng;</item>
    /// <item>Gắn luồng hiện tại vào hàng đợi nhập liệu của luồng sở hữu cửa sổ tiền cảnh rồi thử lại lần nữa;</item>
    /// <item>Vẫn không được thì dùng <c>BringWindowToTop</c>, ít nhất đưa nó lên trước theo thứ tự z.</item>
    /// </list>
    /// Cả ba bước đều không thành công thì trả về <c>false</c> đúng thực tế, để bên gọi hạ cấp thành thông báo «đã khởi chạy nhưng không thể đưa lên trước»,
    /// thay vì báo lỗi.
    /// </remarks>
    public static bool TryBringToForeground(nint window)
    {
        if (window == 0 || !User32.IsWindow(window))
        {
            return false;
        }

        try
        {
            if (User32.IsIconic(window))
            {
                _ = User32.ShowWindow(window, User32.SwRestore);
            }

            if (User32.SetForegroundWindow(window))
            {
                return true;
            }

            return TryWithAttachedInput(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryWithAttachedInput(nint window)
    {
        var foreground = User32.GetForegroundWindow();
        var ourThread = Kernel32.GetCurrentThreadId();
        var targetThread = foreground == 0 ? 0 : User32.GetWindowThreadProcessId(foreground, out _);

        if (targetThread == 0 || targetThread == ourThread)
        {
            return User32.BringWindowToTop(window);
        }

        if (!User32.AttachThreadInput(ourThread, targetThread, true))
        {
            return User32.BringWindowToTop(window);
        }

        try
        {
            _ = User32.BringWindowToTop(window);
            return User32.SetForegroundWindow(window);
        }
        finally
        {
            // Không tháo cặp gắn kết sẽ khiến hàng đợi nhập liệu của hai luồng bị buộc chặt vào nhau vĩnh viễn —
            // biểu hiện là hành vi tiêu điểm bàn phím bị rối loạn, và rất khó điều tra.
            _ = User32.AttachThreadInput(ourThread, targetThread, false);
        }
    }

    /// <summary>
    /// Một cửa sổ có đáng được coi là ứng viên «cửa sổ của chương trình này» hay không.
    /// </summary>
    /// <remarks>
    /// Phải thỏa mãn cả ba điều kiện: hiển thị, có tiêu đề, không phải cửa sổ công cụ. Điều cuối cùng đặc biệt quan trọng —
    /// rất nhiều chương trình giữ lại một cửa sổ thông điệp ẩn, không tiêu đề; tính cả chúng vào sẽ khiến
    /// «chỉ có một cửa sổ» biến thành «có nhiều cửa sổ», khiến thanh dock bật danh sách lựa chọn.
    /// </remarks>
    private static bool IsCandidateWindow(nint window)
    {
        if (!User32.IsWindowVisible(window))
        {
            return false;
        }

        if (User32.GetWindow(window, User32.GwOwner) != 0)
        {
            return false;
        }

        if (User32.ReadWindowText(window).Length == 0)
        {
            return false;
        }

        // WS_EX_TOOLWINDOW: cửa sổ phụ trợ không thuộc thanh tác vụ.
        const int GwlExStyle = -20;
        const int WsExToolWindow = 0x00000080;

        var extendedStyle = User32.GetWindowLong(window, GwlExStyle);
        return (extendedStyle & WsExToolWindow) == 0;
    }

    private static string? TryReadImagePath(uint processId)
    {
        var handle = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            // Dùng trước một bộ đệm đủ lớn để hỏi xong trong một lần; với đường dẫn dài thì MAX_PATH không đủ.
            var buffer = new char[1024];
            var size = (uint)buffer.Length;

            if (!Kernel32.QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return null;
            }

            return size == 0 ? null : NormalizePath(new string(buffer, 0, (int)size));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            _ = Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Thu gọn về dạng có thể so sánh được.
    /// </summary>
    /// <remarks>
    /// Đường dẫn tệp ảnh có thể ở dạng thiết bị như <c>\??\C:\...</c> (tùy thuộc vào 32-bit/64-bit và cách truy vấn),
    /// cũng có thể mang tiền tố <c>\\?\</c>. Nếu không tước bỏ thì hai bên sẽ không bao giờ bằng nhau.
    /// </remarks>
    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();

        foreach (var prefix in new[] { @"\??\", @"\\?\", @"\SystemRoot\" })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[prefix.Length..];

                if (prefix == @"\SystemRoot\")
                {
                    trimmed = @"C:\Windows\" + trimmed;
                }

                break;
            }
        }

        var expanded = Environment.ExpandEnvironmentVariables(trimmed);

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return expanded;
        }
    }
}