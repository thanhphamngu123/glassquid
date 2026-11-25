using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>Một biểu tượng đã trích xuất: các pixel BGRA theo thứ tự từ trên xuống dưới.</summary>
/// <param name="Width">Chiều rộng (pixel).</param>
/// <param name="Height">Chiều cao (pixel).</param>
/// <param name="Bgra">4 byte mỗi pixel, hàng đầu tiên là phía trên cùng.</param>
public sealed record ExtractedIcon(int Width, int Height, byte[] Bgra)
{
    /// <summary>Số pixel thực sự có nội dung (alpha khác 0). Trống hoàn toàn nghĩa là ảnh rỗng, không nên lưu đệm.</summary>
    public int VisiblePixelCount
    {
        get
        {
            var count = 0;
            for (var i = 3; i < Bgra.Length; i += 4)
            {
                if (Bgra[i] != 0)
                {
                    count++;
                }
            }

            return count;
        }
    }
}

/// <summary>
/// Trích xuất các pixel biểu tượng từ tệp hoặc vị trí biểu tượng shell.
/// </summary>
/// <remarks>
/// Hạ cấp 3 tầng, theo thứ tự độ phân giải thu được từ cao xuống thấp:
/// <list type="number">
/// <item><c>IShellItemImageFactory</c> — thực nghiệm cho 256×256, kèm kênh alpha thật. Không bị mờ trên DPI cao, là lựa chọn ưu tiên.</item>
/// <item><c>ExtractIconEx</c> — lấy theo chỉ mục, dùng cho trường hợp vị trí biểu tượng lối tắt được ghi dưới dạng
/// <c>shell32.dll,3</c> kiểu "biểu tượng thứ N trong một tệp". Thực nghiệm cho 32×32.</item>
/// <item><c>SHGetFileInfo</c> — dự phòng theo phần mở rộng, vẫn xuất được ảnh ngay cả khi đích không tồn tại. Thực nghiệm cho 32×32.</item>
/// </list>
///
/// <para>
/// Tầng nào thất bại thì rơi xuống tầng kế tiếp, thất bại hết thì trả về null. Không lấy được biểu tượng chỉ là giao diện thiếu một hình,
/// không được làm hỏng quá trình nhập.
/// </para>
/// </remarks>
public static class IconExtractor
{
    /// <summary>Độ dài cạnh yêu cầu từ <c>IShellItemImageFactory</c>. Đủ cho thanh dock dùng ở tỷ lệ thu phóng 200%.</summary>
    public const int PreferredSize = 256;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>
    /// Lấy biểu tượng từ <paramref name="iconSource"/>.
    /// </summary>
    /// <param name="iconSource">
    /// Đường dẫn tệp, hoặc dạng <c>đường dẫn,chỉ mục</c>. Chỉ mục có thể là số âm — biểu thị ID tài nguyên trong DLL,
    /// <c>ExtractIconEx</c> chấp nhận cách ghi này, chúng ta truyền nguyên vẹn cho nó.
    /// </param>
    public static ExtractedIcon? Extract(string? iconSource)
    {
        if (string.IsNullOrWhiteSpace(iconSource))
        {
            return null;
        }

        var (path, index) = SplitIconSource(iconSource);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.Contains("ImmersiveControlPanel", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("SystemSettings.exe", StringComparison.OrdinalIgnoreCase))
        {
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var win11Logo = Path.Combine(winDir, "ImmersiveControlPanel", "images", "logo.targetsize-256.png");
            if (File.Exists(win11Logo))
            {
                var fluentLogo = ExtractByShellItem(win11Logo, PreferredSize, iconOnly: false);
                if (fluentLogo is { VisiblePixelCount: > 0 })
                {
                    return fluentLogo;
                }
            }
        }

        // Khi có chỉ mục rõ ràng thì lấy theo chỉ mục trước: đường dẫn đó chỉ đúng biểu tượng mà người dùng nhìn thấy,
        // còn IShellItemImageFactory sẽ chỉ trả về biểu tượng thứ 0 của tệp này.
        if (index is not null || Path.GetExtension(path).Length == 0)
        {
            var byIndex = ExtractResource(path, index ?? 0) ?? ExtractByIndex(path, index ?? 0);
            if (byIndex is { VisiblePixelCount: > 0 })
            {
                return byIndex;
            }
        }

        return ExtractByShellItem(path, PreferredSize)
            ?? ExtractByIndex(path, 0)
            ?? ExtractByFileInfo(path);
    }

    private static ExtractedIcon? ExtractResource(string path, int index)
    {
        nint icon = 0;

        try
        {
            var result = Shell32.SHDefExtractIcon(path, index, 0, out icon, 0, PreferredSize);
            return result == Ole32.SoK && icon != 0 ? FromIconHandle(icon) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (icon != 0)
            {
                _ = User32.DestroyIcon(icon);
            }
        }
    }

    /// <summary>
    /// Tách <c>đường dẫn,chỉ mục</c>.
    /// </summary>
    /// <remarks>
    /// Phải tách từ dấu phẩy **cuối cùng**, vì tên thư mục có thể chứa dấu phẩy (thực nghiệm <c>D:\a,b\app.exe</c>
    /// là đường dẫn hợp lệ). Chỉ coi phần sau dấu phẩy là chỉ mục khi toàn bộ phần đó là số nguyên.
    /// </remarks>
    public static (string Path, int? Index) SplitIconSource(string iconSource)
    {
        var trimmed = iconSource.Trim();
        var comma = trimmed.LastIndexOf(',');

        if (comma <= 0 || comma == trimmed.Length - 1)
        {
            return (trimmed, null);
        }

        var tail = trimmed[(comma + 1)..].Trim();
        if (int.TryParse(tail, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            return (trimmed[..comma].Trim(), index);
        }

        return (trimmed, null);
    }

    /// <summary>Lộ trình ưu tiên: yêu cầu shell cung cấp một biểu tượng theo kích thước chỉ định.</summary>
    private static ExtractedIcon? ExtractByShellItem(string path, int size, bool iconOnly = true)
    {
        nint instancePointer = 0;
        IShellItemImageFactory? factory = null;

        try
        {
            var hr = Shell32.SHCreateItemFromParsingName(path, 0, in ShellComIds.ShellItemImageFactory, out instancePointer);
            if (hr != Ole32.SoK || instancePointer == 0)
            {
                return null;
            }

            factory = (IShellItemImageFactory)Wrappers.GetOrCreateObjectForComInstance(instancePointer, CreateObjectFlags.UniqueInstance);

            // SIZE phải truyền nguyên khối; tách thành hai int sẽ làm toàn bộ tham số phía sau bị lệch (thực nghiệm lỗi 0xC0000005).
            var flags = (iconOnly ? Siigbf.IconOnly : 0) | Siigbf.BiggerSizeOk;

            var imageHr = factory.GetImage(new Size { Width = size, Height = size }, flags, out var bitmap);
            if (imageHr != Ole32.SoK || bitmap == 0)
            {
                return null;
            }

            try
            {
                return ReadBitmapPixels(bitmap);
            }
            finally
            {
                _ = Gdi32.DeleteObject(bitmap);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Bắt tất cả ngoại lệ, cùng lý do như ở ShortcutResolver: tầng COM sẽ ánh xạ HRESULT thất bại
            // thành đủ loại ngoại lệ BCL, liệt kê từng kiểu chắc chắn sẽ sót, mà sót một kiểu sẽ biến
            // việc lấy biểu tượng thất bại thành nhập thất bại. Không lấy được biểu tượng chỉ là thiếu một hình.
            _ = ex;
            return null;
        }
        finally
        {
            if (factory is IDisposable disposable)
            {
                disposable.Dispose();
            }

            if (instancePointer != 0)
            {
                Marshal.Release(instancePointer);
            }
        }
    }

    /// <summary>Lấy biểu tượng từ exe/dll theo chỉ mục. Khi chỉ mục âm thì biểu thị ID tài nguyên.</summary>
    private static ExtractedIcon? ExtractByIndex(string path, int index)
    {
        nint large = 0;
        nint small = 0;

        try
        {
            var count = Shell32.ExtractIconEx(path, index, out large, out small, 1);

            return count == 0 || large == 0 ? null : FromIconHandle(large);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (large != 0)
            {
                _ = User32.DestroyIcon(large);
            }

            if (small != 0)
            {
                _ = User32.DestroyIcon(small);
            }
        }
    }

    /// <summary>Dự phòng: lấy biểu tượng hệ thống theo loại tệp. Vẫn xuất được ảnh khi đích không tồn tại.</summary>
    private static ExtractedIcon? ExtractByFileInfo(string path)
    {
        try
        {
            var info = new Shell32.ShellFileInfo();
            var flags = Shell32.ShgfiIcon | Shell32.ShgfiLargeIcon;

            // Khi đích không tồn tại thì suy luận theo phần mở rộng, nếu không sẽ trả về biểu tượng chung "tệp không xác định".
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                flags |= Shell32.ShgfiUseFileAttributes;
            }

            var result = Shell32.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<Shell32.ShellFileInfo>(), flags);
            if (result == 0 || info.Icon == 0)
            {
                return null;
            }

            try
            {
                return FromIconHandle(info.Icon);
            }
            finally
            {
                _ = User32.DestroyIcon(info.Icon);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Đọc một <c>HICON</c> thành các pixel BGRA.</summary>
    private static ExtractedIcon? FromIconHandle(nint icon)
    {
        if (icon == 0 || !User32.GetIconInfo(icon, out var info))
        {
            return null;
        }

        try
        {
            if (info.ColorBitmap == 0)
            {
                return null;
            }

            return ReadBitmapPixels(info.ColorBitmap);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            // GetIconInfo sao chép ra hai bitmap thuộc quyền sở hữu của bên gọi, phải xóa tường minh,
            // nếu không mỗi lần lấy biểu tượng sẽ rò rỉ một cặp đối tượng GDI.
            if (info.ColorBitmap != 0)
            {
                _ = Gdi32.DeleteObject(info.ColorBitmap);
            }

            if (info.MaskBitmap != 0)
            {
                _ = Gdi32.DeleteObject(info.MaskBitmap);
            }
        }
    }

    /// <summary>
    /// Đọc BGRA từ bitmap.
    /// </summary>
    /// <remarks>
    /// Thực nghiệm cho thấy kênh alpha do shell trả về là <strong>alpha thẳng (straight)</strong>, không phải nhân trước (premultiplied):
    /// trong kết quả của <c>GetImage</c> có 6215 pixel có một kênh màu lớn hơn alpha,
    /// điều không thể xảy ra khi nhân trước. Vì vậy không cần khử nhân trước khi ghi PNG.
    /// </remarks>
    private static unsafe ExtractedIcon? ReadBitmapPixels(nint bitmap)
    {
        if (bitmap == 0 || Gdi32.GetObject(bitmap, Marshal.SizeOf<Bitmap>(), out var source) == 0)
        {
            return null;
        }

        if (source.Width <= 0 || source.Height <= 0 || source.Width > 4096 || source.Height > 4096)
        {
            return null;
        }

        var width = source.Width;
        var height = source.Height;
        var pixels = new byte[width * height * 4];

        // Chiều cao lấy số âm = từ trên xuống dưới, đọc ra là đúng thứ tự hàng của PNG luôn.
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        var info = new BitmapInfo { Header = header };
        var deviceContext = Gdi32.GetDC(0);

        if (deviceContext == 0)
        {
            return null;
        }

        try
        {
            fixed (byte* destination = pixels)
            {
                if (Gdi32.GetDIBits(deviceContext, bitmap, 0, (uint)height, destination, &info) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            _ = Gdi32.ReleaseDC(0, deviceContext);
        }

        return new ExtractedIcon(width, height, pixels);
    }
}
