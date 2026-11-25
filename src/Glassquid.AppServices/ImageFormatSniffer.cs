namespace Glassquid.AppServices;

/// <summary>Các định dạng ảnh có thể nhận diện.</summary>
public enum ImageFormat
{
    Png = 0,
    Jpeg = 1,
    Bmp = 2,
    Gif = 3,
    Ico = 4,
}

/// <summary>
/// Xác định định dạng ảnh theo phần đầu tệp.
/// </summary>
/// <remarks>
/// Không dựa vào phần mở rộng: người dùng hoàn toàn có thể lưu một ảnh PNG thành <c>.jpg</c>,
/// hoặc đặt bất kỳ tên nào cho biểu tượng tùy chỉnh. Phần đầu tệp là căn cứ đáng tin cậy duy nhất.
///
/// <para>
/// Chỉ nhận diện định dạng, không giải mã. <strong>Việc xác thực tính hợp lệ bằng cách giải mã đòi hỏi thư viện xử lý ảnh, trong khi sản phẩm không dùng thư viện bên thứ ba</strong>;
/// nhận diện định dạng giúp chặn các lỗi rõ ràng như "kéo một tệp PDF vào", đây chính là điều đầu vào này cần phòng tránh nhất.
/// Khi giải mã thất bại thực sự, giao diện sẽ tự hiển thị ảnh giữ chỗ, không làm tình hình tệ hơn.
/// </para>
/// </remarks>
public static class ImageFormatSniffer
{
    /// <summary>Trả về null nếu không nhận diện được định dạng.</summary>
    public static ImageFormat? Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Đủ số byte để bao phủ chữ ký dài nhất trong các chữ ký bên dưới.
        Span<byte> header = stackalloc byte[16];

        int read;

        try
        {
            using var stream = File.OpenRead(path);
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (read < 4)
        {
            return null;
        }

        var bytes = header[..read];

        if (StartsWith(bytes, PngSignature))
        {
            return ImageFormat.Png;
        }

        if (StartsWith(bytes, JpegSignature))
        {
            return ImageFormat.Jpeg;
        }

        if (StartsWith(bytes, BmpSignature))
        {
            return ImageFormat.Bmp;
        }

        if (StartsWith(bytes, GifSignature))
        {
            return ImageFormat.Gif;
        }

        // ICO bắt đầu bằng 00 00 01 00. CUR là 00 00 02 00; con trỏ chuột không dùng làm biểu tượng, vì vậy chỉ nhận dạng trước.
        if (StartsWith(bytes, IcoSignature))
        {
            return ImageFormat.Ico;
        }

        return null;
    }

    /// <summary>So sánh chuỗi byte, tránh cấp phát mảng chỉ để so sánh vài hằng số.</summary>
    public static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
        => data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> BmpSignature => [0x42, 0x4D];

    private static ReadOnlySpan<byte> GifSignature => [0x47, 0x49, 0x46, 0x38];

    private static ReadOnlySpan<byte> IcoSignature => [0x00, 0x00, 0x01, 0x00];

    /// <summary>Phần mở rộng dùng cho tệp bộ nhớ đệm. Tên phải khớp với định dạng, nếu không khi tải sau này sẽ chọn nhầm bộ giải mã.</summary>
    public static string FileExtensionFor(ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Gif => ".gif",
        ImageFormat.Ico => ".ico",
        _ => ".bin",
    };
}