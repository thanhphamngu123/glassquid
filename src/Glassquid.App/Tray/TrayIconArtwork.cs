using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glassquid.Platform;

namespace Glassquid.App.Tray;

/// <summary>Renders the shared Glassquid brand artwork for windows and the tray.</summary>
internal static class TrayIconArtwork
{
    /// <summary>Kết xuất biểu trưng và trả về handle biểu tượng. Bên gọi chịu trách nhiệm giải phóng handle.</summary>
    public static nint CreateHandle(int size)
    {
        var bitmap = Render(size);
        var pixels = ToStraightBgra(bitmap);
        return IconFactory.CreateIconFromBgra(pixels, size, size);
    }

    /// <summary>Renders the same mark as a WPF image for application windows.</summary>
    public static BitmapSource RenderBitmap(int size) => Render(size);

    private static RenderTargetBitmap Render(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var mark = Application.Current?.TryFindResource("GlassquidBrandMark") as DrawingImage
            ?? throw new InvalidOperationException("The Glassquid brand mark resource is unavailable.");
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var context = visual.RenderOpen())
        {
            var scale = size / 256.0;
            context.PushTransform(new ScaleTransform(scale, scale));
            context.DrawDrawing(mark.Drawing);
            context.Pop();
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// Chuyển đổi sang định dạng BGRA thẳng (không nhân trước alpha) mà bitmap biểu tượng yêu cầu.
    /// </summary>
    private static byte[] ToStraightBgra(BitmapSource source)
    {
        // Bgra32 là định dạng alpha thẳng; việc chuyển đổi từ đích kết xuất đã nhân trước alpha
        // sẽ tự động bỏ phần nhân trước đó cho chúng ta.
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
