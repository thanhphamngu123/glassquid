using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Glassquid.Core.Imaging;

namespace Glassquid.Core.Tests;

/// <summary>
/// Kiểm thử cho bộ mã hóa PNG.
/// </summary>
/// <remarks>
/// Bộ mã hóa này được viết thủ công — sản phẩm không dùng thư viện ảnh bên thứ ba, nhưng biểu tượng cần lưu dưới dạng PNG.
/// Lỗi phổ biến nhất khi tự viết định dạng nhị phân là “tệp đã được ghi ra và kích thước đúng, nhưng nội dung không đúng chuẩn”,
/// vì vậy mỗi bài kiểm thử ở đây không chỉ kiểm tra “tệp có tồn tại”, mà giải nén ngược các byte để đối chiếu:
/// cấu trúc chunk, CRC, byte lọc ở đầu mỗi dòng và thứ tự kênh từ BGRA sang RGBA.
///
/// <para>
/// Việc giải nén sử dụng <see cref="ZLibStream"/> tích hợp sẵn của .NET — cùng triển khai với bộ mã hóa —
/// nên chưa thể chứng minh “chương trình khác cũng đọc được”. Việc đó do <c>--verify-icons</c> trong sản phẩm đảm nhận,
/// bằng cách dùng bộ giải mã ảnh của hệ thống (WIC) để đọc tệp thực tế.
/// Hai phần bổ trợ cho nhau: ở đây kiểm chứng từng byte do chính mã nguồn ghi ra, còn ở đó xác minh tệp được trình đọc độc lập chấp nhận.
/// </para>
/// </remarks>
public class PngWriterTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void HeaderHasStandardEightByteSignature()
    {
        var png = PngWriter.EncodeBgra(new byte[4], 1, 1);

        Assert.Equal(PngSignature, png.Take(8));
    }

    [Fact]
    public void ChunkStructureAndLengthsAreValid()
    {
        var png = PngWriter.EncodeBgra(new byte[16], 2, 2);
        var chunks = ReadChunks(png);

        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(chunk => chunk.Type));
        Assert.Equal(13, chunks[0].Length);
        Assert.Equal(0, chunks[2].Length);

        // Trường độ dài phải khớp khi kiểm tra ngược lại, nếu không tệp sẽ thất bại ở bất kỳ bước kiểm tra độ dài nào.
        foreach (var (length, type, data, offset) in chunks)
        {
            Assert.Equal(data.Length, length);
            Assert.Equal(length, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset)));
            Assert.Equal(Encoding.ASCII.GetBytes(type), png.Skip(offset + 4).Take(4));
        }
    }

    [Fact]
    public void EveryChunkCrcMatches()
    {
        var png = PngWriter.EncodeBgra(RandomPixels(7, 5), 7, 5);

        foreach (var (_, _, _, offset) in ReadChunks(png))
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));

            // CRC bao phủ phần loại chunk và dữ liệu, không bao gồm trường độ dài.
            var expected = ComputeCrc(png.AsSpan(offset + 4, 4 + length));

            Assert.Equal(expected, stored);
        }
    }

    [Fact]
    public void IhdrDimensionsAndColorTypeAreCorrect()
    {
        var png = PngWriter.EncodeBgra(new byte[300 * 200 * 4], 300, 200);
        var header = ReadChunks(png)[0].Data;

        Assert.Equal(300, BinaryPrimitives.ReadInt32BigEndian(header));
        Assert.Equal(200, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)));
        Assert.Equal(8, header[8]);
        Assert.Equal(6, header[9]);
        Assert.Equal(0, header[10]);
        Assert.Equal(0, header[11]);
        Assert.Equal(0, header[12]);
    }

    [Fact]
    public void ScanlinesDecodeAndConvertBgraToRgba()
    {
        // Một ảnh 3×1 với ba điểm ảnh lần lượt là đỏ, lục, lam (theo thứ tự BGRA).
        byte[] bgra =
        [
            0x00, 0x00, 0xFF, 0xFF,  // Lam=00 Lục=00 Đỏ=FF -> Đỏ
            0x00, 0xFF, 0x00, 0xFF,  // -> Lục
            0xFF, 0x00, 0x00, 0xFF,  // -> Lam
        ];

        var png = PngWriter.EncodeBgra(bgra, 3, 1);
        var raw = InflateImageData(png);

        // Đầu mỗi dòng có một byte lọc, ở đây luôn là 0 (None).
        Assert.Equal(1 + (3 * 4), raw.Length);
        Assert.Equal(0, raw[0]);

        Assert.Equal([0xFF, 0x00, 0x00, 0xFF], raw[1..5]);
        Assert.Equal([0x00, 0xFF, 0x00, 0xFF], raw[5..9]);
        Assert.Equal([0x00, 0x00, 0xFF, 0xFF], raw[9..13]);
    }

    [Fact]
    public void EachRowHasItsOwnFilterByte()
    {
        var png = PngWriter.EncodeBgra(RandomPixels(4, 3), 4, 3);
        var raw = InflateImageData(png);

        var stride = 1 + (4 * 4);
        Assert.Equal(stride * 3, raw.Length);

        for (var y = 0; y < 3; y++)
        {
            Assert.Equal(0, raw[y * stride]);
        }
    }

    [Fact]
    public void PixelDataIsPreservedExactly()
    {
        var pixels = RandomPixels(16, 12);
        var png = PngWriter.EncodeBgra(pixels, 16, 12);
        var raw = InflateImageData(png);

        var stride = 1 + (16 * 4);

        for (var y = 0; y < 12; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var from = ((y * 16) + x) * 4;
                var to = (y * stride) + 1 + (x * 4);

                Assert.Equal(pixels[from + 2], raw[to]);
                Assert.Equal(pixels[from + 1], raw[to + 1]);
                Assert.Equal(pixels[from], raw[to + 2]);
                Assert.Equal(pixels[from + 3], raw[to + 3]);
            }
        }
    }

    [Fact]
    public void FullyTransparentImageCanBeEncoded()
    {
        var png = PngWriter.EncodeBgra(new byte[64], 4, 4);
        var raw = InflateImageData(png);

        Assert.All(raw, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(-1, 5)]
    public void RejectsInvalidWidthOrHeight(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PngWriter.EncodeBgra(new byte[4], width, height));
    }

    [Fact]
    public void RejectsMismatchedPixelCountAndDimensions()
    {
        // Thiếu một điểm ảnh: nếu không từ chối, ảnh ghi ra sẽ có chiều rộng đúng nhưng dòng cuối bị thiếu một đoạn.
        var pixels = new byte[(4 * 4 * 4) - 4];

        var error = Assert.Throws<ArgumentException>(() => PngWriter.EncodeBgra(pixels, 4, 4));
        Assert.Contains("Độ dài dữ liệu điểm ảnh", error.Message);
    }

    private static byte[] RandomPixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        new Random(width * 1000 + height).NextBytes(pixels);
        return pixels;
    }

    private static byte[] InflateImageData(byte[] png)
    {
        var idat = ReadChunks(png).First(chunk => chunk.Type == "IDAT").Data;

        using var input = new MemoryStream(idat);
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        inflate.CopyTo(output);
        return output.ToArray();
    }

    private static List<(int Length, string Type, byte[] Data, int Offset)> ReadChunks(byte[] png)
    {
        var chunks = new List<(int, string, byte[], int)>();
        var position = 8;

        while (position + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            var type = Encoding.ASCII.GetString(png, position + 4, 4);
            var data = png.Skip(position + 8).Take(length).ToArray();

            chunks.Add((length, type, data, position));

            position += 12 + length;

            if (type == "IEND")
            {
                break;
            }
        }

        return chunks;
    }

    /// <summary>Triển khai độc lập thuật toán CRC trong kiểm thử để tránh dùng chung với mã nguồn sản phẩm và che khuất lỗi lẫn nhau.</summary>
    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}