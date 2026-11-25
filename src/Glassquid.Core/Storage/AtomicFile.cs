using System.Text;

namespace Glassquid.Core.Storage;

/// <summary>
/// Khi ghi tệp, đảm bảo bên đọc không bao giờ nhìn thấy tài liệu đang ghi dở dang.
/// </summary>
/// <remarks>
/// Trình tự là: ghi một tệp tạm trong cùng thư mục, đẩy toàn bộ dữ liệu xuống đĩa, rồi dùng
/// <see cref="File.Replace(string, string, string, bool)"/> thay thế vào vị trí đích; lệnh gọi này
/// đồng thời hạ cấp nội dung cũ thành tệp <c>.bak</c> bên cạnh. Do đó sau khi gặp sự cố, thứ còn lại
/// hoặc là tài liệu cũ, hoặc là tài liệu mới——tuyệt đối không phải hỗn hợp bị cắt cụt——và luôn
/// có một bản sao khả dụng được biết gần nhất.
/// </remarks>
public static class AtomicFile
{
    public const string BackupSuffix = ".bak";
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// Ghi <paramref name="contents"/> vào <paramref name="path"/> dưới dạng UTF-8 không có BOM.
    /// </summary>
    /// <exception cref="StorageException">Không thể thay thế tệp.</exception>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + TempSuffix;
        var backupPath = path + BackupSuffix;

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                // Đẩy xuống thiết bị vật lý để khi mất điện đột ngột không để lại
                // một tệp tạm đã tồn tại nhưng rỗng.
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(tempPath);
            throw new StorageException(
                $"Không thể lưu {Path.GetFileName(path)}: {ex.Message}",
                path,
                ex);
        }
    }

    /// <summary>Đọc tệp; trả về null nếu tệp không tồn tại.</summary>
    public static string? ReadAllTextOrNull(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageException(
                $"Không thể đọc {Path.GetFileName(path)}: {ex.Message}",
                path,
                ex);
        }
    }

    /// <summary>
    /// Dời tệp không đọc được sang một bên thay vì xóa đi, để người dùng vẫn có thể tự khôi phục
    /// hoặc gửi báo cáo. Trả về vị trí mới; trả về null nếu không thể di chuyển.
    /// </summary>
    public static string? PreserveCorrupt(string path, DateTimeOffset timestamp)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var stamp = timestamp.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(directory, $"{name}.corrupt-{stamp}{extension}");

        try
        {
            // Tuyệt đối không ghi đè bản sao cứu hộ cũ hơn; hai lần lỗi trong cùng một giây
            // ít khả năng xảy ra, nhưng làm mất bản đầu tiên còn tệ hơn việc tên tệp trông hơi lạ.
            var candidate = target;
            var counter = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{name}.corrupt-{stamp}-{counter}{extension}");
                counter++;
            }

            File.Move(path, candidate);
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Chỉ dọn dẹp; điều thực sự quan trọng là lỗi gốc và lỗi đó đã được báo cáo rồi.
        }
    }
}