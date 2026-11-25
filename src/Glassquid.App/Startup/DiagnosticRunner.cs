using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Glassquid.Core.Storage;
using Glassquid.Platform;
using Glassquid.Platform.Displays;

namespace Glassquid.App.Startup;

/// <summary>
/// Xuất dữ liệu đo đạc thực tế về hiển thị, không gian tọa độ và phát hiện toàn màn hình của máy hiện tại.
/// </summary>
/// <remarks>
/// Đây là công cụ cốt lõi của Giai đoạn 2: định vị đa màn hình, vùng chạm và vị trí menu khay hệ thống đều dựa trên việc hệ tọa độ có đúng hay không,
/// mà hệ tọa độ sai thì sẽ không báo lỗi, chỉ làm mọi thứ xuất hiện sai chỗ. Vì vậy cần đo và ghi lại sự thật trước,
/// rồi mới viết mã dựa trên đó.
///
/// Trọng tâm là đặt song song hai góc nhìn tọa độ của <strong>cùng một máy</strong>: điểm ảnh vật lý của Win32 và DIP của WPF.
/// Lỗi góc nóng (hot corner) trên màn hình phụ mất tác dụng trong báo cáo cơ sở bản chất chính là do hai góc nhìn này bị trộn lẫn với nhau.
///
/// Phát hiện toàn màn hình cũng dùng cách tương tự: nó quyết định việc gọi Dock từ cạnh màn hình, góc nóng và nút hông chuột có bị ức chế hay không,
/// và nó cũng không báo lỗi — phán đoán sai sẽ biểu hiện thành "Dock không bật lên" hoặc "đang xem video thì tự bật lên".
/// Do đó phần này cũng sẽ tạo thật một cửa sổ toàn màn hình để kiểm thử.
/// </remarks>
internal static class DiagnosticRunner
{
    public static int Run(AppPaths paths, Stream? acceptanceReport = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var report = new StringBuilder();
        var failures = 0;

        report.AppendLine("Chẩn đoán hiển thị và không gian tọa độ Glassquid");
        report.AppendLine(new string('=', 52));
        report.AppendLine(CultureInfo.InvariantCulture, $"Thời gian: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Hệ điều hành: Windows {WindowsCapabilities.OsVersion} (Build {WindowsCapabilities.BuildNumber})");
        report.AppendLine();

        // ---- Nhận biết DPI ----
        var awareness = DisplayService.ProbeDpiAwareness();

        report.AppendLine("[Nhận biết DPI]");
        report.AppendLine(CultureInfo.InvariantCulture, $"Ngữ cảnh luồng: {awareness.RawContext}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Mức độ nhận biết: {awareness.Describe()}");
        report.AppendLine(CultureInfo.InvariantCulture, $"DPI hệ thống: {awareness.SystemDpi} ({awareness.SystemDpi / 96.0:P0})");

        if (awareness.Level != DpiAwarenessLevel.PerMonitorAware || !awareness.IsPerMonitorV2)
        {
            failures++;
            report.AppendLine("✗ Không phải PerMonitorV2. Khi cửa sổ sang màn hình khác sẽ không tự đổi tỷ lệ theo DPI của màn hình mới,");
            report.AppendLine("  và việc quy đổi giữa điểm ảnh vật lý với DIP cũng mất đi cơ sở duy nhất. Xem phần đối chiếu tọa độ bên dưới.");
        }
        else
        {
            report.AppendLine("✓ Đã có nhận biết DPI theo từng màn hình V2, tọa độ có thể quy đổi riêng theo từng màn hình.");
        }

        report.AppendLine();

        // ---- Màn hình (điểm ảnh vật lý) ----
        var displays = DisplayService.Enumerate();

        report.AppendLine("[Màn hình: điểm ảnh vật lý Win32]");
        if (displays.Count == 0)
        {
            failures++;
            report.AppendLine("✗ Không liệt kê được màn hình nào.");
        }

        foreach (var display in displays)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"· {display}");
        }

        report.AppendLine();

        // ---- Góc nhìn WPF (DIP) ----
        report.AppendLine("[Góc nhìn WPF: DIP]");
        report.AppendLine(CultureInfo.InvariantCulture, $"Vùng làm việc màn hình chính: {SystemParameters.WorkArea.Width:F0}×{SystemParameters.WorkArea.Height:F0} DIP, gốc ({SystemParameters.WorkArea.Left:F0},{SystemParameters.WorkArea.Top:F0})");
        report.AppendLine(CultureInfo.InvariantCulture, $"Màn hình ảo: {SystemParameters.VirtualScreenWidth:F0}×{SystemParameters.VirtualScreenHeight:F0} DIP, gốc ({SystemParameters.VirtualScreenLeft:F0},{SystemParameters.VirtualScreenTop:F0})");

        report.AppendLine();
        report.AppendLine("[Đối chiếu hai không gian tọa độ (quan trọng)]");
        report.AppendLine("  Điểm ảnh vật lý đến từ Win32, DIP đến từ WPF. Hai giá trị này chỉ bằng nhau khi tỷ lệ 100% và trùng gốc tọa độ.");
        report.AppendLine();

        foreach (var display in displays)
        {
            var physical = display.Bounds;
            var dipWidth = display.ToDips(physical.Width);
            var dipHeight = display.ToDips(physical.Height);

            report.AppendLine(CultureInfo.InvariantCulture, $"· {display.DeviceName}{(display.IsPrimary ? " (Màn hình chính)" : string.Empty)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"    Điểm ảnh vật lý: gốc ({physical.X},{physical.Y})  kích thước {physical.Width}×{physical.Height}");
            report.AppendLine(CultureInfo.InvariantCulture, $"    DIP tương ứng: kích thước khoảng {dipWidth:F0}×{dipHeight:F0} (theo tỷ lệ {display.ScaleFactor:P0} của màn hình này)");
            report.AppendLine(CultureInfo.InvariantCulture, $"    Vùng làm việc: vật lý {display.WorkArea}  →  DIP khoảng {display.ToDips(display.WorkArea.Width):F0}×{display.ToDips(display.WorkArea.Height):F0}");
        }

        report.AppendLine();

        // ---- Kiểm tra tính nhất quán ----
        report.AppendLine("[Kiểm tra tính nhất quán]");

        var primary = displays.FirstOrDefault(static d => d.IsPrimary);
        if (primary is not null)
        {
            var expectedDipWidth = primary.ToDips(primary.WorkArea.Width);
            var actualDipWidth = SystemParameters.WorkArea.Width;
            var delta = Math.Abs(expectedDipWidth - actualDipWidth);

            report.AppendLine(CultureInfo.InvariantCulture, $"Chiều rộng vùng làm việc màn hình chính: quy đổi vật lý {expectedDipWidth:F0} DIP vs báo cáo WPF {actualDipWidth:F0} DIP (chênh {delta:F0})");

            // Cho phép sai số 2 DIP: cách làm tròn vùng làm việc giữa Win32 và WPF hơi khác nhau.
            if (delta > 2)
            {
                failures++;
                report.AppendLine("✗ Hai giá trị không khớp. Điều này cho thấy chuẩn quy đổi khác với chuẩn thực tế mà WPF đang dùng,");
                report.AppendLine("  mọi phép quy đổi thủ công 'điểm ảnh vật lý ↔ DIP' đều sẽ bị lệch. Đây là vấn đề cần giải quyết trước.");
            }
            else
            {
                report.AppendLine("✓ Chuẩn quy đổi nhất quán với WPF, có thể quy đổi điểm ảnh ↔ DIP theo DPI của từng màn hình.");
            }
        }

        // DPI hỗn hợp là tình huống dễ làm lộ lỗi tọa độ nhất, đáng để chỉ rõ riêng.
        var distinctScales = displays.Select(static d => d.Dpi).Distinct().Count();
        report.AppendLine(CultureInfo.InvariantCulture, $"Số lượng màn hình: {displays.Count}, số mức DPI khác nhau: {distinctScales}");
        if (distinctScales > 1)
        {
            report.AppendLine("· Máy này là môi trường DPI hỗn hợp: tọa độ liên màn hình phải quy đổi theo DPI riêng của từng màn hình, không thể dùng một hệ số tỷ lệ duy nhất.");
        }

        if (displays.Any(static d => d.Bounds.X < 0 || d.Bounds.Y < 0))
        {
            report.AppendLine("· Có màn hình tọa độ âm (bên trái hoặc phía trên màn hình chính): phép tính định vị phải hỗ trợ gốc tọa độ âm, không được giả định bắt đầu từ 0.");
        }

        // Thử nghiệm bố trí phải dùng cửa sổ thật, vì thứ cần kiểm chứng chính là ánh xạ thực tế giữa WPF và Win32,
        // chứ không phải ánh xạ mà ta tự suy đoán.
        failures += WindowPlacementProbe.Run(report, displays);

        report.AppendLine();

        // Phát hiện toàn màn hình cũng phải kiểm chứng bằng cửa sổ thật: các dạng có thể kích hoạt nó (toàn màn hình không viền, phóng đại tối đa,
        // toàn màn hình ở màn hình phụ, game toàn màn hình độc quyền) khác nhau rất nhiều, chỉ đọc mã nguồn không thể phán đoán hết được.
        failures += FullscreenProbe.Run(report, displays);

        report.AppendLine();
        report.AppendLine(new string('=', 52));
        report.AppendLine(failures == 0 ? "Kết luận: Cả không gian tọa độ và phát hiện toàn màn hình đều sẵn sàng cho phần triển khai tiếp theo." : $"Kết luận: Có {failures} mục cần giải quyết trước.");

        var logPath = Path.Combine(paths.LogDirectory, "diagnose.txt");
        var text = report.ToString();
        if (acceptanceReport is null) ReportWriter.Write(text, logPath);
        else
        {
            AcceptanceEntryPoint.WriteText(acceptanceReport, text);
            ReportWriter.Write(text, null);
        }

        return failures == 0 ? 0 : 1;
    }
}
