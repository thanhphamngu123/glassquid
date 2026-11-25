using System.Runtime.InteropServices;

namespace Glassquid.Platform.Interop;

/// <summary>
/// Các điểm vào GDI và biểu tượng dùng để chuyển đổi bitmap đã kết xuất thành <c>HICON</c>.
/// </summary>
/// <remarks>
/// Sản phẩm không đi kèm thư viện biểu tượng của bên thứ ba và cũng không mượn đồ họa có sẵn,
/// nên các dấu hiệu trên khay hệ thống và cửa sổ do chúng tôi tự vẽ rồi giao cho shell thông qua các lệnh gọi này.
/// </remarks>
internal static partial class Gdi32
{
    internal const int DibRgbColors = 0;
    internal const int BiBitfields = 3;

    [LibraryImport("user32.dll")]
    internal static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    internal static partial nint CreateDibSection(nint dc, ref BitmapV5Header header, int usage, out nint bits, nint section, int offset);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[]? bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    internal static partial nint CreateIconIndirect(ref IconInfo iconInfo);

    // ---- Đọc lại pixel từ bitmap (trích xuất biểu tượng ở giai đoạn 3) ----

    internal const int DibRgb = 0;

    /// <summary>
    /// Đọc thông tin phần đầu (header) của một đối tượng GDI.
    /// </summary>
    /// <remarks>
    /// Giá trị trả về là số byte đã ghi, 0 nghĩa là thất bại. Ở đây chỉ dùng để hỏi chiều rộng, chiều cao và độ sâu bit của bitmap——
    /// bản thân <c>GetDIBits</c> sẽ không cho bạn biết kích thước bitmap nguồn.
    /// </remarks>
    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    internal static partial int GetObject(nint handle, int size, out Bitmap target);

    /// <summary>
    /// Sao chép các pixel của bitmap ra ngoài.
    /// </summary>
    /// <remarks>
    /// Khi yêu cầu 32-bit và đặt chiều cao trong header là <strong>số âm</strong>, kết quả thu được là bộ đệm liên tục
    /// theo thứ tự từ trên xuống dưới, mỗi pixel là BGRA, khớp với cách sắp xếp của PNG nên không cần lật lại.
    /// </remarks>
    internal static unsafe int GetDIBits(nint deviceContext, nint bitmap, uint start, uint lines, byte* bits, BitmapInfo* info)
        => GetDIBitsCore(deviceContext, bitmap, start, lines, bits, info, DibRgb);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits")]
    private static unsafe partial int GetDIBitsCore(nint deviceContext, nint bitmap, uint start, uint lines, byte* bits, BitmapInfo* info, int usage);
}

/// <summary><c>BITMAP</c>. Thứ tự trường khớp với cấu trúc gốc, dùng để truy vấn chiều rộng, chiều cao và độ sâu bit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Bitmap
{
    internal int Type;
    internal int Width;
    internal int Height;
    internal int WidthBytes;
    internal ushort Planes;
    internal ushort BitsPerPixel;
    internal nint Bits;
}

/// <summary><c>BITMAPINFOHEADER</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    internal int Size;
    internal int Width;
    internal int Height;
    internal ushort Planes;
    internal ushort BitCount;
    internal int Compression;
    internal int SizeImage;
    internal int XPelsPerMeter;
    internal int YPelsPerMeter;
    internal int ColorsUsed;
    internal int ColorsImportant;
}

/// <summary>
/// <c>BITMAPINFO</c>: một phần đầu cộng một bảng màu.
/// </summary>
/// <remarks>
/// Bảng màu ở đây chỉ giữ chỗ. Chúng ta luôn yêu cầu màu thực 32-bit nên hệ thống sẽ không đọc nó,
/// nhưng kích thước struct vẫn phải bao gồm mục đầu tiên, nếu không <c>GetDIBits</c> sẽ ghi tràn biên khi trả dữ liệu.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfo
{
    internal BitmapInfoHeader Header;
    internal uint Colors;
}

/// <summary>
/// <c>BITMAPV5HEADER</c>. Dùng header phiên bản 5 vì nó có thể mang mặt nạ kênh
/// tường minh, đây chính là lý do bitmap 32-bit có kênh alpha thực hoạt động được.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BitmapV5Header
{
    internal int Size;
    internal int Width;
    internal int Height;
    internal ushort Planes;
    internal ushort BitCount;
    internal int Compression;
    internal int SizeImage;
    internal int XPelsPerMeter;
    internal int YPelsPerMeter;
    internal int ColorsUsed;
    internal int ColorsImportant;

    internal uint RedMask;
    internal uint GreenMask;
    internal uint BlueMask;
    internal uint AlphaMask;

    internal uint CsType;
    internal int Endpoints0;
    internal int Endpoints1;
    internal int Endpoints2;
    internal int Endpoints3;
    internal int Endpoints4;
    internal int Endpoints5;
    internal int Endpoints6;
    internal int Endpoints7;
    internal int Endpoints8;

    internal uint GammaRed;
    internal uint GammaGreen;
    internal uint GammaBlue;

    internal int Intent;
    internal int ProfileData;
    internal int ProfileSize;
    internal int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IconInfo
{
    /// <summary>
    /// Kiểu <c>BOOL</c> của Win32, biểu diễn bằng <see cref="int"/> thay vì
    /// <see cref="bool"/> để giữ cho struct ở dạng blittable. Kiểu bool cần marshaling sẽ buộc
    /// trình marshaler lúc chạy phải kích hoạt, làm mất khả năng dùng P/Invoke sinh từ mã nguồn ở trên.
    /// </summary>
    internal int IsIcon;

    internal int HotspotX;
    internal int HotspotY;
    internal nint MaskBitmap;
    internal nint ColorBitmap;
}