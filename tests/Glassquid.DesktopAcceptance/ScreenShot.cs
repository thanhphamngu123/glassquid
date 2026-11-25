using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glassquid.DesktopAcceptance;

/// <summary>
/// Lưu ảnh chụp toàn màn hình làm bằng chứng khi thất bại và bản ghi lại trạng thái màn hình tại thời điểm đó.
/// </summary>
/// <remarks>
/// Dữ liệu lưu ở đây là <strong>điểm ảnh màn hình thật</strong>, không phải ảnh kết xuất WPF của một cửa sổ cụ thể:
/// trình điều khiển desktop tác động lên đầu vào thật, nên bằng chứng cũng phải nằm ở cùng tầng đó. Điểm ảnh được lấy từ GDI,
/// chỉ mượn bộ mã hóa của WPF để ghi PNG, do đó không cần phụ thuộc bất kỳ thư viện xử lý ảnh nào.
/// </remarks>
internal static class ScreenShot
{
    internal static string? TryCapture(string path)
    {
        try
        {
            var area = Win32.VirtualScreen();
            if (area.IsEmpty)
            {
                return null;
            }

            var pixels = Win32.CaptureBgra(area, out var stride);
            var source = BitmapSource.Create(
                area.Width,
                area.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            encoder.Save(stream);

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Ảnh chụp màn hình chỉ là bằng chứng, nếu không chụp được thì không được đánh dấu ca kiểm thử là thất bại.
            return null;
        }
    }
}