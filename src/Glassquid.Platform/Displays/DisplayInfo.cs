namespace Glassquid.Platform.Displays;

/// <summary>Hình chữ nhật biểu diễn bằng pixel vật lý.</summary>
/// <remarks>
/// Định nghĩa riêng thay vì dùng lại <c>Rect</c> của WPF: kiểu này biểu diễn **pixel vật lý**, còn <c>Rect</c>
/// của WPF biểu diễn DIP. Việc dùng lẫn cả hai chính là nguyên nhân gây lệch vị trí trên màn hình phụ; tách chúng bằng hai kiểu khác nhau
/// giúp trình biên dịch ngăn chặn sẵn một phần lỗi.
/// </remarks>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

    public override string ToString() => $"({X},{Y} {Width}×{Height})";
}

/// <summary>
/// Vị trí, vùng làm việc và DPI của một màn hình trong không gian tọa độ **pixel vật lý**.
/// </summary>
public sealed record DisplayInfo(
    string DeviceName,
    bool IsPrimary,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint Dpi)
{
    /// <summary>Tỷ lệ thu phóng, ví dụ tỷ lệ 150% tương ứng với 1.5.</summary>
    public double ScaleFactor => Dpi / 96.0;

    /// <summary>Quy đổi pixel vật lý sang DIP.</summary>
    public double ToDips(int physicalPixels) => physicalPixels * 96.0 / Dpi;

    /// <summary>Quy đổi DIP sang pixel vật lý.</summary>
    public int ToPixels(double dips) => (int)Math.Round(dips * Dpi / 96.0);

    public override string ToString() =>
        $"{DeviceName}{(IsPrimary ? " (màn hình chính)" : string.Empty)} | ranh giới={Bounds} | vùng làm việc={WorkArea} DPI={Dpi} ({ScaleFactor:P0})";
}

/// <summary>Mức độ nhận biết DPI của tiến trình.</summary>
public enum DpiAwarenessLevel
{
    Unknown = -1,
    Unaware = 0,
    SystemAware = 1,
    PerMonitorAware = 2,
}

/// <summary>Kết quả dò tìm, bao gồm cả thông tin thô và chi tiết.</summary>
/// <remarks>
/// <see cref="DpiAwarenessLevel"/> không thể phân biệt PerMonitor **V1 và V2** — cả hai đều báo "Nhận biết từng màn hình".
/// Trong khi V1 không tự động tính lại tỷ lệ thu phóng khi cửa sổ chuyển qua màn hình khác, nên phải đối chiếu thêm handle ngữ cảnh mới xác nhận được là V2.
/// </remarks>
public readonly record struct DpiAwarenessInfo(
    DpiAwarenessLevel Level,
    bool IsPerMonitorV2,
    string RawContext,
    uint SystemDpi)
{
    public string Describe() => Level switch
    {
        DpiAwarenessLevel.PerMonitorAware when IsPerMonitorV2 => "Nhận biết từng màn hình V2 (PerMonitorV2)",
        DpiAwarenessLevel.PerMonitorAware => "Nhận biết từng màn hình V1 (PerMonitor)",
        DpiAwarenessLevel.SystemAware => "Nhận biết theo hệ thống (System Aware) — không đổi tỷ lệ khi chuyển màn hình",
        DpiAwarenessLevel.Unaware => "Không nhận biết DPI — Windows sẽ kéo giãn tọa độ",
        _ => "Không xác định",
    };
}