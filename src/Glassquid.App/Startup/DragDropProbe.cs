using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Glassquid.Core.Storage;
using Glassquid.Platform.Shell;

namespace Glassquid.App.Startup;

/// <summary>
/// Giai đoạn 4 Đầu dò C: Đường ống kéo thả — quyền sở hữu đăng ký giữa WPF và OLE gốc trên cùng một handle cửa sổ.
/// </summary>
/// <remarks>
/// Kết luận trước (nhiệm vụ của đầu dò này là xác minh điều đó): Khi <see cref="System.Windows.UIElement.AllowDrop"/>
/// được đặt là true, WPF <strong>tự mình</strong> đăng ký <c>IDropTarget</c> của OLE với handle cửa sổ,
/// bằng chứng là khi OLE đăng ký mục tiêu kéo thả sẽ để lại atom <c>OleDropTargetInterface</c>
/// trong bảng thuộc tính của handle (hành vi công khai của <c>RegisterDragDrop</c>, được dò qua
/// <see cref="DropTargetRegistration.IsOleDropTargetRegistered"/>). Do đó:
///
/// <list type="number">
/// <item>Thao tác kéo vào từ bên ngoài (Explorer → Dock) <strong>thống nhất đi qua sự kiện Drop của WPF</strong>,
/// WPF chịu trách nhiệm cho một đăng ký OLE duy nhất đó; mã sản phẩm tuyệt đối không gọi
/// <c>RegisterDragDrop</c> thêm lần nữa trên cùng một handle — hai bên đăng ký ghi đè lẫn nhau sẽ gây hành vi không thể đoán trước.</item>
/// <item>Danh sách tệp được kéo vào đi qua <c>DataObject</c> của WPF dưới định dạng <c>FileDrop</c>
/// (dạng được quản lý của CF_HDROP), đường dẫn có tiếng Trung và khoảng trắng do WPF đảm bảo truyền chính xác.</item>
/// <item>Thao tác kéo thật liên tiến trình (nhấn giữ kéo từ Explorer vào) không thể mô phỏng bằng chương trình trong đầu dò không giao diện,
/// thuộc danh sách kiểm tra thủ công.</item>
/// </list>
///
/// Kết quả đầu ra được lưu tại <c>docs/evidence/giai-doan-4-ket-qua-do-kiem.txt</c>.
/// </remarks>
internal static class DragDropProbe
{
    public static int Run(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var report = new StringBuilder();
        report.AppendLine("Giai đoạn 4 Đầu dò C: Đường ống kéo thả (quyền sở hữu đăng ký WPF / OLE)");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"Thời gian: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine();

        var window = new Window
        {
            Width = 200,
            Height = 100,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowDrop = true,
            Content = new Border(),
        };

        try
        {
            window.Show();

            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            report.AppendLine(CultureInfo.InvariantCulture, $"[C1] Handle cửa sổ kiểm thử 0x{handle:X}, AllowDrop = true.");

            var registered = DropTargetRegistration.IsOleDropTargetRegistered(handle);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"[C2] Atom OleDropTargetInterface trong bảng thuộc tính handle: {(registered ? "Tồn tại" : "Không tồn tại")}");

            report.AppendLine();
            report.AppendLine(registered
                ? "Kết luận: WPF đã tự hoàn tất đăng ký OLE IDropTarget. Thao tác kéo vào từ bên ngoài của Dock đi qua sự kiện WPF Drop, "
                  + "mã sản phẩm không gọi thêm RegisterDragDrop trên cùng một handle; tệp kéo vào đến qua định dạng FileDrop của DataObject."
                : "Kết luận: Không phát hiện atom đăng ký OLE — thời điểm đăng ký của WPF không khớp với dự kiến, cần kiểm tra thủ công trước khi xác định quyền sở hữu.");

            report.AppendLine("Thao tác kéo thật liên tiến trình (Explorer → Dock, đường dẫn có tiếng Trung và khoảng trắng) được đưa vào danh sách kiểm tra thủ công.");
        }
        finally
        {
            window.Close();
        }

        ReportWriter.Write(report.ToString(), Path.Combine(paths.LogDirectory, "phase4-probe-dragpipe.log"));
        Console.Write(report.ToString());
        return 0;
    }
}
