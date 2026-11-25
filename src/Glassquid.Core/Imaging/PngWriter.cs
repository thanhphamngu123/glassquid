using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Glassquid.Core.Imaging;

/// <summary>
/// Ghi các pixel BGRA thành tệp PNG.
/// </summary>
/// <remarks>
/// Tự viết thay vì thêm thư viện xử lý ảnh: sản phẩm không phụ thuộc bên thứ ba,
/// mà <see cref="ZLibStream"/> có sẵn trong .NET đã cung cấp luồng deflate mà PNG cần,
/// phần còn lại chỉ là chia chunk, CRC và byte lọc đầu dòng — chỉ vài chục dòng mã tất định,
/// gọn nhẹ hơn việc thêm một thư viện phụ thuộc.
///
/// <para>
/// Đầu vào là <strong>BGRA</strong> (cách sắp xếp của bitmap Win32), PNG yêu cầu <strong>RGBA</strong>,
/// nên mỗi pixel cần hoán đổi R và B. Alpha được sao chép trực tiếp: qua kiểm thử thực tế, alpha do shell cung cấp là dạng thẳng,
/// không cần giải nhân trước.
/// </para>
///
/// <para>
/// Chỉ ghi RGBA 8-bit, không xen kẽ, byte lọc luôn bằng 0 (None). Đây là dạng đơn giản và phổ biến nhất của PNG,
/// mọi bộ giải mã đều hỗ trợ; đối với biểu tượng tối đa chỉ vài trăm pixel, tỷ lệ nén không đáng để đánh đổi thêm độ phức tạp.
/// </para>
/// </remarks>
public static class PngWriter
{
    /// <summary>Phần đầu tệp cố định của PNG.</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Loại màu 6 = Màu thật kèm alpha (RGBA).</summary>
    private const byte ColorTypeRgba = 6;

    /// <summary>Phương thức lọc 0 = Không lọc.</summary>
    private const byte FilterNone = 0;

    /// <summary>Mã hóa các pixel BGRA thành mảng byte PNG.</summary>
    /// <param name="bgra">4 byte mỗi pixel, dòng đầu tiên nằm ở trên cùng; độ dài phải bằng <c>width * height * 4</c>.</param>
    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var expected = checked(width * height * 4);
        if (bgra.Length != expected)
        {
            throw new ArgumentException($"Độ dài dữ liệu pixel phải là {expected} byte, thực tế là {bgra.Length} byte.", nameof(bgra));
        }

        using var output = new MemoryStream(Signature.Length + expected + 128);

        output.Write(Signature);

        WriteHeader(output, width, height);
        WriteImageData(output, bgra, width, height);
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);

        return output.ToArray();
    }

    private static void WriteHeader(Stream output, int width, int height)
    {
        Span<byte> header = stackalloc byte[13];

        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = ColorTypeRgba;
        header[10] = 0;
        header[11] = FilterNone;
        header[12] = 0;

        WriteChunk(output, "IHDR", header);
    }

    private static void WriteImageData(Stream output, ReadOnlySpan<byte> bgra, int width, int height)
    {
        // Thêm một byte lọc ở đầu mỗi dòng.
        var stride = (width * 4) + 1;
        var raw = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            var destination = y * stride;
            raw[destination] = FilterNone;

            var source = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var from = source + (x * 4);
                var to = destination + 1 + (x * 4);

                // BGRA -> RGBA: chỉ hoán đổi hai kênh đầu và cuối.
                raw[to] = bgra[from + 2];
                raw[to + 1] = bgra[from + 1];
                raw[to + 2] = bgra[from];
                raw[to + 3] = bgra[from + 3];
            }
        }

        using var compressed = new MemoryStream(raw.Length / 4 + 64);

        // ZLibStream ghi ra một luồng zlib hoàn chỉnh (gồm phần đầu 2 byte và adler32 ở cuối),
        // đúng định dạng mà IDAT của PNG yêu cầu, không cần tự bổ sung hai phần này.
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        // Loại chunk và dữ liệu được tính CRC nối tiếp nhau, nên không thể chỉ đưa type riêng lẻ vào một đối tượng tăng dần rồi bỏ đi.
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32.Compute(typeBytes, data);

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    /// <summary>
    /// CRC-32 dùng cho PNG: đa thức phản chiếu <c>0xEDB88320</c>, đảo bit cả giá trị đầu và giá trị cuối.
    /// </summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        internal static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var crc = 0xFFFFFFFFu;

            crc = Update(crc, first);
            crc = Update(crc, second);

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];

            for (var i = 0u; i < table.Length; i++)
            {
                var value = i;

                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}