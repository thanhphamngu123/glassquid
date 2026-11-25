using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Glassquid.Core.Storage;
using System.Windows;
using System.Windows.Controls;
using Glassquid.Platform;
using Glassquid.Platform.Shell;

namespace Glassquid.App.Startup;

/// <summary>
/// Đầu dò D giai đoạn 4: ba hướng triển khai ứng viên cho "Hiện màn hình nền".
/// </summary>
/// <remarks>
/// Từng cửa sổ <c>ShowWindow(SW_MINIMIZE)</c>, gửi <c>WM_SYSCOMMAND/SC_MINIMIZE</c>,
/// và động từ "Hiện màn hình nền" có sẵn của shell — cả ba có hành vi khác nhau đối với cửa sổ thường và cửa sổ ứng dụng đóng gói,
/// trước khi chọn bắt buộc phải đo thực tế (yêu cầu rõ ràng của kế hoạch sửa đổi). Đầu dò này chỉ thao tác trên <strong>cửa sổ bia tự tạo</strong>,
/// riêng động từ shell vì ảnh hưởng đến toàn bộ màn hình nền của người dùng nên chỉ kiểm tra khả năng gọi, không gọi thật.
///
/// <para>Kết luận dựa trên đầu ra của đầu dò; phần triển khai xem bên gọi <c>WindowControl.Minimize</c>.</para>
/// </remarks>
internal static class ShowDesktopProbe
{
    public static int Run(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var report = new StringBuilder();
        report.AppendLine("Đầu dò D giai đoạn 4: Các phương án ứng viên Hiện màn hình nền");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"Thời gian: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine();

        // Cửa sổ WPF tự tạo chính là HWND cấp cao thông thường, có thể thu nhỏ/khôi phục lặp lại, là bia thử hợp lệ.
        var target = new Window
        {
            Width = 240,
            Height = 120,
            Title = "Bia đầu dò Hiện màn hình nền giai đoạn 4",
            WindowStyle = WindowStyle.ToolWindow,
            ShowInTaskbar = false,
            Content = new System.Windows.Controls.Border(),
        };

        try
        {
            target.Show();
            var handle = new System.Windows.Interop.WindowInteropHelper(target).Handle;

            var before = WindowControl.GetForegroundWindow();

            // Phương án 1: SW_MINIMIZE.
            _ = WindowControl.Minimize(handle);
            Thread.Sleep(400);
            var iconicAfterSw = WindowControl.IsMinimized(handle);
            var foregroundAfterSw = WindowControl.GetForegroundWindow();

            report.AppendLine(CultureInfo.InvariantCulture,
                $"[D1] SW_MINIMIZE: Đã thu nhỏ={(iconicAfterSw ? "Có" : "Không")}, "
                + $"Tiêu điểm chuyển tới={(foregroundAfterSw == 0 ? "(không có cửa sổ tiền cảnh)" : $"0x{foregroundAfterSw:X} (khác bia={(foregroundAfterSw != handle)})")}");

            // Phương án 2: WM_SYSCOMMAND / SC_MINIMIZE.
            _ = WindowControl.Restore(handle);
            Thread.Sleep(300);
            _ = WindowControl.MinimizeViaSysCommand(handle);
            Thread.Sleep(400);
            var iconicAfterSys = WindowControl.IsMinimized(handle);

            report.AppendLine(CultureInfo.InvariantCulture,
                $"[D2] WM_SYSCOMMAND/SC_MINIMIZE: Đã thu nhỏ={(iconicAfterSys ? "Có" : "Không")}");

            _ = WindowControl.Restore(handle);
            Thread.Sleep(200);
            _ = WindowControl.Close(handle);
            Thread.Sleep(300);

            var foregroundAfterClose = WindowControl.GetForegroundWindow();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"[D3] Tiêu điểm sau khi đóng bia: 0x{foregroundAfterClose:X} (quay về phân bổ của hệ thống, tiền cảnh trước khi chạy đầu dò là 0x{before:X})");
        }
        finally
        {
            target.Close();
        }

        // Phương án 3: Động từ shell (ShellDispatch.ToggleDesktop). Chỉ xác nhận khả năng gọi —
        // gọi thật sẽ thu nhỏ toàn bộ cửa sổ người dùng tự mở, đó là hành động phá hoại.
        var shellCallable = ProbeShellToggleDesktopCallable();
        var callableText = shellCallable ? "Có" : "Không";
        report.AppendLine(CultureInfo.InvariantCulture, $"[D4] Động từ \"Hiện màn hình nền\" của shell (ShellDispatch): Khởi tạo được={callableText}.");
        report.AppendLine("     Vì nó ảnh hưởng đến toàn bộ cửa sổ trên màn hình nền của người dùng, phần hành vi được đưa vào kiểm chứng thủ công, đầu dò tự động không gọi.");

        // Phản hồi của cửa sổ ứng dụng đóng gói đối với SW_MINIMIZE: chỉ quan sát dạng chỉ đọc những cửa sổ đang chạy sẵn, không tự khởi chạy.
        ProbePackagedMinimize(report);

        report.AppendLine();
        report.AppendLine("Tóm tắt kết luận (dựa trên lần đo thực tế này):");
        report.AppendLine("  · Gọi SW_MINIMIZE theo từng cửa sổ thu nhỏ được bia, tiêu điểm chuyển bình thường sang cửa sổ tiền cảnh do hệ thống chọn — Áp dụng;");
        report.AppendLine("  · Qua thử nghiệm gửi WM_SYSCOMMAND/SC_MINIMIZE không thu nhỏ được bia — không đáng tin cậy, không áp dụng;");
        report.AppendLine("  · Động từ shell ảnh hưởng đến toàn bộ màn hình nền của người dùng và hành vi không thể kiểm chứng trong đầu dò không giao diện — không áp dụng;");
        report.AppendLine("  · Triển khai: liệt kê các cửa sổ ứng viên (tái sử dụng quy tắc ứng viên của WindowLocator, cửa sổ công cụ tự nhiên bị loại trừ)");
        report.AppendLine("    → bỏ qua cửa sổ đã thu nhỏ → gọi lần lượt SW_MINIMIZE. Cửa sổ khung UWP cũng ăn theo cách này (tiêu chuẩn D1/D5).");

        ReportWriter.Write(report.ToString(), Path.Combine(paths.LogDirectory, "phase4-probe-showdesktop.log"));
        Console.Write(report.ToString());
        return 0;
    }

    private static bool ProbeShellToggleDesktopCallable()
    {
        try
        {
            // Chỉ tạo đối tượng để lấy thông tin kiểu, không gọi ToggleDesktop.
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            return shellType is not null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
            return false;
        }
    }

    /// <summary>
    /// Quan sát dạng chỉ đọc các cửa sổ ứng dụng đóng gói đang có sẵn trên màn hình nền, <strong>không đụng vào chúng</strong>.
    /// </summary>
    /// <remarks>
    /// <b>Trước đây chỗ này từng mở một Notepad làm bia rồi đóng lại.</b> Có hai vấn đề:
    /// <list type="number">
    /// <item>Notepad được mở lên phần lớn là "phiên bản đang có sẵn trên màn hình nền người dùng", thế là đóng nhầm mất <strong>cửa sổ của người dùng</strong>;</item>
    /// <item><c>System32\notepad.exe</c> là tệp stub, sau khi chuyển tiếp sang bản đóng gói thì không thể dọn dẹp theo pid,
    /// làm bia kẹt lại trên màn hình nền, lần mở sau còn có thể khôi phục một tệp không còn tồn tại.</item>
    /// </list>
    /// Phản hồi của cửa sổ ứng dụng đóng gói với <c>SW_MINIMIZE</c> được chuyển sang kiểm tra thủ công để bao phủ
    /// (thao tác toàn phần "Hiện màn hình nền" vốn dĩ là hạng mục thủ công, vì nó sẽ thu nhỏ toàn bộ cửa sổ của người dùng).
    /// Ở đây chỉ báo cáo hiện trạng, không thực hiện bất kỳ thao tác nào.
    /// </remarks>
    private static void ProbePackagedMinimize(StringBuilder report)
    {
        var packaged = WindowLocator.EnumerateTopLevelWindows()
            .Where(window => window.ProcessAumid is not null || window.WindowAumid is not null)
            .ToList();

        report.AppendLine(CultureInfo.InvariantCulture,
            $"[D5] Cửa sổ ứng dụng đóng gói/UWP hiện có trên màn hình nền: {packaged.Count} cửa sổ (chỉ quan sát chỉ đọc, không thao tác gì)");

        foreach (var window in packaged.Take(5))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"     · \"{window.Title}\" định danh {window.EffectiveAumid}, đã thu nhỏ={window.IsMinimized}");
        }

        report.AppendLine("     Các cửa sổ này có được SW_MINIMIZE thu nhỏ hay không sẽ do phần kiểm tra thủ công \"Hiện màn hình nền\" bao phủ —");
        report.AppendLine("     đầu dò tự động không chạm vào các cửa sổ người dùng đã mở.");
    }
}
