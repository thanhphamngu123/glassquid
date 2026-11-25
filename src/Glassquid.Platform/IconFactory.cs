using System.Runtime.InteropServices;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform;

/// <summary>
/// Tạo handle biểu tượng Windows từ dữ liệu pixel thô do chính ứng dụng kết xuất.
/// </summary>
/// <remarks>
/// Nhận trực tiếp các byte BGRA thay vì bitmap WPF để assembly này không phụ thuộc bất kỳ
/// framework UI nào; cách tạo đồ họa do bên gọi quyết định.
///
/// Mỗi handle trả về ở đây đều thuộc quyền sở hữu của bên gọi và phải được giải phóng bằng
/// <see cref="DestroyIcon"/>.
/// </remarks>
public static class IconFactory
{
    /// <summary>Tạo biểu tượng 32-bit có kênh alpha từ các pixel BGRA theo thứ tự từ trên xuống dưới.</summary>
    /// <param name="bgra">Các pixel sắp xếp theo thứ tự BGRA, hàng đầu tiên ở trên cùng, mỗi pixel 4 byte.</param>
    public static nint CreateIconFromBgra(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var expected = width * height * 4;
        if (bgra.Length < expected)
        {
            throw new ArgumentException($"Độ dài dữ liệu pixel không đủ: cần {expected} byte, thực tế {bgra.Length} byte.", nameof(bgra));
        }

        nint colorBitmap = 0;
        nint maskBitmap = 0;
        var deviceContext = Gdi32.GetDC(0);

        try
        {
            // Chiều cao âm biểu thị bitmap từ trên xuống dưới, khớp với thứ tự dữ liệu của chúng ta.
            var header = new BitmapV5Header
            {
                Size = Marshal.SizeOf<BitmapV5Header>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = Gdi32.BiBitfields,
                SizeImage = expected,
                RedMask = 0x00FF0000,
                GreenMask = 0x0000FF00,
                BlueMask = 0x000000FF,
                AlphaMask = 0xFF000000,
            };

            colorBitmap = Gdi32.CreateDibSection(deviceContext, ref header, Gdi32.DibRgbColors, out var bits, 0, 0);
            if (colorBitmap == 0 || bits == 0)
            {
                return 0;
            }

            Marshal.Copy(bgra, 0, bits, expected);

            // Mặt nạ 1bpp toàn số 0 biểu thị "không che bất kỳ pixel nào"; độ trong suốt do kênh alpha đảm nhiệm.
            var maskStride = ((width + 31) / 32) * 4;
            maskBitmap = Gdi32.CreateBitmap(width, height, 1, 1, new byte[maskStride * height]);
            if (maskBitmap == 0)
            {
                return 0;
            }

            var info = new IconInfo
            {
                IsIcon = 1,
                MaskBitmap = maskBitmap,
                ColorBitmap = colorBitmap,
            };

            return Gdi32.CreateIconIndirect(ref info);
        }
        finally
        {
            if (deviceContext != 0)
            {
                // Nếu giải phóng DC thất bại ở đây thì cũng không có hành động khắc phục nào; lúc này biểu tượng hoặc đã được tạo xong,
                // hoặc lỗi đã được báo ra thông qua handle rỗng.
                _ = Gdi32.ReleaseDC(0, deviceContext);
            }

            // CreateIconIndirect sẽ sao chép cả hai bitmap nên có thể giải phóng ngay khi hàm trả về.
            // Nếu không giải phóng tại đây, mỗi lần tạo lại biểu tượng sẽ bị rò rỉ bộ nhớ.
            if (colorBitmap != 0)
            {
                Gdi32.DeleteObject(colorBitmap);
            }

            if (maskBitmap != 0)
            {
                Gdi32.DeleteObject(maskBitmap);
            }
        }
    }

    /// <summary>Giải phóng handle biểu tượng được tạo bởi <see cref="CreateIconFromBgra"/>.</summary>
    public static void DestroyIcon(nint icon)
    {
        if (icon != 0)
        {
            User32.DestroyIcon(icon);
        }
    }
}