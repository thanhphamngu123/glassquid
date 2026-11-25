using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Glassquid.Platform.Displays;

namespace Glassquid.App.Startup;

/// <summary>
/// Đo thực tế hai cách đặt cửa sổ lên màn hình chỉ định để xác định cách nào đáp ứng được định vị đa màn hình.
/// </summary>
/// <remarks>
/// Đây là thí nghiệm then chốt của giai đoạn 2 và **không thể xác định bằng suy luận**.
///
/// Câu hỏi cần trả lời: để đặt cửa sổ vào một vị trí vật lý trên màn hình phụ, cần tính như thế nào?
/// Cách nghĩ trực giác là "dùng DPI của màn hình đích để quy đổi pixel vật lý sang DIP, rồi gán Left/Top của WPF".
/// Đo thực tế cho thấy trực giác này sai trong môi trường DPI hỗn hợp: Left/Top của WPF dùng DPI của **màn hình hiện tại
/// đang chứa cửa sổ**, nên khi đặt từ màn hình chính (150%) sang màn hình phụ (125%), X bị lệch 640 pixel vật lý —
/// kích thước đúng, vị trí sai, không báo lỗi mà chỉ xuất hiện sai chỗ.
///
/// Vì vậy tại đây đo thực tế đồng thời cả hai hướng:
///   A: Left/Top của WPF (quy đổi theo DPI màn hình đích)
///   B: SetWindowPos của Win32 (pixel vật lý)
/// Trước mỗi thí nghiệm đều đưa cửa sổ về màn hình chính để đảm bảo điểm xuất phát của hai cách như nhau.
/// </remarks>
internal static class WindowPlacementProbe
{
    private const int ProbeWidth = 320;
    private const int ProbeHeight = 80;

    private readonly record struct PlacementResult(PixelRect Expected, PixelRect Actual)
    {
        public int DeltaX => Actual.X - Expected.X;

        public int DeltaY => Actual.Y - Expected.Y;

        public bool IsExact => DeltaX == 0 && DeltaY == 0;

        public string Describe() =>
            $"Kỳ vọng {Expected}, thực tế {Actual}, độ lệch ({DeltaX},{DeltaY})";
    }

    public static int Run(StringBuilder report, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(displays);

        var culture = CultureInfo.InvariantCulture;

        report.AppendLine("[Đo thực tế định vị đa màn hình]");
        report.AppendLine("  Vị trí đích lấy chính giữa cạnh dưới vùng làm việc mỗi màn hình, tức nơi Dock sẽ đặt.");
        report.AppendLine("  A = Left/Top của WPF (quy đổi theo DPI màn hình đích); B = Win32 SetWindowPos (pixel vật lý).");
        report.AppendLine();

        if (displays.Count == 0)
        {
            report.AppendLine("✗ Không có màn hình nào để thực hiện thí nghiệm đặt vị trí.");
            return 1;
        }

        var primary = displays.FirstOrDefault(static d => d.IsPrimary) ?? displays[0];

        var probeWindow = new Window
        {
            Width = 200,
            Height = 60,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Opacity = 0.01,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        var methodAFailures = 0;
        var methodBFailures = 0;

        try
        {
            probeWindow.Show();

            var handle = new WindowInteropHelper(probeWindow).Handle;
            if (handle == 0)
            {
                report.AppendLine("✗ Cửa sổ thăm dò không lấy được tay cầm (handle).");
                return 1;
            }

            foreach (var display in displays)
            {
                var target = BottomCentreOfWorkArea(display);

                report.AppendLine(culture, $"· {display.DeviceName} ({display.ScaleFactor:P0})");

                // ---- Cách A: Left/Top của WPF ----
                ResetToPrimary(probeWindow, handle, primary);
                var resultA = PlaceWithWpfCoordinates(probeWindow, handle, display, target);
                report.AppendLine(culture, $"    A  WPF Left/Top: {resultA.Describe()}");

                if (resultA.IsExact)
                {
                    report.AppendLine("       ✓ Vị trí chính xác");
                }
                else
                {
                    methodAFailures++;
                    report.AppendLine("       ✗ Bị lệch");
                }

                // ---- Cách B: Pixel vật lý Win32 ----
                ResetToPrimary(probeWindow, handle, primary);
                var resultB = PlaceWithPixelRect(handle, target);
                report.AppendLine(culture, $"    B  SetWindowPos: {resultB.Describe()}");

                if (resultB.IsExact)
                {
                    report.AppendLine("       ✓ Vị trí chính xác");
                }
                else
                {
                    methodBFailures++;
                    report.AppendLine("       ✗ Bị lệch");
                }

                report.AppendLine();
            }

            report.AppendLine("  Tóm tắt:");
            report.AppendLine(culture, $"    Cách A (quy đổi DIP của WPF): {displays.Count - methodAFailures}/{displays.Count} màn hình chính xác");
            report.AppendLine(culture, $"    Cách B (pixel vật lý Win32): {displays.Count - methodBFailures}/{displays.Count} màn hình chính xác");
        }
        finally
        {
            probeWindow.Close();
        }

        if (methodAFailures > 0 && methodBFailures == 0)
        {
            report.AppendLine("    → Chọn Cách B. Định vị xuyên màn hình không được phụ thuộc vào quy đổi Left/Top của WPF,");
            report.AppendLine("      nếu không dưới môi trường DPI hỗn hợp cửa sổ sẽ xuất hiện sai vị trí mà không báo lỗi.");
        }
        else if (methodAFailures > 0 && methodBFailures > 0)
        {
            report.AppendLine("    → Cả hai cách đều bị lệch, cần tiếp tục điều tra.");
        }

        report.AppendLine();

        // Chỉ khi Cách B bị lệch mới là vấn đề thực sự cần giải quyết: đó chính là hướng dự kiến áp dụng.
        return methodBFailures;
    }

    /// <summary>Vị trí đích: chính giữa cạnh dưới vùng làm việc của màn hình đó, chừa lề 8 DIP.</summary>
    private static PixelRect BottomCentreOfWorkArea(DisplayInfo display)
    {
        var work = display.WorkArea;
        return new PixelRect(
            work.X + ((work.Width - ProbeWidth) / 2),
            work.Bottom - ProbeHeight - display.ToPixels(8),
            ProbeWidth,
            ProbeHeight);
    }

    /// <summary>Đưa cửa sổ về màn hình chính để đảm bảo điểm xuất phát của hai cách như nhau.</summary>
    private static void ResetToPrimary(Window window, nint handle, DisplayInfo primary)
    {
        // Khi đặt lại cũng đi qua pixel vật lý để tránh bản thân việc đặt lại gây sai lệch.
        DisplayService.MoveWindowToPixelRect(
            handle,
            new PixelRect(primary.WorkArea.X + 40, primary.WorkArea.Y + 40, ProbeWidth, ProbeHeight));

        window.UpdateLayout();
    }

    /// <summary>Cách A: Dùng DPI của màn hình đích để quy đổi tọa độ vật lý sang DIP rồi gán cho WPF.</summary>
    private static PlacementResult PlaceWithWpfCoordinates(
        Window window,
        nint handle,
        DisplayInfo display,
        PixelRect target)
    {
        window.Width = display.ToDips(target.Width);
        window.Height = display.ToDips(target.Height);
        window.Left = display.ToDips(target.X);
        window.Top = display.ToDips(target.Y);
        window.UpdateLayout();

        var actual = DisplayService.GetWindowPixelBounds(handle) ?? default;
        return new PlacementResult(target, actual);
    }

    /// <summary>Cách B: Đặt trực tiếp theo pixel vật lý.</summary>
    private static PlacementResult PlaceWithPixelRect(nint handle, PixelRect target)
    {
        // Chỉ gọi một lần. Việc đặt hai lần và thứ tự thời gian DPI ở giữa đã được đóng gói bên trong phương thức dùng chung —
        // chương trình thăm dò cũng chỉ gọi một lần để xác minh lớp đóng gói đó thực sự có hiệu lực;
        // nếu tại đây vẫn phải tự gọi bù lần thứ hai thì chứng tỏ việc đóng gói chưa triệt để.
        DisplayService.MoveWindowToPixelRect(handle, target);

        var actual = DisplayService.GetWindowPixelBounds(handle) ?? default;
        return new PlacementResult(target, actual);
    }
}