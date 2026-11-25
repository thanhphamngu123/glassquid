using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glassquid.Platform;
using Glassquid.Platform.Displays;
using Glassquid.Platform.Shell;

namespace Glassquid.App.Startup;

/// <summary>
/// Đo kiểm thực tế hai luồng phát hiện toàn màn hình và báo cáo trung thực sự khác biệt giữa chúng.
/// </summary>
/// <remarks>
/// Đây là công cụ nghiệm thu của Giai đoạn 2 "Phát hiện toàn màn hình". Bản thân logic phát hiện không khó viết,
/// cái khó là biết nó sẽ đưa ra kết luận gì ở những dạng cửa sổ nào - vì vậy ngoài việc báo cáo trạng thái hiện tại,
/// ở đây còn **tạo thật một cửa sổ toàn màn hình** để đo biểu hiện của hai luồng trên cửa sổ thực tế.
///
/// Lý do bắt buộc phải đo thực tế chứ không thể suy luận suông: Luồng 1 (hình chữ nhật cửa sổ) và Luồng 2 (trạng thái thông báo shell)
/// dùng hai bộ phán đoán độc lập, chúng sẽ không nhất quán ở các dạng như "phóng đại không viền" hay "game toàn màn hình độc quyền",
/// và luồng nào đáng tin cậy hơn trong tình huống nào thì chỉ có đo thực tế mới biết được.
/// </remarks>
internal static class FullscreenProbe
{
    /// <summary>Số lần lấy mẫu để thống kê thời gian thực thi.</summary>
    private const int TimingIterations = 50;

    /// <summary>Số lần lấy mẫu cho truy vấn nhẹ (rẻ hơn dò tìm đầy đủ nên lấy mẫu nhiều hơn).</summary>
    private const int CheapTimingIterations = 200;

    /// <summary>Thời lượng giữ cửa sổ toàn màn hình hiển thị, đủ để hook sự kiện chạy xong một vòng gộp cửa sổ.</summary>
    private const int FullscreenHoldMilliseconds = 500;

    public static int Run(StringBuilder report, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(displays);

        var culture = CultureInfo.InvariantCulture;
        var failures = 0;

        report.AppendLine("[Phát hiện toàn màn hình]");
        report.AppendLine("  Luồng 1: So sánh hình chữ nhật cửa sổ tiền cảnh với biên màn hình chứa nó (GetWindowRect, hiệu chỉnh bằng khung hiển thị DWM).");
        report.AppendLine("  Luồng 2: SHQueryUserNotificationState, tức bộ API Windows dùng để quyết định 'có bật thông báo hay không'.");
        report.AppendLine(culture, $"  Tự động ẩn thanh tác vụ: {(FullscreenDetector.IsTaskbarAutoHidden() ? "Có - lúc này cửa sổ phóng đại và toàn màn hình không thể phân biệt về mặt hình học" : "Không")}");
        report.AppendLine();

        // ---- Cửa sổ tiền cảnh thực tế hiện tại ----
        var current = FullscreenDetector.Probe();
        AppendSnapshot(report, "Cửa sổ tiền cảnh thực tế hiện tại", current);
        report.AppendLine();

        // ---- Thời gian thực thi ----
        report.AppendLine("  · Thời gian truy vấn");

        var windowSamples = Sample(TimingIterations, static () => FullscreenDetector.Probe().WindowPath.Duration);
        var shellSamples = Sample(TimingIterations, static () => FullscreenDetector.Probe().ShellPath.Duration);
        var cheapWatch = Stopwatch.StartNew();
        for (var i = 0; i < CheapTimingIterations; i++)
        {
            _ = FullscreenDetector.IsFullscreenActive();
        }

        cheapWatch.Stop();

        report.AppendLine(culture, $"      Luồng 1 (dò đầy đủ, gồm lấy tên lớp/tiêu đề/tên tiến trình): {Describe(windowSamples)}");
        report.AppendLine(culture, $"      Luồng 2 (gọi shell): {Describe(shellSamples)}");
        report.AppendLine(culture, $"      Truy vấn nhẹ IsFullscreenActive (chỉ đi Luồng 1, không truy vấn mô tả): "
            + $"trung bình {cheapWatch.Elapsed.TotalMicroseconds / CheapTimingIterations:F2} micro-giây ({CheapTimingIterations} lần)");

        // Kết luận này quyết định luồng kích hoạt nên dùng API nào. Tiêu chí không chỉ là bội số mà còn là độ ổn định:
        // Đo thực tế cho thấy thời gian gọi shell biến động rất mạnh theo trạng thái (trên cùng một máy từng thấy từ 1.9 micro-giây đến 2.5 mili-giây),
        // trong khi luồng hình học cửa sổ ổn định trong phạm vi dưới 10 micro-giây.
        var cheapPerCall = cheapWatch.Elapsed.TotalMicroseconds / CheapTimingIterations;
        var shellMedian = shellSamples[shellSamples.Length / 2];

        report.AppendLine(culture, $"      → Trung vị gọi shell gấp {shellMedian / cheapPerCall:F1} lần truy vấn nhẹ (tối đa {shellSamples[^1]:F0} micro-giây và biến động theo trạng thái); vì vậy luồng kích hoạt chỉ dùng Luồng 1, trạng thái shell dành cho chẩn đoán và xác nhận tần suất thấp.");
        report.AppendLine("        Một lý do khác nằm ở phần đo thực tế tiếp theo: luồng shell không nhìn thấy cửa sổ toàn màn hình trên màn hình phụ.");

        report.AppendLine();

        // ---- Hook sự kiện và cửa sổ toàn màn hình thật ----
        failures += RunLiveFullscreenTest(report, displays);

        report.AppendLine();
        AppendKnownBoundaries(report);

        report.AppendLine();
        return failures;
    }

    /// <summary>
    /// Các biên đã biết: dạng nào đã xác minh, dạng nào không đáng tin cậy, dạng nào chưa xác minh.
    /// </summary>
    /// <remarks>
    /// Các kết luận này đến từ kết quả đo thực tế của <c>tmp/fullscreen-probe</c> (trên máy này, phiên bản này),
    /// được ghi vào phần chẩn đoán để thông tin "phát hiện được đến mức nào" đi cùng sản phẩm build,
    /// thay vì chỉ nằm lại trong nhật ký thử nghiệm dùng một lần.
    /// </remarks>
    private static void AppendKnownBoundaries(StringBuilder report)
    {
        report.AppendLine("  · Các biên đã biết (đo thực tế trên máy này, dữ liệu đầy đủ xem đầu ra của tmp/fullscreen-probe)");
        report.AppendLine("      Điều kiện tiên quyết: Tiến trình phải khai báo PerMonitorV2. Khi thiếu manifest, GetWindowRect trả về tọa độ ảo còn khung hiển thị DWM");
        report.AppendLine("            trả về pixel vật lý; trộn lẫn hai thứ sẽ khiến toàn màn hình trên màn hình phụ bị nhận nhầm thành không toàn màn hình (đã đo thực tế).");
        report.AppendLine("      ✓ Không viền phủ kín màn hình chính: Cả hai luồng đều phán đoán là toàn màn hình");
        report.AppendLine("      ✓ Không viền + phóng đại: Cả hai luồng đều phán đoán là toàn màn hình");
        report.AppendLine("      ✓ Cửa sổ thường phóng đại: Khung hiển thị dừng ở vùng làm việc (1368 < 1440), cả hai luồng đều phán đoán không toàn màn hình");
        report.AppendLine("      ✓ Cửa sổ có viền bị kéo lớn bằng toàn màn hình: GetWindowRect vừa khít toàn màn hình, khung hiển thị (9,0 2542×1431) nhỏ hơn một vòng,");
        report.AppendLine("        phán đoán là không toàn màn hình. Phần hiệu chỉnh bằng khung hiển thị DWM được thêm vào chính cho trường hợp này - chỉ nhìn GetWindowRect sẽ nhận nhầm thành toàn màn hình.");
        report.AppendLine("      ✓ Chuyển đổi toàn màn hình trong cùng cửa sổ (kiểu F11, cửa sổ tiền cảnh không đổi): Nhờ sự kiện thay đổi vị trí, cả hai lần đảo trạng thái đều nhận được");
        report.AppendLine("      ✓ Thu nhỏ cửa sổ toàn màn hình: Phán đoán được giải phóng ngay lập tức");
        report.AppendLine("      ✗ Cửa sổ toàn màn hình trên màn hình phụ: Luồng 1 phán đoán toàn màn hình, Luồng 2 bỏ sót (báo không toàn màn hình), hai luồng không nhất quán");
        report.AppendLine("      ✗ Nền có cửa sổ toàn màn hình, tiền cảnh là cửa sổ nhỏ khác: Không ức chế. Phát hiện chỉ xem cửa sổ tiền cảnh, đây là sự đánh đổi có chủ đích");
        report.AppendLine("      ⚠ Khi thanh tác vụ tự động ẩn: Cửa sổ phóng đại và toàn màn hình không thể phân biệt về mặt hình học, mọi phán đoán dựa trên hình chữ nhật đều không tách được");
        report.AppendLine("      ⚠ Game toàn màn hình độc quyền (D3D): Không thể tạo trên máy này nên chưa đo thực tế; Luồng 2 có trạng thái chuyên biệt để nhận diện");
        report.AppendLine("      ⚠ Bản thân Luồng 2 không ổn định: Thời gian đo thực tế biến động theo trạng thái từ 1.9 micro-giây đến 2.5 mili-giây, và từng gặp trường hợp 'cửa sổ không toàn màn hình ở tiền cảnh nhưng lại báo Busy'");
    }

    /// <summary>
    /// Tạo một cửa sổ toàn màn hình thật để kiểm chứng biểu hiện của hai luồng và hook sự kiện trên cửa sổ thực tế.
    /// </summary>
    private static int RunLiveFullscreenTest(StringBuilder report, IReadOnlyList<DisplayInfo> displays)
    {
        var culture = CultureInfo.InvariantCulture;
        var failures = 0;

        var primary = displays.FirstOrDefault(static d => d.IsPrimary)
            ?? (displays.Count > 0 ? displays[0] : null);

        if (primary is null)
        {
            report.AppendLine("  · Đo thực tế cửa sổ toàn màn hình: Bỏ qua - không có màn hình nào khả dụng để phủ kín.");
            return 1;
        }

        report.AppendLine("  · Đo thực tế cửa sổ toàn màn hình (không viền, luôn trên cùng, phủ kín màn hình chính; sẽ hiển thị toàn màn hình trong chốc lát như dự kiến)");

        // Trình giám sát đã tắt loại trừ bản thân: dùng để kiểm chứng chuỗi sự kiện (hook → gộp → tính lại → callback) thực sự thông suốt.
        var counting = new ChangeCounter();
        using var countingWatcher = FullscreenWatcher.TryStart(
            counting.Record,
            new FullscreenOptions { ExcludeCurrentProcess = false });

        // Trình giám sát theo thiết lập mặc định của sản phẩm: nó phải **hoàn toàn phớt lờ** thay đổi này,
        // đây chính là đảm bảo cho việc "Dock không tự coi cửa sổ của mình là ứng dụng toàn màn hình".
        var selfIgnoring = new ChangeCounter();
        using var selfIgnoringWatcher = FullscreenWatcher.TryStart(selfIgnoring.Record);

        report.AppendLine(culture, $"      Hook sự kiện: {(countingWatcher is null ? "✗ Cài đặt thất bại (SetWinEventHook trả về 0)" : "✓ Đã cài đặt (EVENT_SYSTEM_FOREGROUND + EVENT_OBJECT_LOCATIONCHANGE)")}");

        if (countingWatcher is null)
        {
            // Không cài được hook không đồng nghĩa với không thể phát hiện: bên gọi sẽ lùi về truy vấn theo yêu cầu.
            report.AppendLine("        Khi hook không khả dụng thì truy vấn theo yêu cầu vẫn hoạt động, do đó không tính là lỗi.");
        }

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x18)),
            Content = new TextBlock
            {
                Text = "Glassquid đang đo thực tế phát hiện toàn màn hình (tự động thoát sau khoảng 1 giây)",
                Foreground = Brushes.White,
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        try
        {
            window.Show();

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == 0)
            {
                report.AppendLine("      ✗ Cửa sổ dò tìm không lấy được handle, không thể đo thực tế.");
                return 1;
            }

            // Phủ kín **toàn bộ** biên của màn hình chính (bao gồm dải do thanh tác vụ chiếm), đây mới là cửa sổ toàn màn hình thật sự;
            // cửa sổ chỉ phủ kín vùng làm việc là "phóng đại", không phải toàn màn hình.
            // Đặt vị trí bằng pixel vật lý: Left/Top của WPF sẽ bị lệch dưới DPI hỗn hợp, xem phần đo thực tế định vị đa màn hình.
            DisplayService.MoveWindowToPixelRect(handle, primary.Bounds);
            PumpFor(60);
            DisplayService.MoveWindowToPixelRect(handle, primary.Bounds);

            window.Activate();
            _ = WindowActivation.TryBringToForeground(handle);
            PumpFor(FullscreenHoldMilliseconds);

            var actual = DisplayService.GetWindowPixelBounds(handle);
            var selfProbe = FullscreenDetector.Probe();
            var testProbe = FullscreenDetector.Probe(new FullscreenOptions { ExcludeCurrentProcess = false });
            var isForeground = selfProbe.Foreground.Handle == handle;

            report.AppendLine(culture, $"      Cửa sổ dò tìm: handle 0x{handle:X}, kết quả đặt vị trí {actual} (màn hình chính mục tiêu {primary.Bounds})");

            // Ca dương tính có hợp lệ hay không. Nếu không hợp lệ thì các kiểm tra bên dưới đều không thể bàn tới - chúng cần tiền đề
            // "tiến trình này có một cửa sổ phủ kín thực sự đang ở tiền cảnh".
            //
            // Điều này bắt buộc phải tách bạch nghiêm ngặt với "phát hiện bị hỏng": khi khởi chạy từ script hoặc chạy nền, Windows sẽ từ chối
            // chuyển lên tiền cảnh, đó là **lỗi dựng ca kiểm thử**, không phải khiếm khuyết sản phẩm. Trước đây chỗ này vẫn tính là lỗi,
            // khiến chẩn đoán báo "3 mục thất bại" trong khi thực chất chưa kiểm chứng được gì - điều đó còn tệ hơn không báo,
            // vì sẽ dẫn người ta đi điều tra một vấn đề vốn không hề tồn tại.
            var positiveCaseValid = isForeground;

            if (!positiveCaseValid)
            {
                report.AppendLine(culture, $"      ⚠ Không thể đưa cửa sổ dò tìm lên tiền cảnh (tiến trình tiền cảnh hiện tại là '{selfProbe.Foreground.ProcessName}').");
                report.AppendLine("        **Ca dương tính của lần kiểm tra toàn màn hình này không có hiệu lực; ba mục bên dưới được đánh dấu 'Chưa xác minh', không tính vào số lỗi.**");
                report.AppendLine("        Chúng chỉ cho thấy mã không bị sập, không thể dùng làm bằng chứng cho tính đúng đắn của bộ phát hiện.");
                report.AppendLine("        Khi khởi chạy từ script hoặc chạy nền, Windows sẽ từ chối chuyển tiền cảnh, đây là tình huống lường trước được;");
                report.AppendLine("        để kiểm thử lại thủ công, vui lòng chạy trực tiếp --diagnose trong phiên tương tác.");
            }

            var unverified = 0;

            report.AppendLine(culture, $"      Khi loại trừ bản thân: Luồng 1 = {DescribeVerdict(selfProbe.WindowPath.Verdict)} (kỳ vọng 'Đã loại trừ'), Luồng 2 = {selfProbe.ShellPath}");

            if (!positiveCaseValid)
            {
                unverified++;
                report.AppendLine("      ○ Chưa xác minh: Loại trừ cửa sổ của chính mình (cần cửa sổ dò tìm thực sự ở tiền cảnh)");
            }
            else if (selfProbe.WindowPath.Verdict != FullscreenVerdict.Excluded)
            {
                failures++;
                report.AppendLine("      ✗ Cửa sổ toàn màn hình của chính sản phẩm này không được loại trừ. Điều này sẽ khiến Dock tự nhận mình là ứng dụng toàn màn hình và từ đó không phản hồi khi gọi mở nữa.");
            }
            else
            {
                report.AppendLine("      ✓ Cửa sổ của chính mình được loại trừ đúng: Dock sẽ không tự ức chế vì cửa sổ của chính nó.");
            }

            report.AppendLine(culture, $"      Khi không loại trừ bản thân: Luồng 1 = {DescribeVerdict(testProbe.WindowPath.Verdict)} (kỳ vọng 'Toàn màn hình'), Luồng 2 = {testProbe.ShellPath}");

            if (!positiveCaseValid)
            {
                unverified++;
                report.AppendLine("      ○ Chưa xác minh: Luồng 1 phán đoán cửa sổ phủ kín là toàn màn hình");
            }
            else if (testProbe.WindowPath.Verdict != FullscreenVerdict.Fullscreen)
            {
                failures++;
                report.AppendLine("      ✗ Luồng 1 không phán đoán cửa sổ này là toàn màn hình, chứng tỏ logic kiểm tra phủ kín có vấn đề.");
            }
            else if (!testProbe.ShellPath.IsFullscreen)
            {
                report.AppendLine("      ⚠ Luồng 2 không báo toàn màn hình (nó không tính 'cửa sổ không viền phủ kín toàn màn hình' là toàn màn hình). Đây là khác biệt đã biết giữa hai luồng,");
                report.AppendLine("        sản phẩm lấy phép 'HOẶC' cho kết quả này nên sẽ không bỏ sót dạng cửa sổ này. Cửa sổ toàn màn hình trên màn hình phụ Luồng 2 cũng không nhìn thấy -");
                report.AppendLine("        xem kiểm thử liên tiến trình ở tmp/fullscreen-probe: Luồng 1 báo toàn màn hình, Luồng 2 báo không toàn màn hình.");
            }
            else
            {
                report.AppendLine("      ✓ Cả hai luồng đều phán đoán là toàn màn hình.");
            }

            // ---- Hook sự kiện ----
            var recorded = counting.Snapshot();
            var selfRecorded = selfIgnoring.Snapshot();

            if (countingWatcher is not null)
            {
                var trail = recorded.Length == 0
                    ? string.Empty
                    : $" ({string.Join(" → ", recorded.Select(static v => v ? "Toàn màn hình" : "Không toàn màn hình"))})";

                report.AppendLine(culture, $"      Hook sự kiện nhận được thay đổi phán đoán: {recorded.Length} lần{trail}");

                if (recorded.Length == 0 && !positiveCaseValid)
                {
                    unverified++;
                    report.AppendLine("      ○ Chưa xác minh: Hook sự kiện nhận được thay đổi phán đoán (cửa sổ không lên được tiền cảnh, vốn dĩ không nên có thay đổi)");
                }
                else if (recorded.Length == 0)
                {
                    failures++;
                    report.AppendLine("      ✗ Đã cài hook nhưng không nhận được thay đổi nào, chuỗi sự kiện có vấn đề (phán đoán trễ tối đa cửa sổ gộp sự kiện 120 mili-giây).");
                }
                else if (recorded[0])
                {
                    report.AppendLine("      ✓ Chuỗi hướng sự kiện hoạt động tốt: không thăm dò định kỳ, hoàn toàn dựa vào SetWinEventHook kích hoạt tính lại.");
                }
            }

            report.AppendLine(culture, $"      Trình giám sát loại trừ bản thân nhận được thay đổi: {selfRecorded.Length} lần (kỳ vọng 0 - cửa sổ của chính mình không được làm đổi phán đoán)");

            if (selfRecorded.Length != 0)
            {
                // Mục này không liên quan đến tiền cảnh: dù cửa sổ có lên được tiền cảnh hay không, cửa sổ của chính mình đều không được làm đổi phán đoán.
                failures++;
                report.AppendLine("      ✗ Cửa sổ của chính mình đã kích hoạt thay đổi phán đoán, mâu thuẫn với quy ước 'loại trừ bản thân'.");
            }

            if (unverified > 0)
            {
                report.AppendLine(culture, $"      -- Lần này có {unverified} mục chưa xác minh (xem giải thích 'không có hiệu lực' ở trên), số lỗi không tính các mục này.");
            }
        }
        finally
        {
            window.Close();
        }

        // Sau khi đóng cửa sổ, xác nhận khôi phục thêm một lần nữa: trạng thái ức chế phải tự giải phóng được, nếu không một lần nhận nhầm sẽ có hiệu lực vĩnh viễn.
        PumpFor(FullscreenHoldMilliseconds);
        var afterClose = FullscreenDetector.Probe();

        report.AppendLine(culture, $"      Sau khi đóng: {afterClose.Describe()}; Kết luận: {(afterClose.ShouldSuppressTriggers ? "Vẫn ức chế kích hoạt" : "Không còn ức chế")}");

        if (afterClose.WindowPath.Verdict == FullscreenVerdict.Fullscreen)
        {
            failures++;
            report.AppendLine("      ✗ Cửa sổ toàn màn hình đã đóng, nhưng Luồng 1 vẫn phán đoán là toàn màn hình.");
        }

        return failures;
    }

    private static void AppendSnapshot(StringBuilder report, string title, FullscreenSnapshot snapshot)
    {
        var culture = CultureInfo.InvariantCulture;

        report.AppendLine(culture, $"  · {title}");
        report.AppendLine(culture, $"      Cửa sổ tiền cảnh: {snapshot.Foreground.ProcessName} (tiến trình {snapshot.Foreground.ProcessId})"
            + $"  Tên lớp {snapshot.Foreground.ClassName}{(string.IsNullOrEmpty(snapshot.Foreground.Title) ? string.Empty : $"  Tiêu đề '{snapshot.Foreground.Title}'")}");
        report.AppendLine(culture, $"      Luồng 1: {DescribeVerdict(snapshot.WindowPath.Verdict)} - {snapshot.WindowPath.Reason}");
        report.AppendLine(culture, $"        GetWindowRect = {Describe(snapshot.WindowPath.WindowRect)}");
        report.AppendLine(culture, $"        Khung hiển thị DWM = {Describe(snapshot.WindowPath.VisibleFrame)}");
        report.AppendLine(culture, $"        Màn hình chứa = {snapshot.WindowPath.MonitorDeviceName} {Describe(snapshot.WindowPath.MonitorBounds)}");
        report.AppendLine(culture, $"      Luồng 2: {snapshot.ShellPath.State} ({(snapshot.ShellPath.IsFullscreen ? "Toàn màn hình" : "Không toàn màn hình")})"
            + $"{(snapshot.ShellPath.Error is null ? string.Empty : $" - {snapshot.ShellPath.Error}")}");
        report.AppendLine(culture, $"      Hai luồng nhất quán: {(snapshot.PathsAgree ? "Có" : "Không")}; Ức chế kích hoạt: {(snapshot.ShouldSuppressTriggers ? "Có" : "Không")}");
    }

    /// <summary>Thu thập một số mẫu thời gian thực thi, trả về các giá trị micro-giây được sắp xếp tăng dần.</summary>
    private static double[] Sample(int iterations, Func<TimeSpan> measure)
    {
        var samples = new double[iterations];

        for (var i = 0; i < iterations; i++)
        {
            samples[i] = measure().TotalMicroseconds;
        }

        Array.Sort(samples);
        return samples;
    }

    private static string Describe(double[] sorted) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"tối thiểu {sorted[0]:F2} micro-giây, trung vị {sorted[sorted.Length / 2]:F2} micro-giây, tối đa {sorted[^1]:F2} micro-giây");

    private static string Describe(PixelRect? rect) => rect?.ToString() ?? "(không khả dụng)";

    private static string DescribeVerdict(FullscreenVerdict verdict) => verdict switch
    {
        FullscreenVerdict.Fullscreen => "Toàn màn hình",
        FullscreenVerdict.NotFullscreen => "Không toàn màn hình",
        FullscreenVerdict.Excluded => "Đã loại trừ",
        _ => "Không thể phán đoán",
    };

    /// <summary>
    /// Đẩy vòng lặp thông điệp tiến lên trong một khoảng thời gian.
    /// </summary>
    /// <remarks>
    /// Chỉ dùng để chờ trong phần chẩn đoán: kích hoạt cửa sổ, thay đổi DPI, tính lại của hook sự kiện đều phải nhờ vòng lặp thông điệp mới hoàn tất được.
    /// Ở đây dùng <c>DispatcherFrame</c> để bơm thông điệp thực sự, chứ không phải "ngủ một lát rồi kiểm tra lại" -
    /// cách sau sẽ khiến thông điệp kích hoạt bị kẹt trong hàng đợi mãi, chờ bao lâu cũng không có hiệu lực.
    /// </remarks>
    private static void PumpFor(int milliseconds)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();

        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            dispatcher);

        timer.Start();

        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>
    /// Thu thập các thay đổi phán đoán từ luồng hook. Callback không nằm trên luồng giao diện nên việc ghi phải khóa (lock).
    /// </summary>
    private sealed class ChangeCounter
    {
        private readonly List<bool> _values = [];

        public void Record(bool value)
        {
            lock (_values)
            {
                _values.Add(value);
            }
        }

        public bool[] Snapshot()
        {
            lock (_values)
            {
                return [.. _values];
            }
        }
    }
}
