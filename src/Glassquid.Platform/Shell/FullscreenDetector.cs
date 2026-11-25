using System.Diagnostics;
using System.Runtime.InteropServices;
using Glassquid.Platform.Displays;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>Kết quả phán đoán lấp đầy màn hình của cửa sổ tiền cảnh.</summary>
public enum FullscreenVerdict
{
    /// <summary>Không thể xác định (không có cửa sổ tiền cảnh hoặc lấy thông tin hình học thất bại).</summary>
    Unknown = 0,

    /// <summary>Cửa sổ tiền cảnh không lấp đầy màn hình chứa nó.</summary>
    NotFullscreen = 1,

    /// <summary>Cửa sổ tiền cảnh đã lấp đầy màn hình chứa nó.</summary>
    Fullscreen = 2,

    /// <summary>
    /// Cửa sổ tiền cảnh thuộc nhóm "không nên bị coi là ứng dụng toàn màn hình": màn hình nền, thanh tác vụ,
    /// cửa sổ của chính sản phẩm này, cửa sổ đã thu nhỏ hoặc không hiển thị.
    /// </summary>
    Excluded = 3,
}

/// <summary>A nullable verdict preserves the prior foreign window during session switches and own menus.</summary>
public readonly record struct PerformanceFullscreenObservation(bool? IsFullscreen, bool BelongsToCurrentProcess);

/// <summary>
/// Trạng thái thông báo người dùng do shell báo cáo, tức mã trả về của <c>SHQueryUserNotificationState</c>.
/// </summary>
/// <remarks>
/// Giá trị tương ứng một-một với các hằng số <c>QUNS_*</c> của Win32 một cách có chủ đích: để báo cáo có thể đối chiếu trực tiếp
/// với tài liệu hệ thống, thay vì thêm một lớp tên tự đặt.
/// </remarks>
public enum UserNotificationState
{
    /// <summary>Lần gọi này thất bại, hoặc hệ thống trả về giá trị mà sản phẩm không nhận diện được.</summary>
    Unknown = 0,

    /// <summary>Người dùng không có mặt (đã khóa màn hình hoặc đang chạy trình bảo vệ màn hình).</summary>
    NotPresent = 1,

    /// <summary>Đang chạy ứng dụng toàn màn hình hoặc đã bật cài đặt trình chiếu.</summary>
    Busy = 2,

    /// <summary>Đang chạy ứng dụng Direct3D toàn màn hình ở chế độ độc quyền.</summary>
    RunningD3dFullScreen = 3,

    /// <summary>Đã bật cài đặt trình chiếu (không nhất thiết là toàn màn hình).</summary>
    PresentationMode = 4,

    /// <summary>Trạng thái bình thường: có thể làm phiền người dùng.</summary>
    AcceptsNotifications = 5,

    /// <summary>Thời gian yên tĩnh (giờ đầu tiên sau khi tạo tài khoản người dùng mới).</summary>
    QuietTime = 6,

    /// <summary>Đang chạy ứng dụng Microsoft Store (UWP) ở chế độ toàn màn hình.</summary>
    App = 7,
}

/// <summary>Các tùy chọn cài đặt cho một lần phán đoán toàn màn hình.</summary>
public sealed record FullscreenOptions
{
    /// <summary>Cài đặt mặc định: loại trừ cửa sổ của chính tiến trình này.</summary>
    public static FullscreenOptions Default { get; } = new();

    /// <summary>
    /// Có loại trừ các cửa sổ của tiến trình hiện tại hay không.
    /// </summary>
    /// <remarks>
    /// Khi sản phẩm chạy phải đặt là <c>true</c>: Dock, bệ phóng, hoặc cửa sổ cài đặt một khi bị coi là "ứng dụng toàn màn hình",
    /// logic ức chế sẽ tự khóa chính mình — người dùng gọi Dock lên, cửa sổ của Dock trở thành cửa sổ tiền cảnh,
    /// thế là bị phán đoán thành "có ứng dụng toàn màn hình", và từ đó không phản hồi việc gọi lên nữa.
    /// Chỉ công cụ đo thực tế mới cần bật tùy chọn này, vì khi đo thực tế cần tạo cửa sổ toàn màn hình trong chính tiến trình của mình.
    /// </remarks>
    public bool ExcludeCurrentProcess { get; init; } = true;

    /// <summary>
    /// Có gộp trạng thái shell (luồng 2) vào phán đoán của truy vấn nhẹ hay không.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mặc định <c>false</c>: phán đoán tần suất cao trên đường dẫn kích hoạt chỉ dùng hình học cửa sổ vì nó ổn định và rẻ (đo thực tế trung vị 4.5 micro giây),
    /// trong khi lời gọi shell đo thực tế trung vị khoảng 200 micro giây, tệ nhất 2.5 mili giây và biến động theo trạng thái.
    /// </para>
    /// <para>
    /// Bật nó chỉ có giá trị trong một trường hợp: trò chơi toàn màn hình độc quyền (D3D). Loại cửa sổ đó không hẳn là cửa sổ tiền cảnh thông thường,
    /// hình học cửa sổ có thể không thấy được, trong khi shell có một trạng thái riêng (<c>QUNS_RUNNING_D3D_FULL_SCREEN</c>).
    /// Cái giá phải trả là đưa vào mặt thiếu tin cậy của luồng 2 — đo thực tế cho thấy nó không thấy cửa sổ toàn màn hình trên màn hình phụ, và cũng từng gặp nó báo Busy
    /// trong khi tiền cảnh không phải cửa sổ toàn màn hình; cả hai trường hợp này đều khiến việc ức chế bị quá gắt.
    /// </para>
    /// <para>
    /// Lưu ý nó chỉ ảnh hưởng đến <see cref="FullscreenDetector.IsFullscreenActive"/> (cũng như
    /// phán đoán của <see cref="FullscreenWatcher"/>); <see cref="FullscreenDetector.Probe"/> dù thế nào
    /// cũng sẽ đo cả hai luồng và báo cáo riêng rẽ.
    /// </para>
    /// </remarks>
    public bool IncludeShellState { get; init; }
}

/// <summary>Thông tin nhận dạng của cửa sổ tiền cảnh, chỉ dùng để hiển thị chẩn đoán.</summary>
public sealed record ForegroundWindowInfo(
    nint Handle,
    string ClassName,
    string Title,
    uint ProcessId,
    string ProcessName,
    bool IsMaximized)
{
    public override string ToString() =>
        $"Handle 0x{Handle:X}  Tên lớp {ClassName}  Tiến trình {ProcessName} ({ProcessId}){(IsMaximized ? "  Đã phóng to" : string.Empty)}{(string.IsNullOrEmpty(Title) ? string.Empty : $"  Tiêu đề \"{Title}\"")}";
}

/// <summary>Kết quả phán đoán của luồng 1: so sánh hình chữ nhật cửa sổ tiền cảnh với ranh giới màn hình chứa nó.</summary>
/// <param name="WindowRect">Hình chữ nhật của <c>GetWindowRect</c>, tính theo pixel vật lý. Bao gồm viền điều chỉnh kích thước vô hình của cửa sổ phóng to.</param>
/// <param name="VisibleFrame">Viền hiển thị do DWM báo cáo, tính theo pixel vật lý. Bằng null nếu lấy giá trị thất bại.</param>
public sealed record WindowPathResult(
    FullscreenVerdict Verdict,
    string Reason,
    PixelRect? WindowRect,
    PixelRect? VisibleFrame,
    PixelRect? MonitorBounds,
    string MonitorDeviceName,
    TimeSpan Duration)
{
    /// <summary>Kết luận của luồng này; bằng null khi bị loại trừ hoặc không thể xác định (tức "không có kết luận").</summary>
    public bool? IsFullscreen => Verdict switch
    {
        FullscreenVerdict.Fullscreen => true,
        FullscreenVerdict.NotFullscreen => false,
        _ => null,
    };
}

/// <summary>Kết quả phán đoán của luồng 2: trạng thái thông báo người dùng của shell.</summary>
public sealed record ShellPathResult(
    UserNotificationState State,
    bool IsFullscreen,
    string? Error,
    TimeSpan Duration)
{
    public override string ToString() =>
        Error is null ? $"{State} ({(IsFullscreen ? "Toàn màn hình" : "Không toàn màn hình")})" : $"{State} — {Error}";
}

/// <summary>
/// Kết quả thăm dò đầy đủ một lần của cả hai luồng.
/// </summary>
/// <remarks>
/// Kết luận của hai luồng **có thể không nhất quán**, và bản thân sự không nhất quán đó mang lại thông tin:
/// ví dụ cửa sổ phóng to không viền, trò chơi toàn màn hình độc quyền, hoặc cửa sổ của chính sản phẩm bị loại trừ.
/// Vì vậy ở đây đặt kết quả của hai luồng song song nguyên trạng, thay vì gộp sớm thành một giá trị boolean.
/// </remarks>
public sealed record FullscreenSnapshot(
    ForegroundWindowInfo Foreground,
    WindowPathResult WindowPath,
    ShellPathResult ShellPath)
{
    /// <summary>
    /// Các cách kích hoạt (gọi ở cạnh màn hình Dock, góc nóng, nút bên chuột) lúc này có nên bị ức chế hay không.
    /// </summary>
    /// <remarks>
    /// Hai luồng lấy phép "HOẶC": chỉ cần một luồng cho rằng có ứng dụng toàn màn hình là ức chế. Đây là sự đánh đổi bất đối xứng có chủ đích —
    /// ức chế nhầm chỉ khiến Dock xuất hiện muộn một chút khi một cửa sổ nào đó lấp đầy màn hình, người dùng chỉ cần di chuyển chuột thêm một cái;
    /// bỏ lọt thì sẽ bật bảng điều khiển và cướp tiêu điểm khi đang xem video hoặc chơi game, cái giá đắt hơn nhiều.
    /// </remarks>
    public bool ShouldSuppressTriggers =>
        WindowPath.Verdict == FullscreenVerdict.Fullscreen || ShellPath.IsFullscreen;

    /// <summary>
    /// Kết luận của hai luồng có nhất quán hay không.
    /// </summary>
    /// <remarks>
    /// Khi luồng cửa sổ bị loại trừ (màn hình nền, thanh tác vụ, cửa sổ của chính sản phẩm này) hoặc không thể xác định thì nó không có kết luận,
    /// lúc này không tính là "không nhất quán" — nhưng báo cáo vẫn sẽ ghi rõ lý do bị loại trừ.
    /// </remarks>
    public bool PathsAgree => WindowPath.IsFullscreen is null || WindowPath.IsFullscreen == ShellPath.IsFullscreen;

    /// <summary>Tóm tắt một dòng, dùng cho báo cáo chẩn đoán.</summary>
    public string Describe() =>
        $"Luồng 1 {DescribeVerdict(WindowPath.Verdict)}, luồng 2 {ShellPath.State}, "
        + (PathsAgree ? "hai luồng nhất quán" : "hai luồng không nhất quán");

    private static string DescribeVerdict(FullscreenVerdict verdict) => verdict switch
    {
        FullscreenVerdict.Fullscreen => "Toàn màn hình",
        FullscreenVerdict.NotFullscreen => "Không toàn màn hình",
        FullscreenVerdict.Excluded => "Đã loại trừ (không có kết luận)",
        _ => "Không thể xác định",
    };
}

/// <summary>
/// Phán đoán xem tiền cảnh có đang chạy ứng dụng lấp đầy toàn màn hình hay không.
/// </summary>
/// <remarks>
/// <para>
/// Việc gọi ở cạnh màn hình của Dock, góc nóng và nút bên chuột đều phải bị ức chế khi có ứng dụng toàn màn hình ở tiền cảnh,
/// nếu không người dùng sẽ bị chính công cụ làm đẹp màn hình của mình làm gián đoạn khi xem video hoặc chơi game. Kiểu này là điểm chốt duy nhất cho điều kiện ức chế đó.
/// </para>
/// <para>
/// Việc kiểm tra đi theo hai luồng độc lập với nhau, kết quả của hai luồng sẽ được **báo cáo song song trung thực**, không giả vờ chúng tương đương:
/// </para>
/// <list type="number">
/// <item>
/// So sánh hình chữ nhật cửa sổ tiền cảnh với ranh giới màn hình chứa nó. Tất cả đều là truy vấn Win32 rẻ tiền (khoảng vài chục micro giây),
/// nên nó chính là câu trả lời cho câu hỏi tần suất cao "hiện tại có đang toàn màn hình không".
/// Chi tiết mấu chốt là dùng **viền hiển thị** của DWM chứ không dùng <c>GetWindowRect</c> để tham gia phán đoán,
/// xem giải thích ở <see cref="DwmApi.DwmwaExtendedFrameBounds"/>.
/// </item>
/// <item>
/// <c>SHQueryUserNotificationState</c>, tức bộ phán đoán mà Windows dùng khi quyết định "có bật thông báo hay không".
/// Nó gần với ngữ nghĩa của chính hệ thống hơn (trò chơi toàn màn hình độc quyền sẽ được nó nhận diện), đổi lại là một lần gọi shell liên tiến trình.
/// </item>
/// </list>
/// <para>
/// Ở đây **không có bất kỳ bộ định thời (timer) nào**: việc phán đoán hoặc do bên gọi truy vấn theo nhu cầu, hoặc do
/// <see cref="FullscreenWatcher"/> kích hoạt thông qua <c>SetWinEventHook</c> khi sự kiện đến.
/// </para>
/// </remarks>
public static class FullscreenDetector
{
    /// <summary>
    /// Sai số pixel cho phép khi phán đoán lấp đầy màn hình.
    /// </summary>
    /// <remarks>
    /// Cho phép 1 pixel vì hướng làm tròn giữa viền cửa sổ và ranh giới màn hình có thể khác nhau (đặc biệt khi tỷ lệ thu phóng không phải số nguyên),
    /// mà 1 pixel trên hình ảnh thì không thể nhận ra. Nới rộng thêm nữa sẽ bắt đầu tính nhầm các cửa sổ "gần như lấp đầy" thành toàn màn hình.
    /// </remarks>
    private const int CoverageTolerance = 1;

    /// <summary>
    /// Tên lớp cửa sổ của chính shell: màn hình nền, thanh tác vụ. Việc chúng lấp đầy màn hình là bình thường, không cấu thành ứng dụng toàn màn hình.
    /// </summary>
    private static readonly string[] ShellWindowClasses =
    [
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    ];

    /// <summary>
    /// Thăm dò đầy đủ một lần: chạy cả hai luồng, mỗi luồng kèm thời gian thực thi.
    /// </summary>
    /// <remarks>
    /// Dành cho chẩn đoán và các bên gọi "cần giải thích lý do". Với truy vấn tần suất cao trên đường dẫn kích hoạt, hãy dùng
    /// <see cref="IsFullscreenActive"/>, nó không chạm tới shell.
    /// </remarks>
    public static FullscreenSnapshot Probe(FullscreenOptions? options = null)
    {
        var effective = options ?? FullscreenOptions.Default;
        var handle = User32.GetForegroundWindow();

        var windowWatch = Stopwatch.StartNew();
        var (verdict, reason, geometry) = EvaluateWindowPath(handle, effective);
        windowWatch.Stop();

        var shellWatch = Stopwatch.StartNew();
        var (state, error) = QueryShellStateCore();
        shellWatch.Stop();

        var foreground = new ForegroundWindowInfo(
            handle,
            User32.ReadClassName(handle),
            User32.ReadWindowText(handle),
            GetProcessId(handle),
            ReadProcessName(handle),
            (User32.GetWindowLong(handle, User32.GwlStyle) & User32.WsMaximize) != 0);

        return new FullscreenSnapshot(
            foreground,
            new WindowPathResult(
                verdict,
                reason,
                geometry.WindowRect,
                geometry.VisibleFrame,
                geometry.MonitorBounds,
                geometry.MonitorDeviceName,
                windowWatch.Elapsed),
            new ShellPathResult(state, IsFullscreenState(state), error, shellWatch.Elapsed));
    }

    /// <summary>
    /// Truy vấn nhẹ: cửa sổ tiền cảnh lúc này có đang lấp đầy toàn màn hình hay không.
    /// </summary>
    /// <remarks>
    /// Mặc định chỉ dùng hình học cửa sổ (không gọi shell, không cấp phát chuỗi), phù hợp đặt trong mỗi lần kiểm tra gọi lên.
    /// Khi cần tính cả trạng thái shell thì bật <see cref="FullscreenOptions.IncludeShellState"/>,
    /// cái giá và lý do xem tại đó.
    /// </remarks>
    public static bool IsFullscreenActive(FullscreenOptions? options = null)
    {
        var effective = options ?? FullscreenOptions.Default;

        if (GetForegroundVerdict(effective) == FullscreenVerdict.Fullscreen)
        {
            return true;
        }

        // Chỉ khi hình học không coi là toàn màn hình mới đến lượt luồng thứ hai tùy chọn — thứ tự này là có chủ đích:
        // trường hợp phổ biến (không có ứng dụng toàn màn hình) chỉ tốn vài micro giây của hình học.
        return effective.IncludeShellState && IsFullscreenState(QueryShellStateCore().State);
    }

    /// <summary>Read one foreground handle consistently. Only the specific exclusive-D3D shell state augments geometry.</summary>
    public static PerformanceFullscreenObservation ReadPerformanceObservation()
    {
        var handle = User32.GetForegroundWindow();
        if (handle == 0) return new(null, false);
        if (GetProcessId(handle) == (uint)Environment.ProcessId) return new(null, true);
        if (IsExcluded(handle, FullscreenOptions.Default, out _)) return new(false, false);
        if (!TryGetGeometry(handle, out var geometry, out _)) return new(null, false);
        var coversMonitor = Covers(geometry.VisibleFrame ?? geometry.WindowRect, geometry.MonitorBounds);
        return new(coversMonitor || QueryShellStateCore().State == UserNotificationState.RunningD3dFullScreen, false);
    }

    /// <summary>
    /// Dạng đầy đủ của truy vấn nhẹ: trả về kết luận phán đoán của cửa sổ tiền cảnh (bao gồm mức "Đã loại trừ").
    /// </summary>
    public static FullscreenVerdict GetForegroundVerdict(FullscreenOptions? options = null)
    {
        var effective = options ?? FullscreenOptions.Default;
        var handle = User32.GetForegroundWindow();

        if (handle == 0)
        {
            return FullscreenVerdict.Unknown;
        }

        if (IsExcluded(handle, effective, out _))
        {
            return FullscreenVerdict.Excluded;
        }

        if (!TryGetGeometry(handle, out var geometry, out _))
        {
            return FullscreenVerdict.Unknown;
        }

        // Khi không lấy được viền hiển thị thì lùi về GetWindowRect: thà hơi gắt một chút,
        // còn hơn vì một truy vấn thất bại mà cho phép kích hoạt.
        var frame = geometry.VisibleFrame ?? geometry.WindowRect;

        return Covers(frame, geometry.MonitorBounds)
            ? FullscreenVerdict.Fullscreen
            : FullscreenVerdict.NotFullscreen;
    }

    /// <summary>Hỏi shell trạng thái thông báo người dùng hiện tại.</summary>
    public static UserNotificationState QueryShellState() => QueryShellStateCore().State;

    /// <summary>
    /// Một trạng thái shell có đồng nghĩa với "có ứng dụng toàn màn hình" hay không.
    /// </summary>
    /// <remarks>
    /// Không gộp <c>PresentationMode</c> vào: nó biểu thị đang bật cài đặt trình chiếu, không đại diện cho việc có cửa sổ lấp đầy màn hình;
    /// <c>NotPresent</c> / <c>QuietTime</c> là "đừng làm phiền", cũng không phải toàn màn hình.
    /// Ba trạng thái này thuộc về "không làm phiền" chứ không phải "toàn màn hình", nếu bên gọi cần điều kiện ức chế rộng hơn,
    /// có thể tự phán đoán giá trị gốc của <see cref="UserNotificationState"/>.
    /// </remarks>
    public static bool IsFullscreenState(UserNotificationState state) =>
        state is UserNotificationState.Busy
            or UserNotificationState.RunningD3dFullScreen
            or UserNotificationState.App;

    /// <summary>
    /// Thanh tác vụ lúc này có đang ở chế độ tự động ẩn hay không.
    /// </summary>
    /// <remarks>
    /// Thông tin này dùng để giải thích một tình huống **không thể phân biệt**: khi thanh tác vụ tự động ẩn, viền hiển thị của cửa sổ phóng to
    /// cũng phủ kín toàn bộ màn hình, hoàn toàn trùng khớp về mặt hình học với toàn màn hình thực sự; mọi phán đoán dựa trên hình chữ nhật đều không thể tách chúng ra.
    /// Trả về false nếu truy vấn thất bại.
    /// </remarks>
    public static bool IsTaskbarAutoHidden()
    {
        try
        {
            var data = new Shell32.AppBarData
            {
                Size = (uint)Marshal.SizeOf<Shell32.AppBarData>(),
            };

            return (Shell32.SHAppBarMessage(Shell32.AbmGetState, ref data) & Shell32.AbsAutoHide) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Dạng đầy đủ của luồng 1: phán đoán + giải thích + dữ liệu hình học.</summary>
    private static (FullscreenVerdict Verdict, string Reason, WindowGeometry Geometry) EvaluateWindowPath(nint handle, FullscreenOptions options)
    {
        if (handle == 0)
        {
            return (FullscreenVerdict.Unknown, "Hiện không có cửa sổ tiền cảnh (phiên đang chuyển đổi hoặc đã khóa màn hình).", default);
        }

        if (IsExcluded(handle, options, out var exclusion))
        {
            return (FullscreenVerdict.Excluded, exclusion, default);
        }

        if (!TryGetGeometry(handle, out var geometry, out var failure))
        {
            return (FullscreenVerdict.Unknown, failure, default);
        }

        var visible = geometry.VisibleFrame ?? geometry.WindowRect;

        if (Covers(visible, geometry.MonitorBounds))
        {
            var note = geometry.IsMaximized
                ? " (cửa sổ này đang ở trạng thái phóng to; khi thanh tác vụ tự động ẩn, cửa sổ phóng to và toàn màn hình không thể phân biệt về mặt hình học)"
                : string.Empty;
            return (
                FullscreenVerdict.Fullscreen,
                $"Viền hiển thị {visible} đã phủ kín toàn bộ {geometry.MonitorBounds} của {geometry.MonitorDeviceName}{note}",
                geometry);
        }

        if (Covers(geometry.WindowRect, geometry.MonitorBounds))
        {
            // Đây chính là cái bẫy khi chỉ nhìn GetWindowRect, đáng để giải thích riêng một câu:
            // GetWindowRect của cửa sổ phóng to bao gồm viền điều chỉnh kích thước vô hình bị đẩy ra ngoài màn hình.
            return (
                FullscreenVerdict.NotFullscreen,
                $"GetWindowRect {geometry.WindowRect} về mặt số liệu phủ kín toàn màn hình, nhưng viền hiển thị {visible} dừng bên trong "
                    + $"vùng làm việc của {geometry.MonitorDeviceName} — đây là viền điều chỉnh kích thước vô hình của cửa sổ phóng to, không phải toàn màn hình",
                geometry);
        }

        return (
            FullscreenVerdict.NotFullscreen,
            $"Viền hiển thị {visible} chưa phủ kín toàn bộ {geometry.MonitorBounds} của {geometry.MonitorDeviceName}",
            geometry);
    }

    /// <summary>
    /// Phán đoán xem cửa sổ tiền cảnh có thuộc nhóm "không nên bị coi là ứng dụng toàn màn hình" hay không.
    /// </summary>
    /// <remarks>
    /// Thứ tự có tính toán: so sánh handle và tên lớp rẻ nhất trước, cuối cùng mới truy vấn DWM.
    /// Đường dẫn này sẽ được gọi với tần suất cao, mỗi lời gọi hệ thống dư thừa đều phải có lý do chính đáng.
    /// </remarks>
    private static bool IsExcluded(nint handle, FullscreenOptions options, out string reason)
    {
        if (handle == User32.GetShellWindow() || handle == User32.GetDesktopWindow())
        {
            reason = "Cửa sổ tiền cảnh là màn hình nền (Progman/WorkerW); việc màn hình nền lấp đầy màn hình là bình thường, không tính là ứng dụng toàn màn hình.";
            return true;
        }

        if (IsShellWindowClass(handle))
        {
            reason = $"Cửa sổ tiền cảnh là thanh tác vụ của shell (tên lớp {User32.ReadClassName(handle)}), không tính là ứng dụng toàn màn hình.";
            return true;
        }

        if (options.ExcludeCurrentProcess)
        {
            var processId = GetProcessId(handle);
            if (processId != 0 && processId == (uint)Environment.ProcessId)
            {
                reason = "Cửa sổ tiền cảnh thuộc về chính sản phẩm này, không tính là ứng dụng toàn màn hình (nếu không Dock sẽ coi cửa sổ của chính mình là toàn màn hình và không gọi lên được nữa).";
                return true;
            }
        }

        if (User32.IsIconic(handle))
        {
            reason = "Cửa sổ tiền cảnh đã bị thu nhỏ, không thể lấp đầy màn hình.";
            return true;
        }

        if (!User32.IsWindowVisible(handle))
        {
            reason = "Cửa sổ tiền cảnh không hiển thị.";
            return true;
        }

        if (DwmApi.IsCloaked(handle))
        {
            reason = "Cửa sổ tiền cảnh đã bị DWM ẩn (cloaked), thường là vỏ trống do ứng dụng UWP bị tạm ngưng để lại.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>Tên lớp cửa sổ có thuộc màn hình nền/thanh tác vụ của shell hay không. Không cấp phát chuỗi.</summary>
    private static bool IsShellWindowClass(nint handle)
    {
        Span<char> buffer = stackalloc char[64];
        var length = User32.GetClassName(handle, buffer);

        if (length <= 0)
        {
            return false;
        }

        var name = buffer[..length];

        foreach (var shellClass in ShellWindowClasses)
        {
            if (name.Equals(shellClass, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lấy hình chữ nhật cửa sổ, viền hiển thị, ranh giới màn hình chứa nó và tên thiết bị.</summary>
    private static bool TryGetGeometry(nint handle, out WindowGeometry geometry, out string failure)
    {
        geometry = default;
        failure = string.Empty;

        if (!Display32.GetWindowRect(handle, out var windowRect))
        {
            failure = "GetWindowRect thất bại, không lấy được hình chữ nhật của cửa sổ tiền cảnh.";
            return false;
        }

        var monitor = Display32.MonitorFromWindow(handle, Display32.MonitorDefaultToNearest);
        if (monitor == 0)
        {
            failure = "MonitorFromWindow thất bại, không thể xác định cửa sổ tiền cảnh nằm trên màn hình nào.";
            return false;
        }

        var info = new MonitorInfoEx
        {
            Size = Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty,
        };

        if (!Display32.GetMonitorInfo(monitor, ref info))
        {
            failure = "GetMonitorInfo thất bại, không lấy được ranh giới màn hình.";
            return false;
        }

        PixelRect? visibleFrame = DwmApi.TryGetExtendedFrameBounds(handle, out var extended)
            ? ToPixelRect(extended)
            : null;

        geometry = new WindowGeometry(
            ToPixelRect(windowRect),
            visibleFrame,
            ToPixelRect(info.Monitor),
            info.DeviceName,
            (User32.GetWindowLong(handle, User32.GwlStyle) & User32.WsMaximize) != 0);

        return true;
    }

    /// <summary>Hình chữ nhật cửa sổ có phủ kín toàn bộ màn hình hay không.</summary>
    private static bool Covers(PixelRect window, PixelRect monitor) =>
        window.X <= monitor.X + CoverageTolerance
        && window.Y <= monitor.Y + CoverageTolerance
        && window.Right >= monitor.Right - CoverageTolerance
        && window.Bottom >= monitor.Bottom - CoverageTolerance;

    private static (UserNotificationState State, string? Error) QueryShellStateCore()
    {
        try
        {
            var result = Shell32.SHQueryUserNotificationState(out var raw);

            if (result < 0)
            {
                return (UserNotificationState.Unknown, $"SHQueryUserNotificationState trả về lỗi HRESULT 0x{result:X8}");
            }

            var state = (UserNotificationState)raw;

            return IsKnownState(state)
                ? (state, null)
                : (UserNotificationState.Unknown, $"SHQueryUserNotificationState trả về trạng thái không xác định {raw}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (UserNotificationState.Unknown, "shell32.dll trên máy này không có SHQueryUserNotificationState.");
        }
    }

    private static bool IsKnownState(UserNotificationState state) => state switch
    {
        UserNotificationState.NotPresent or
        UserNotificationState.Busy or
        UserNotificationState.RunningD3dFullScreen or
        UserNotificationState.PresentationMode or
        UserNotificationState.AcceptsNotifications or
        UserNotificationState.QuietTime or
        UserNotificationState.App => true,
        _ => false,
    };

    private static uint GetProcessId(nint handle) =>
        User32.GetWindowThreadProcessId(handle, out var processId) == 0 ? 0 : processId;

    /// <summary>Lấy tên tiến trình của cửa sổ tiền cảnh, chỉ dùng cho báo cáo chẩn đoán. Trả về "?" nếu không lấy được.</summary>
    private static string ReadProcessName(nint handle)
    {
        var processId = GetProcessId(handle);

        if (processId == 0)
        {
            return "?";
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Tiến trình có thể vừa thoát hoặc không đủ quyền. Báo cáo chẩn đoán không đáng bị gián đoạn vì việc này.
            return "?";
        }
    }

    private static PixelRect ToPixelRect(Rect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);

    /// <summary>Dữ liệu hình học dùng trong luồng 1. Đơn vị pixel vật lý.</summary>
    private readonly record struct WindowGeometry(
        PixelRect WindowRect,
        PixelRect? VisibleFrame,
        PixelRect MonitorBounds,
        string MonitorDeviceName,
        bool IsMaximized);
}
