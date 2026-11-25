using System.Windows;
using System.Windows.Media;

namespace Glassquid.UI.Theming;

/// <summary>
/// Các màu dùng khi vẽ một cửa sổ, đã được tính sẵn theo chế độ sáng hoặc tối.
/// </summary>
/// <remarks>
/// Tạo bằng mã thay vì ResourceDictionary là để màu nhấn và tùy chọn "giảm độ trong suốt" có hiệu lực trực tiếp mà không cần đổi từ điển,
/// đồng thời đảm bảo cả ba nơi là Dock, Launcher và Trung tâm Cài đặt luôn phân giải ra cùng một bộ giá trị.
///
/// <b>Hai bộ màu nền bảng là có chủ đích.</b> Giai đoạn 1 chỉ có một bộ (55% trắng), kết quả là chất liệu hệ thống và lớp tô tự vẽ
/// chồng lên nhau tạo thành màu trắng đục xám vừa — chủ đề hệ thống trên máy là tối, DWM trả về Mica tối, phủ thêm một lớp trắng bán trong suốt lên trên,
/// hai lớp triệt tiêu nhau nên hình nền gần như không xuyên qua được. Vì vậy phải tách theo "phía sau có chất liệu hay không":
/// khi có chất liệu thì chỉ dìm sắc độ rất nhẹ, còn khi không có chất liệu thì lớp tô phải tự đảm nhận cảm giác kính.
/// </remarks>
public sealed class GlassPalette
{
    /// <summary>Màu nhấn mặc định: màu xanh dương theo thiết kế chỉ định.</summary>
    public static readonly Color DefaultAccent = Color.FromRgb(0x0A, 0x84, 0xFF);

    private GlassPalette(
        bool isDark,
        Color accent,
        Color acrylicOverlay,
        Color fallbackPanel,
        Color acrylicTint,
        byte acrylicTintAlpha,
        Color panelBorder,
        Color foreground,
        Color secondaryForeground)
    {
        IsDark = isDark;
        Accent = accent;
        AcrylicOverlayBackground = acrylicOverlay;
        FallbackPanelBackground = fallbackPanel;
        AcrylicTint = acrylicTint;
        AcrylicTintAlpha = acrylicTintAlpha;
        PanelBorder = panelBorder;
        Foreground = foreground;
        SecondaryForeground = secondaryForeground;
    }

    public bool IsDark { get; }

    public Color Accent { get; }

    /// <summary>
    /// Lớp tô phủ chồng lên khi Acrylic có hiệu lực. Phải rất nhạt — sắc độ đã do lớp Acrylic cung cấp,
    /// nếu phủ thêm một lớp đậm nữa sẽ che mất độ mờ, thành ra làm uổng công.
    /// </summary>
    public Color AcrylicOverlayBackground { get; }

    /// <summary>
    /// Lớp tô mỏng nhẹ cho bảng lồng nhau và khi không có chất liệu nền,
    /// đồng thời cố ý giữ lại chút độ xuyên thấu thay vì thoái lui thành mảng màu đặc.
    /// </summary>
    public Color FallbackPanelBackground { get; }

    /// <summary>
    /// Sắc độ giao cho lớp Acrylic.
    /// </summary>
    /// <remarks>
    /// Lớp này là chìa khóa cho giao diện sáng. Chất liệu hệ thống không làm được điều này: độ sáng tối của Mica đi theo chủ đề hệ thống,
    /// dưới hệ thống tối không lấy được bảng sáng (đo thực tế chỉ có <c>#383838</c>). Sắc độ của Acrylic do ta truyền vào,
    /// nên chủ đề sáng trên hệ thống tối vẫn giữ được bảng sáng.
    /// </remarks>
    public Color AcrylicTint { get; }

    /// <summary>Độ đục của lớp sắc độ.</summary>
    public byte AcrylicTintAlpha { get; }

    /// <summary>Đường viền sáng mảnh vẽ cho bảng; cảm giác kính chủ yếu dựa vào nó.</summary>
    public Color PanelBorder { get; }

    public Color Foreground { get; }

    public Color SecondaryForeground { get; }

    public SolidColorBrush AccentBrush => CreateFrozenBrush(Accent);

    public Brush PanelBorderBrush => CreateBorderBrush();

    public SolidColorBrush ForegroundBrush => CreateFrozenBrush(Foreground);

    public SolidColorBrush SecondaryForegroundBrush => CreateFrozenBrush(SecondaryForeground);

    /// <summary>Lấy lớp tô phù hợp cần dùng.</summary>
    /// <param name="acrylicApplied">Có thực sự nhận được độ mờ nền hay không.</param>
    public Brush ResolvePanelBrush(bool acrylicApplied) => CreatePanelBrush(acrylicApplied);

    /// <summary>Khởi tạo một bộ bảng màu.</summary>
    /// <param name="isDark">Chế độ sáng/tối hiệu lực cuối cùng; khi theo hệ thống thì đã được phân giải xong.</param>
    /// <param name="accent">Màu nhấn trong phần cài đặt.</param>
    /// <param name="opaquePanels">
    /// Người dùng yêu cầu giảm độ trong suốt, hoặc hệ thống đã tắt hiệu ứng trong suốt. Lúc này không còn xuyên thấu, cả hai bộ lớp tô đều chuyển về gần như màu đặc.
    /// </param>
    public static GlassPalette Create(bool isDark, Color accent, bool opaquePanels)
    {
        if (isDark)
        {
            return new GlassPalette(
                isDark: true,
                accent: accent,
                // Phía trên Acrylic chỉ giữ lại một lớp trắng rất nhạt, tạo chút độ bóng cho bảng mà không che mất độ mờ.
                acrylicOverlay: Color.FromArgb(opaquePanels ? (byte)0x00 : (byte)0x0A, 0xFF, 0xFF, 0xFF),
                // Bảng lồng nhau giữ mỏng nhẹ; hiệu ứng khúc xạ và cảm giác khối do bề mặt quang học dùng chung đảm nhận.
                fallbackPanel: opaquePanels
                    ? Color.FromArgb(0xF4, 0x1C, 0x1D, 0x24)
                    : Color.FromArgb(0x12, 0x1C, 0x1E, 0x26),
                acrylicTint: Color.FromRgb(0x1A, 0x1A, 0x1F),
                acrylicTintAlpha: 0x18,
                panelBorder: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
                foreground: Color.FromRgb(0xF2, 0xF2, 0xF7),
                secondaryForeground: Color.FromArgb(0xB3, 0xF2, 0xF2, 0xF7));
        }

        return new GlassPalette(
            isDark: false,
            accent: accent,
            acrylicOverlay: Color.FromArgb(opaquePanels ? (byte)0x00 : (byte)0x14, 0xFF, 0xFF, 0xFF),
            fallbackPanel: opaquePanels
                ? Color.FromArgb(0xF4, 0xF7, 0xF8, 0xFC)
                : Color.FromArgb(0x14, 0xF0, 0xF4, 0xFC),
            acrylicTint: Color.FromRgb(0xF5, 0xF5, 0xF8),
            // Sắc độ tương thích cho chất liệu hệ thống cũ cũng giữ mỏng nhẹ, tránh chồng lớp phủ xám mờ.
            acrylicTintAlpha: 0x20,
            panelBorder: Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF),
            foreground: Color.FromRgb(0x1C, 0x1C, 0x1E),
            secondaryForeground: Color.FromArgb(0xB3, 0x1C, 0x1C, 0x1E));
    }

    /// <summary>
    /// Phân tích <c>#RRGGBB</c> hoặc <c>#AARRGGBB</c>, nếu thất bại thì quay về màu nhấn mặc định,
    /// tránh việc cấu hình bị sửa hỏng thủ công khiến giao diện hoàn toàn không có màu dùng được.
    /// </summary>
    public static Color ParseAccent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultAccent;
        }

        try
        {
            if (ColorConverter.ConvertFromString(value.Trim()) is Color color)
            {
                return color;
            }
        }
        catch (FormatException)
        {
            // Rơi về giá trị mặc định.
        }
        catch (NotSupportedException)
        {
            // Rơi về giá trị mặc định.
        }

        return DefaultAccent;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        // Cọ vẽ đã đóng băng giúp kết xuất tiết kiệm hơn và có thể chia sẻ an toàn giữa các luồng.
        brush.Freeze();
        return brush;
    }

    private LinearGradientBrush CreatePanelBrush(bool acrylicApplied)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };

        if (acrylicApplied)
        {
            var top = IsDark ? (byte)0x1C : (byte)0x32;
            var middle = IsDark ? (byte)0x08 : (byte)0x16;
            var bottom = IsDark ? (byte)0x13 : (byte)0x22;
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(top, 0xFF, 0xFF, 0xFF), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(middle, 0xFF, 0xFF, 0xFF), 0.52));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(bottom, Accent.R, Accent.G, Accent.B), 1));
        }
        else
        {
            var top = FallbackPanelBackground;
            var bottom = IsDark
                ? Color.FromArgb(top.A, 0x29, 0x2C, 0x36)
                : Color.FromArgb(top.A, 0xE7, 0xEC, 0xF5);
            brush.GradientStops.Add(new GradientStop(top, 0));
            brush.GradientStops.Add(new GradientStop(bottom, 1));
        }

        brush.Freeze();
        return brush;
    }

    private LinearGradientBrush CreateBorderBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(IsDark ? (byte)0x85 : (byte)0xA0, 0xFF, 0xFF, 0xFF), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(IsDark ? (byte)0x10 : (byte)0x20, 0xFF, 0xFF, 0xFF), 0.4));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(IsDark ? (byte)0x08 : (byte)0x15, 0xFF, 0xFF, 0xFF), 0.6));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(IsDark ? (byte)0x55 : (byte)0x70, 0xFF, 0xFF, 0xFF), 1.0));
        brush.Freeze();
        return brush;
    }
}
