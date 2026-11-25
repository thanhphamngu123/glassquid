using Glassquid.Core.Models.Settings;
using Glassquid.Platform.Displays;

namespace Glassquid.Dock;

/// <summary>
/// Bộ tính toán duy nhất cho "vị trí đặt Dock".
/// </summary>
/// <remarks>
/// Đầu vào là vùng làm việc của màn hình và kích thước cửa sổ theo <strong>pixel vật lý</strong>, đầu ra là hình chữ nhật theo pixel vật lý;
/// độ lệch (DIP) được quy đổi bên trong theo <strong>DPI riêng của màn hình đích</strong>, không dùng DPI hệ thống —
/// dùng sai DPI khi quy đổi liên màn hình chính là nguyên nhân gốc gây lệch màn hình phụ đã kiểm chứng ở giai đoạn 2.
///
/// <para>
/// Phép tính là hàm thuần túy: không chạm tới cửa sổ, không chạm tới interop, cùng một đầu vào luôn trả về cùng một hình chữ nhật.
/// Do đó tính đúng đắn của bốn vị trí × đa màn hình có thể kiểm thử đơn vị ngoại tuyến; kiểm thử hồi quy trên máy thật với cửa sổ thực chỉ chịu trách nhiệm xác minh
/// lớp "cửa sổ WPF thực sự nằm đúng vào hình chữ nhật đã tính".
/// </para>
///
/// <para>
/// Kết quả đảm bảo <strong>nằm hoàn toàn trong vùng làm việc</strong> (D-15): khi màn hình không chứa hết, cửa sổ sẽ bị kẹp lại
/// vào mép vùng làm việc, tuyệt đối không vượt biên. Trường hợp ghim nhiều mục quá một hàng không chứa hết sẽ được xử lý bằng cuộn nội dung
/// (vùng chứa cuộn ở đợt 3), không đẩy cửa sổ ra ngoài màn hình.
/// </para>
/// </remarks>
public static class DockPlacementCalculator
{
    /// <summary>
    /// Tính toán hình chữ nhật đích của cửa sổ Dock (pixel vật lý).
    /// </summary>
    /// <param name="workArea">Vùng làm việc của màn hình đích (tránh thanh tác vụ), tính bằng pixel vật lý.</param>
    /// <param name="placement">Vị trí neo.</param>
    /// <param name="offsetXDips">Tinh chỉnh ngang, DIP. Giá trị dương sang phải, giá trị âm sang trái.</param>
    /// <param name="offsetYDips">Tinh chỉnh dọc, DIP. Giá trị dương xuống dưới, giá trị âm lên trên.</param>
    /// <param name="windowWidthPixels">Chiều rộng cửa sổ, pixel vật lý.</param>
    /// <param name="windowHeightPixels">Chiều cao cửa sổ, pixel vật lý.</param>
    /// <param name="dpi">DPI của màn hình đích. Dùng để quy đổi độ lệch từ DIP sang pixel.</param>
    public static PixelRect Calculate(
        PixelRect workArea,
        DockPlacement placement,
        double offsetXDips,
        double offsetYDips,
        int windowWidthPixels,
        int windowHeightPixels,
        uint dpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowWidthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowHeightPixels);

        var offsetX = ToPixels(offsetXDips, dpi);
        var offsetY = ToPixels(offsetYDips, dpi);

        var (anchorX, anchorY) = placement switch
        {
            // Ngữ nghĩa điểm neo: theo chiều ngang đặt trước theo vị trí, theo chiều dọc bám sát mép vùng làm việc.
            // BottomCenter căn giữa ngang; BottomLeft/Right bám mép trái/phải vùng làm việc; TopCenter căn giữa trên cùng.
            DockPlacement.BottomCenter => (workArea.X + ((workArea.Width - windowWidthPixels) / 2), workArea.Bottom - windowHeightPixels),
            DockPlacement.BottomLeft => (workArea.X, workArea.Bottom - windowHeightPixels),
            DockPlacement.BottomRight => (workArea.Right - windowWidthPixels, workArea.Bottom - windowHeightPixels),
            DockPlacement.TopCenter => (workArea.X + ((workArea.Width - windowWidthPixels) / 2), workArea.Y),
            _ => (workArea.X + ((workArea.Width - windowWidthPixels) / 2), workArea.Bottom - windowHeightPixels),
        };

        var x = anchorX + offsetX;
        var y = anchorY + offsetY;

        return ClampToWorkArea(new PixelRect(x, y, windowWidthPixels, windowHeightPixels), workArea);
    }

    /// <summary>
    /// Kẹp hình chữ nhật trở lại vùng làm việc: nằm hoàn toàn trong vùng làm việc (D-15).
    /// </summary>
    /// <remarks>
    /// Trong trường hợp cực đoan không chứa nổi (cửa sổ rộng/cao hơn cả vùng làm việc), căn theo gốc vùng làm việc
    /// và cắt theo kích thước vùng làm việc — tuyệt đối không đặt cửa sổ ra ngoài màn hình.
    /// </remarks>
    public static PixelRect ClampToWorkArea(PixelRect rect, PixelRect workArea)
    {
        var width = Math.Min(rect.Width, workArea.Width);
        var height = Math.Min(rect.Height, workArea.Height);

        var x = rect.X;
        var y = rect.Y;

        if (x < workArea.X)
        {
            x = workArea.X;
        }
        else if (x + width > workArea.Right)
        {
            x = workArea.Right - width;
        }

        if (y < workArea.Y)
        {
            y = workArea.Y;
        }
        else if (y + height > workArea.Bottom)
        {
            y = workArea.Bottom - height;
        }

        return new PixelRect(x, y, width, height);
    }

    /// <summary>Quy đổi độ lệch từ DIP sang pixel vật lý theo DPI riêng của màn hình đích.</summary>
    private static int ToPixels(double dips, uint dpi)
        => (int)Math.Round(dips * dpi / 96.0, MidpointRounding.AwayFromZero);
}
