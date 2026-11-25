using System.Security.Cryptography;
using System.Text;
using Glassquid.Core.Imaging;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices;

/// <summary>Kết quả của một lần ghi biểu tượng xuống đĩa.</summary>
/// <param name="Path">Đường dẫn tuyệt đối của tệp bộ nhớ đệm.</param>
/// <param name="Width">Chiều rộng biểu tượng.</param>
/// <param name="Height">Chiều cao biểu tượng.</param>
/// <param name="CreatedFile">Tệp mới đã được tạo lần này; false nghĩa là đã tái sử dụng tệp sẵn có.</param>
public sealed record CachedIcon(string Path, int Width, int Height, bool CreatedFile);

/// <summary>Kết quả của việc xóa thư mục bộ nhớ đệm.</summary>
/// <param name="Removed">Số tệp bộ nhớ đệm có thể tái tạo đã bị xóa.</param>
/// <param name="LeftAlone">Số tệp không thuộc bộ nhớ đệm nên được giữ lại.</param>
public sealed record IconCacheClearResult(int Removed, int LeftAlone)
{
    /// <summary>Trong thư mục còn có thứ khác ngoài bộ nhớ đệm của chúng ta, đáng để thông báo cho người dùng.</summary>
    public bool LeftOthers => LeftAlone > 0;
}

/// <summary>
/// Ghi biểu tượng đã trích xuất dưới dạng PNG vào thư mục bộ nhớ đệm.
/// </summary>
/// <remarks>
/// Tên tệp bộ nhớ đệm được quyết định bởi hàm băm của <strong>nguồn biểu tượng</strong>, chứ không phải bởi id mục.
/// Nhờ vậy cùng một nguồn chỉ ghi ra một tệp: hàng chục lối tắt trong menu Start dùng chung một đích
/// chỉ ghi một lần, và cùng một chương trình trên màn hình nền được nhập hai lần cũng chỉ ghi một lần.
/// Nếu đặt tên theo id mục sẽ tạo ra hàng chục tệp PNG giống hệt nhau từng byte.
///
/// <para>
/// Lưu dưới dạng PNG thay vì BGRA thô: tệp nhỏ hơn một bậc độ lớn và là định dạng chuẩn có thể mở xem trực tiếp,
/// khi kiểm tra «tại sao biểu tượng không đúng» sẽ không cần viết thêm công cụ.
/// </para>
/// </remarks>
public sealed class IconCache
{
    // Cập nhật phiên bản này khi thay đổi cách xử lý biểu tượng để tránh tiếp tục tái sử dụng bộ nhớ đệm PNG kích thước cũ.
    private const string CacheKeyVersion = "installer-resource-v3";
    private readonly AppPaths _paths;

    public IconCache(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>
    /// Số lượng tệp bộ nhớ đệm <strong>có thể tái tạo</strong> trong thư mục, tức loại mà <see cref="Clear"/> sẽ xóa.
    /// </summary>
    /// <remarks>
    /// Cố ý không tính biểu tượng tùy chỉnh: chúng không phải bộ nhớ đệm. Trong báo cáo, «thư mục bộ nhớ đệm hiện có N tệp»
    /// là nói đến con số này, nếu không sau khi dọn dẹp con số không về 0, người đọc báo cáo sẽ tưởng việc dọn dẹp không có hiệu lực.
    /// </remarks>
    public int FileCount => Directory.Exists(_paths.IconCacheDirectory)
        ? Directory.GetFiles(_paths.IconCacheDirectory).Count(IsRegenerableCacheFile)
        : 0;

    /// <summary>
    /// Lấy (nếu cần thì trích xuất trước rồi ghi) tệp bộ nhớ đệm theo nguồn biểu tượng.
    /// </summary>
    /// <returns>Trả về null khi không lấy được biểu tượng — đây chỉ là giao diện thiếu một hình ảnh, không phải lỗi.</returns>
    public CachedIcon? EnsureFor(string? iconSource)
    {
        if (string.IsNullOrWhiteSpace(iconSource))
        {
            return null;
        }

        var target = PathFor(iconSource);

        if (IsWindowsSettingsSource(iconSource))
        {
            var win11Logo = ResolveWindows11SettingsIconPath();
            if (win11Logo != null && File.Exists(win11Logo))
            {
                try
                {
                    WriteAtomically(target, File.ReadAllBytes(win11Logo));
                    var (w, h) = ReadPngSize(target);
                    return new CachedIcon(target, w > 0 ? w : 256, h > 0 ? h : 256, CreatedFile: true);
                }
                catch
                {
                    // Fall back
                }
            }
        }

        // Đã lưu đệm rồi thì tái sử dụng trực tiếp. Việc kiểm tra «ảnh có rỗng không» chỉ làm với tệp mới tạo,
        // nếu không mỗi lần gọi đều phải đọc lại toàn bộ tệp PNG.
        if (File.Exists(target))
        {
            var (width, height) = ReadPngSize(target);
            return width > 0 ? new CachedIcon(target, width, height, CreatedFile: false) : null;
        }

        var extracted = IconExtractor.Extract(iconSource);
        if (extracted is null || extracted.VisiblePixelCount == 0)
        {
            // Không nên lưu đệm ảnh trong suốt hoàn toàn: sau khi lưu lại, mọi bản ghi dùng nó sẽ hiển thị thành khoảng trắng,
            // trong khi hành vi đúng là «không có biểu tượng», để giao diện tự quyết định vẽ ảnh giữ chỗ nào.
            return null;
        }

        var normalized = IconCanvasNormalizer.Normalize(extracted.Bgra, extracted.Width, extracted.Height);

        try
        {
            WriteAtomically(target, PngWriter.EncodeBgra(normalized.Bgra, normalized.Width, normalized.Height));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return new CachedIcon(target, normalized.Width, normalized.Height, CreatedFile: true);
    }

    /// <summary>
    /// Sao chép tệp biểu tượng do người dùng tự chọn vào thư mục bộ nhớ đệm.
    /// </summary>
    /// <remarks>
    /// Sao chép thay vì chỉ ghi nhớ đường dẫn người dùng đã chọn: sau này người dùng có thể xóa hoặc di chuyển tệp nguồn,
    /// khi đó chỉ nhớ đường dẫn bên ngoài đồng nghĩa với việc mất biểu tượng. Sau khi sao chép vào,
    /// bản thân thư mục dữ liệu đã đầy đủ và có thể mang đi cùng bản sao lưu.
    ///
    /// <para>
    /// Ở đây chỉ kiểm tra phần đầu tệp, không giải mã toàn bộ — giải mã cần thư viện ảnh, còn sản phẩm không kéo thêm phụ thuộc bên thứ ba.
    /// Định dạng không nhận diện được sẽ bị từ chối ngay, thay vì sao chép một tệp chắc chắn sẽ tải thất bại về sau.
    /// </para>
    /// </remarks>
    /// <returns>Trả về đường dẫn tuyệt đối của tệp bộ nhớ đệm khi sao chép thành công; trả về null khi không nhận diện được định dạng hoặc không đọc được.</returns>
    public string? ImportCustomIcon(string sourcePath, string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        var format = ImageFormatSniffer.Detect(sourcePath);
        if (format is null)
        {
            return null;
        }

        var extension = ImageFormatSniffer.FileExtensionFor(format.Value);

        // Đưa id mục vào tên tệp để cùng một biểu tượng người dùng khi được hai bản ghi chọn sẽ không ghi đè lên nhau.
        // Tiền tố lấy từ AppPaths: việc kiểm tra «đường dẫn này có phải do chúng ta quản lý không» dùng cùng một hằng số,
        // viết riêng ở hai nơi sớm muộn cũng sẽ lệch nhau.
        var target = Path.Combine(
            _paths.IconCacheDirectory,
            AppPaths.ManagedCustomIconPrefix + Sanitize(itemId) + extension);

        try
        {
            Directory.CreateDirectory(_paths.IconCacheDirectory);
            File.Copy(sourcePath, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Đổi định dạng khi chọn lại sẽ để lại tệp có phần mở rộng cũ, hãy xóa nó đi, nếu không thư mục sẽ chất đầy rác không nhìn thấy được.
        foreach (var stale in Directory.GetFiles(_paths.IconCacheDirectory, AppPaths.ManagedCustomIconPrefix + Sanitize(itemId) + ".*"))
        {
            if (!string.Equals(stale, target, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(stale);
            }
        }

        return target;
    }

    /// <summary>Xóa tệp biểu tượng tùy chỉnh đã được một bản ghi sử dụng.</summary>
    public void RemoveCustomIcon(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Directory.Exists(_paths.IconCacheDirectory))
        {
            return;
        }

        foreach (var stale in Directory.GetFiles(_paths.IconCacheDirectory, AppPaths.ManagedCustomIconPrefix + Sanitize(itemId) + ".*"))
        {
            TryDelete(stale);
        }
    }

    /// <summary>
    /// Xóa các tệp bộ nhớ đệm <strong>có thể tái tạo</strong>. <c>IconSource</c> trên mục sẽ được giữ lại nên có thể trích xuất lại.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kiểm tra trước, xóa sau.</b> Phiên bản cũ gọi <see cref="File.Delete"/> trước rồi mới dùng kết quả kiểm tra để đếm,
    /// khiến phép kiểm tra đó thuần túy trở thành thống kê — mọi tệp trong thư mục đều bị xóa sạch,
    /// bao gồm cả tệp do người dùng tự bỏ vào và biểu tượng tùy chỉnh của sản phẩm này.
    /// </para>
    /// <para>
    /// <b>Cố ý không xóa biểu tượng tùy chỉnh (<c>custom-*</c>).</b> Nó không phải bộ nhớ đệm có thể tái tạo:
    /// nó được sao chép từ tệp người dùng chọn, tệp nguồn có thể đã không còn tồn tại từ lâu, xóa rồi sẽ không tìm lại được.
    /// Để gỡ bỏ biểu tượng tùy chỉnh của một bản ghi nào đó, hãy dùng <see cref="RemoveCustomIcon"/>, thao tác đó được thực hiện chính xác theo từng mục.
    /// </para>
    /// </remarks>
    public IconCacheClearResult Clear()
    {
        if (!Directory.Exists(_paths.IconCacheDirectory))
        {
            return new IconCacheClearResult(0, 0);
        }

        var removed = 0;
        var leftAlone = 0;

        foreach (var file in Directory.GetFiles(_paths.IconCacheDirectory))
        {
            if (!IsRegenerableCacheFile(file))
            {
                leftAlone++;
                continue;
            }

            if (TryDelete(file))
            {
                removed++;
            }
            else
            {
                // Giữ lại tệp không xóa được, không ngắt quãng vì lỗi dọn dẹp.
                leftAlone++;
            }
        }

        return new IconCacheClearResult(removed, leftAlone);
    }

    /// <summary>
    /// Có phải là tệp bộ nhớ đệm do <see cref="EnsureFor"/> tạo ra và có thể trích xuất lại theo nguồn biểu tượng hay không.
    /// </summary>
    /// <remarks>
    /// Chỉ nhận dạng kiểu tên «32 chữ số thập lục phân + <c>.png</c>». Yêu cầu từng ký tự phải là chữ số thập lục phân,
    /// thay vì chỉ so độ dài — tránh nhận nhầm thứ khác vô tình có đúng 36 ký tự thành bộ nhớ đệm.
    /// </remarks>
    private static bool IsRegenerableCacheFile(string path)
    {
        var name = Path.GetFileName(path);

        if (name.Length != 36 || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 0; i < 32; i++)
        {
            if (!Uri.IsHexDigit(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Vị trí tệp bộ nhớ đệm tương ứng với một nguồn biểu tượng nào đó.
    /// </summary>
    /// <remarks>
    /// Công khai ra ngoài để câu hỏi «biểu tượng của nguồn này nằm ở đâu» cũng có thể được trả lời bên ngoài bộ nhớ đệm —
    /// cả phần chẩn đoán và bảng liên hệ đều cần hỏi đường dẫn mà không kích hoạt việc trích xuất.
    /// </remarks>
    public string PathFor(string iconSource)
    {
        // Đưa hàm băm của chính nguồn vào mã định danh, nhờ vậy khi «nguồn biểu tượng thay đổi» sẽ tự nhiên chuyển sang tệp mới,
        // không lấy tệp cũ ra dùng bừa.
        var cacheIdentity = $"{CacheKeyVersion}\0{iconSource.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity));
        var name = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        return Path.Combine(_paths.IconCacheDirectory, name + ".png");
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(temporary, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        // Chữ ký PNG dài 8 byte, độ dài IHDR 4 byte, loại 4 byte, tiếp theo là hai số nguyên big-endian.
        Span<byte> header = stackalloc byte[24];

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return (0, 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }

        if (!header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return (0, 0);
        }

        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        return (width, height);
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_');
        }

        return builder.ToString();
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Không xóa được thì giữ nguyên. Việc dọn dẹp thất bại không nên làm ảnh hưởng đến thao tác người dùng đang thực hiện.
            return false;
        }
    }

    /// <summary>Xóa biểu tượng tùy chỉnh không còn cần thiết trên mục và gắn biểu tượng mới vào.</summary>
    public bool ApplyCustomIcon(AppEntry entry, string? customIconPath)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(customIconPath))
        {
            if (entry.CustomIconPath is not null)
            {
                entry.CustomIconPath = null;
                RemoveCustomIcon(entry.Id);
                return true;
            }

            return false;
        }

        var cached = ImportCustomIcon(customIconPath, entry.Id);
        if (cached is null)
        {
            return false;
        }

        entry.CustomIconPath = cached;
        return true;
    }

    private static bool IsWindowsSettingsSource(string? iconSource)
    {
        if (string.IsNullOrWhiteSpace(iconSource)) return false;
        return iconSource.Contains("SystemSettings", StringComparison.OrdinalIgnoreCase)
            || iconSource.Contains("ImmersiveControlPanel", StringComparison.OrdinalIgnoreCase)
            || iconSource.Contains("immersivecontrolcenter", StringComparison.OrdinalIgnoreCase)
            || iconSource.StartsWith("ms-settings", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveWindows11SettingsIconPath()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(winDir, "ImmersiveControlPanel", "images", "logo.targetsize-256.png");
        if (File.Exists(path)) return path;

        path = Path.Combine(winDir, "ImmersiveControlPanel", "images", "logo.scale-400.png");
        if (File.Exists(path)) return path;

        path = Path.Combine(winDir, "ImmersiveControlPanel", "images", "logo.png");
        if (File.Exists(path)) return path;

        return null;
    }
}
